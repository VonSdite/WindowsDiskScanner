using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsDiskScanner.App;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiHistoryKind
{
    Report,
    Inquiry
}

public sealed class AiHistoryRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public AiHistoryKind Kind { get; set; }

    public string Title { get; set; } = string.Empty;

    public string ModelName { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public string Content { get; set; } = string.Empty;

    public string ReasoningContent { get; set; } = string.Empty;

    [JsonIgnore]
    public string KindDisplayName => Kind == AiHistoryKind.Report ? "分析报告" : "AI 询问";

    [JsonIgnore]
    public string CreatedAtDisplay => CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
}

public sealed class AiHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _syncRoot = new();
    private readonly string _historyDirectory = Path.Combine(AppContext.BaseDirectory, "ai-history");

    public string HistoryDirectory => _historyDirectory;

    public event EventHandler? Changed;

    public IReadOnlyList<AiHistoryRecord> LoadAll()
    {
        lock (_syncRoot)
        {
            if (!Directory.Exists(_historyDirectory))
            {
                return [];
            }

            List<AiHistoryRecord> records = [];
            foreach (string path in Directory.EnumerateFiles(_historyDirectory, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    AiHistoryRecord? record = JsonSerializer.Deserialize<AiHistoryRecord>(File.ReadAllText(path), JsonOptions);
                    if (record is null || record.Id == Guid.Empty || record.Content.Length == 0)
                    {
                        continue;
                    }

                    record.Title = record.Title.Trim();
                    record.ModelName = record.ModelName.Trim();
                    records.Add(record);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
                {
                }
            }

            return records.OrderByDescending(record => record.CreatedAt).ToArray();
        }
    }

    public AiHistoryRecord Add(
        AiHistoryKind kind,
        string title,
        string modelName,
        DateTimeOffset createdAt,
        string content,
        string reasoningContent = "")
    {
        AiHistoryRecord record = new()
        {
            Kind = kind,
            Title = title.Trim(),
            ModelName = modelName.Trim(),
            CreatedAt = createdAt,
            Content = content,
            ReasoningContent = reasoningContent
        };

        lock (_syncRoot)
        {
            Directory.CreateDirectory(_historyDirectory);
            string path = GetRecordPath(record.Id);
            string temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(record, JsonOptions));
            File.Move(temporaryPath, path, overwrite: true);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return record;
    }

    public int Delete(IEnumerable<Guid> recordIds)
    {
        int deletedCount = 0;
        lock (_syncRoot)
        {
            foreach (Guid recordId in recordIds.Where(id => id != Guid.Empty).Distinct())
            {
                string path = GetRecordPath(recordId);
                if (!File.Exists(path))
                {
                    continue;
                }

                File.Delete(path);
                deletedCount++;
            }
        }

        if (deletedCount > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return deletedCount;
    }

    private string GetRecordPath(Guid recordId) =>
        Path.Combine(_historyDirectory, recordId.ToString("N") + ".json");
}
