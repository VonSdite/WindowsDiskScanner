using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace WindowsDiskScanner.App;

public readonly record struct ChatStreamDelta(string Content, string ReasoningContent);

public sealed class OpenAiChatClient
{
    public async Task<IReadOnlyList<string>> FetchModelsAsync(
        LlmProvider provider,
        CancellationToken cancellationToken = default)
    {
        List<string> errors = [];
        foreach (Uri endpoint in BuildModelEndpointCandidates(provider.ApiUrl))
        {
            try
            {
                using HttpClient client = CreateClient(provider, timeoutSeconds: 30);
                using HttpRequestMessage request = CreateRequest(HttpMethod.Get, endpoint, provider.ApiKey);
                using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
                string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    errors.Add($"{endpoint} 返回 HTTP {(int)response.StatusCode}：{ExtractError(responseBody)}");
                    continue;
                }

                IReadOnlyList<string> models = ParseModels(responseBody);
                if (models.Count > 0)
                {
                    return models;
                }

                errors.Add($"{endpoint} 未返回模型。");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
            {
                errors.Add($"{endpoint} 请求失败：{exception.Message}");
            }
        }

        throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
    }

    public async Task<ModelTestResult> TestModelAsync(
        LlmProvider provider,
        string model,
        CancellationToken cancellationToken = default)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            string content = await SendChatAsync(
                provider,
                model,
                "你是连通性测试助手。",
                "请仅回复 OK。",
                cancellationToken);
            stopwatch.Stop();
            string preview = content.ReplaceLineEndings(" ").Trim();
            if (preview.Length > 80)
            {
                preview = preview[..80] + "…";
            }

            return new ModelTestResult(true, $"{stopwatch.Elapsed.TotalSeconds:N1} 秒 · {preview}", stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            return new ModelTestResult(false, exception.Message, stopwatch.Elapsed);
        }
    }

    public async Task<string> SendChatAsync(
        LlmProvider provider,
        string model,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default)
    {
        Uri endpoint = BuildChatEndpoint(provider.ApiUrl);
        using HttpClient client = CreateClient(provider, timeoutSeconds: 180);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, endpoint, provider.ApiKey);
        request.Content = JsonContent.Create(new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            stream = false
        });

        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}：{ExtractError(responseBody)}");
        }

        string content = ParseChatContent(responseBody);
        if (content.Length == 0)
        {
            throw new InvalidOperationException("上游返回成功，但没有可用的模型输出。");
        }

        return content;
    }

    public async Task SendChatStreamingAsync(
        LlmProvider provider,
        string model,
        string systemPrompt,
        string userPrompt,
        Action<ChatStreamDelta> onDelta,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onDelta);

        Uri endpoint = BuildChatEndpoint(provider.ApiUrl);
        using HttpClient client = CreateClient(provider, timeoutSeconds: 180);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, endpoint, provider.ApiKey);
        request.Content = JsonContent.Create(new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            stream = true
        });

        using HttpResponseMessage response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}：{ExtractError(responseBody)}");
        }

        await using Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using StreamReader reader = new(responseStream);
        StringBuilder fallbackBody = new();
        bool receivedStreamEvent = false;
        bool receivedContent = false;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (!receivedStreamEvent)
                {
                    fallbackBody.AppendLine(line);
                }

                continue;
            }

            receivedStreamEvent = true;
            string data = line[5..].Trim();
            if (data.Length == 0)
            {
                continue;
            }

            if (string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            ChatStreamDelta delta = ParseChatStreamChunk(data);
            if (delta.Content.Length == 0 && delta.ReasoningContent.Length == 0)
            {
                continue;
            }

            receivedContent |= delta.Content.Length > 0;
            onDelta(delta);
        }

        if (!receivedStreamEvent)
        {
            ChatStreamDelta delta = ParseChatStreamChunk(fallbackBody.ToString());
            if (delta.Content.Length > 0 || delta.ReasoningContent.Length > 0)
            {
                receivedContent = delta.Content.Length > 0;
                onDelta(delta);
            }
        }

        if (!receivedContent)
        {
            throw new InvalidOperationException("上游未返回可用的模型输出。");
        }
    }

    public static Uri BuildChatEndpoint(string apiUrl)
    {
        Uri apiUri = ParseApiUri(apiUrl);
        string path = apiUri.AbsolutePath.TrimEnd('/');
        string lowerPath = path.ToLowerInvariant();
        if (lowerPath.EndsWith("/chat/completions", StringComparison.Ordinal))
        {
            return apiUri;
        }

        string suffix = lowerPath.EndsWith("/v1", StringComparison.Ordinal)
            ? "/chat/completions"
            : "/v1/chat/completions";
        return new UriBuilder(apiUri)
        {
            Path = path + suffix,
            Query = string.Empty,
            Fragment = string.Empty
        }.Uri;
    }

    public static IReadOnlyList<Uri> BuildModelEndpointCandidates(string apiUrl)
    {
        Uri apiUri = ParseApiUri(apiUrl);
        string basePath = StripKnownEndpointSuffix(apiUri.AbsolutePath.TrimEnd('/'));
        UriBuilder builder = new(apiUri)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };
        builder.Path = basePath + "/v1/models";
        Uri first = builder.Uri;
        builder.Path = basePath + "/models";
        Uri second = builder.Uri;
        return [first, second];
    }

    private static HttpClient CreateClient(LlmProvider provider, int timeoutSeconds)
    {
        HttpClientHandler handler = new();
        switch (provider.ProxyMode)
        {
            case ProviderProxyMode.Direct:
                handler.UseProxy = false;
                break;
            case ProviderProxyMode.System:
                handler.UseProxy = true;
                handler.Proxy = WebRequest.DefaultWebProxy;
                break;
            case ProviderProxyMode.Custom:
                handler.UseProxy = true;
                handler.Proxy = new WebProxy(provider.CustomProxy);
                break;
        }

        if (!provider.VerifySsl)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        HttpClient client = new(handler)
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds)
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri endpoint, string apiKey)
    {
        HttpRequestMessage request = new(method, endpoint);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }

        return request;
    }

    private static Uri ParseApiUri(string apiUrl)
    {
        if (!Uri.TryCreate(apiUrl.Trim(), UriKind.Absolute, out Uri? apiUri) ||
            apiUri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("API 地址必须是有效的 HTTP 或 HTTPS 地址。");
        }

        return apiUri;
    }

    private static string StripKnownEndpointSuffix(string path)
    {
        string[] suffixes =
        [
            "/v1/chat/completions",
            "/chat/completions",
            "/v1/completions",
            "/completions",
            "/v1/models",
            "/models",
            "/v1"
        ];
        foreach (string suffix in suffixes)
        {
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return path[..^suffix.Length].TrimEnd('/');
            }
        }

        return path;
    }

    private static IReadOnlyList<string> ParseModels(string responseBody)
    {
        using JsonDocument document = JsonDocument.Parse(responseBody);
        JsonElement root = document.RootElement;
        JsonElement models;
        if (root.ValueKind == JsonValueKind.Array)
        {
            models = root;
        }
        else if (root.ValueKind == JsonValueKind.Object &&
                 (root.TryGetProperty("data", out models) || root.TryGetProperty("models", out models)))
        {
        }
        else
        {
            return [];
        }

        if (models.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> result = [];
        foreach (JsonElement item in models.EnumerateArray())
        {
            string? name = item.ValueKind switch
            {
                JsonValueKind.String => item.GetString(),
                JsonValueKind.Object when item.TryGetProperty("id", out JsonElement id) => id.GetString(),
                JsonValueKind.Object when item.TryGetProperty("name", out JsonElement modelName) => modelName.GetString(),
                _ => null
            };
            name = name?.Trim();
            if (!string.IsNullOrEmpty(name) && seen.Add(name))
            {
                result.Add(name);
            }
        }

        return result;
    }

    private static string ParseChatContent(string responseBody)
    {
        using JsonDocument document = JsonDocument.Parse(responseBody);
        if (!document.RootElement.TryGetProperty("choices", out JsonElement choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            return string.Empty;
        }

        JsonElement choice = choices[0];
        if (choice.TryGetProperty("message", out JsonElement message) &&
            message.TryGetProperty("content", out JsonElement content))
        {
            if (content.ValueKind == JsonValueKind.String)
            {
                return content.GetString()?.Trim() ?? string.Empty;
            }

            if (content.ValueKind == JsonValueKind.Array)
            {
                return string.Join(
                    Environment.NewLine,
                    content.EnumerateArray()
                        .Where(part => part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out _))
                        .Select(part => part.GetProperty("text").GetString())
                        .Where(text => !string.IsNullOrWhiteSpace(text))).Trim();
            }
        }

        return choice.TryGetProperty("text", out JsonElement textElement)
            ? textElement.GetString()?.Trim() ?? string.Empty
            : string.Empty;
    }

    private static ChatStreamDelta ParseChatStreamChunk(string data)
    {
        using JsonDocument document = JsonDocument.Parse(data);
        JsonElement root = document.RootElement;
        if (root.TryGetProperty("error", out _))
        {
            throw new InvalidOperationException(ExtractError(data));
        }

        if (!root.TryGetProperty("choices", out JsonElement choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            return default;
        }

        JsonElement choice = choices[0];
        JsonElement payload = choice.TryGetProperty("delta", out JsonElement delta)
            ? delta
            : choice.TryGetProperty("message", out JsonElement message)
                ? message
                : choice;
        string content = payload.TryGetProperty("content", out JsonElement contentElement)
            ? ParseStreamingContent(contentElement)
            : choice.TryGetProperty("text", out JsonElement textElement) && textElement.ValueKind == JsonValueKind.String
                ? textElement.GetString() ?? string.Empty
                : string.Empty;
        string reasoningContent = ParseReasoningContent(payload);
        if (reasoningContent.Length == 0)
        {
            reasoningContent = ParseReasoningContent(choice);
        }

        return new ChatStreamDelta(content, reasoningContent);
    }

    private static string ParseStreamingContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Concat(
            content.EnumerateArray()
                .Where(part => part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out _))
                .Select(part => part.GetProperty("text").GetString()));
    }

    private static string ParseReasoningContent(JsonElement element)
    {
        string[] propertyNames = ["reasoning_content", "reasoning", "thinking", "analysis", "reasoning_details"];
        foreach (string propertyName in propertyNames)
        {
            if (element.TryGetProperty(propertyName, out JsonElement value))
            {
                string content = ParseReasoningValue(value);
                if (content.Length > 0)
                {
                    return content;
                }
            }
        }

        return string.Empty;
    }

    private static string ParseReasoningValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            return string.Concat(value.EnumerateArray().Select(ParseReasoningValue));
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        string[] propertyNames = ["text", "content", "reasoning_content", "reasoning", "thinking"];
        foreach (string propertyName in propertyNames)
        {
            if (value.TryGetProperty(propertyName, out JsonElement nestedValue))
            {
                string content = ParseReasoningValue(nestedValue);
                if (content.Length > 0)
                {
                    return content;
                }
            }
        }

        return string.Empty;
    }

    private static string ExtractError(string responseBody)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("error", out JsonElement error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString() ?? responseBody;
                }

                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out JsonElement message))
                {
                    return message.GetString() ?? responseBody;
                }
            }
        }
        catch (JsonException)
        {
        }

        string trimmed = responseBody.Trim();
        return trimmed.Length > 500 ? trimmed[..500] + "…" : trimmed;
    }
}
