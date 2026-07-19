using System.Windows;

namespace WindowsDiskScanner.App;

public partial class App : Application
{
    public AiHistoryStore AiHistoryStore { get; } = new();

    public ProviderStore ProviderStore { get; } = new();

    public OpenAiChatClient OpenAiChatClient { get; } = new();
}
