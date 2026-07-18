using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsDiskScanner.App;

public sealed class ProviderStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _configPath;

    public ProviderStore()
    {
        _configPath = Path.Combine(AppContext.BaseDirectory, "providers.json");
        Load();
    }

    public ObservableCollection<LlmProvider> Providers { get; } = [];

    public string? LoadError { get; private set; }

    public event EventHandler? Changed;

    public void Add(LlmProvider provider)
    {
        Validate(provider, exceptId: null);
        Providers.Add(provider.Clone());
        SaveAndNotify();
    }

    public void Update(LlmProvider provider)
    {
        Validate(provider, provider.Id);
        LlmProvider? existing = Providers.FirstOrDefault(item => item.Id == provider.Id);
        if (existing is null)
        {
            throw new InvalidOperationException("Provider 不存在。");
        }

        int index = Providers.IndexOf(existing);
        Providers[index] = provider.Clone();
        SaveAndNotify();
    }

    public void Remove(LlmProvider provider)
    {
        LlmProvider? existing = Providers.FirstOrDefault(item => item.Id == provider.Id);
        if (existing is null)
        {
            return;
        }

        Providers.Remove(existing);
        SaveAndNotify();
    }

    public void Move(int oldIndex, int newIndex)
    {
        if (oldIndex == newIndex || oldIndex < 0 || newIndex < 0 || oldIndex >= Providers.Count || newIndex >= Providers.Count)
        {
            return;
        }

        Providers.Move(oldIndex, newIndex);
        SaveAndNotify();
    }

    public IReadOnlyList<AiModelOption> GetModelOptions() =>
        Providers
            .SelectMany(provider => provider.Models.Select(model => new AiModelOption(provider, model)))
            .ToArray();

    private void Load()
    {
        Providers.Clear();
        LoadError = null;
        if (!File.Exists(_configPath))
        {
            return;
        }

        try
        {
            string json = File.ReadAllText(_configPath);
            ProviderConfiguration? configuration = JsonSerializer.Deserialize<ProviderConfiguration>(json, JsonOptions);
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            foreach (LlmProvider provider in configuration?.Providers ?? [])
            {
                Normalize(provider);
                if (provider.Name.Length == 0 || !names.Add(provider.Name))
                {
                    continue;
                }

                Providers.Add(provider);
            }
        }
        catch (Exception exception)
        {
            LoadError = exception.Message;
        }
    }

    private void SaveAndNotify()
    {
        string? directory = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        ProviderConfiguration configuration = new()
        {
            Providers = Providers.Select(provider => provider.Clone()).ToList()
        };
        string json = JsonSerializer.Serialize(configuration, JsonOptions);
        string temporaryPath = _configPath + ".tmp";
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _configPath, overwrite: true);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Validate(LlmProvider provider, Guid? exceptId)
    {
        Normalize(provider);
        if (provider.Name.Length == 0)
        {
            throw new InvalidOperationException("Provider 名称不能为空。");
        }

        if (provider.Name.Length > 64)
        {
            throw new InvalidOperationException("Provider 名称不能超过 64 个字符。");
        }

        if (!Uri.TryCreate(provider.ApiUrl, UriKind.Absolute, out Uri? apiUri) ||
            apiUri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("API 地址必须是有效的 HTTP 或 HTTPS 地址。");
        }

        if (provider.ProxyMode == ProviderProxyMode.Custom &&
            (!Uri.TryCreate(provider.CustomProxy, UriKind.Absolute, out Uri? proxyUri) ||
             proxyUri.Scheme is not ("http" or "https" or "socks5")))
        {
            throw new InvalidOperationException("自定义代理必须是有效的 HTTP、HTTPS 或 SOCKS5 地址。");
        }

        if (Providers.Any(item =>
                item.Id != exceptId &&
                string.Equals(item.Name, provider.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Provider 名称已存在：{provider.Name}");
        }
    }

    private static void Normalize(LlmProvider provider)
    {
        provider.Id = provider.Id == Guid.Empty ? Guid.NewGuid() : provider.Id;
        provider.Name = provider.Name.Trim();
        provider.ApiUrl = provider.ApiUrl.Trim();
        provider.ApiKey = provider.ApiKey.Trim();
        provider.CustomProxy = provider.CustomProxy.Trim();
        provider.Models ??= [];

        HashSet<string> modelNames = new(StringComparer.OrdinalIgnoreCase);
        LlmModel[] normalizedModels = provider.Models
            .Select(model => new LlmModel { Name = model.Name.Trim() })
            .Where(model => model.Name.Length > 0 && modelNames.Add(model.Name))
            .ToArray();
        provider.Models.Clear();
        foreach (LlmModel model in normalizedModels)
        {
            provider.Models.Add(model);
        }
    }

    private sealed class ProviderConfiguration
    {
        public List<LlmProvider> Providers { get; set; } = [];
    }
}
