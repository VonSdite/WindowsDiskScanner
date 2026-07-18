using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace WindowsDiskScanner.App;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProviderProxyMode
{
    Direct,
    System,
    Custom
}

public sealed class LlmProvider
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string ApiUrl { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public ProviderProxyMode ProxyMode { get; set; } = ProviderProxyMode.Direct;

    public string CustomProxy { get; set; } = string.Empty;

    public bool VerifySsl { get; set; }

    public ObservableCollection<LlmModel> Models { get; set; } = [];

    public LlmProvider Clone() =>
        new()
        {
            Id = Id,
            Name = Name,
            ApiUrl = ApiUrl,
            ApiKey = ApiKey,
            ProxyMode = ProxyMode,
            CustomProxy = CustomProxy,
            VerifySsl = VerifySsl,
            Models = new ObservableCollection<LlmModel>(Models.Select(model => new LlmModel { Name = model.Name }))
        };
}

public sealed class LlmModel : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _testStatus = string.Empty;
    private string _testMessage = string.Empty;
    private bool _isTesting;

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    [JsonIgnore]
    public string TestStatus
    {
        get => _testStatus;
        set => SetField(ref _testStatus, value);
    }

    [JsonIgnore]
    public string TestMessage
    {
        get => _testMessage;
        set => SetField(ref _testMessage, value);
    }

    [JsonIgnore]
    public bool IsTesting
    {
        get => _isTesting;
        set => SetField(ref _isTesting, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed record AiModelOption(LlmProvider Provider, LlmModel Model)
{
    public string DisplayName => $"{Provider.Name} / {Model.Name}";
}

public sealed record ModelTestResult(bool Success, string Message, TimeSpan Elapsed);
