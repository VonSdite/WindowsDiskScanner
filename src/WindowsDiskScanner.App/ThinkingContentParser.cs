using System.Text;

namespace WindowsDiskScanner.App;

internal sealed class ThinkingContentParser
{
    private const string OpeningTag = "<think>";
    private const string ClosingTag = "</think>";
    private string _pending = string.Empty;
    private bool _insideThinking;

    public ChatStreamDelta Append(string content)
    {
        if (content.Length == 0)
        {
            return new ChatStreamDelta(string.Empty, string.Empty);
        }

        _pending += content;
        StringBuilder visibleContent = new();
        StringBuilder reasoningContent = new();
        while (_pending.Length > 0)
        {
            string tag = _insideThinking ? ClosingTag : OpeningTag;
            int tagIndex = _pending.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (tagIndex >= 0)
            {
                AppendCurrentContent(_pending[..tagIndex], visibleContent, reasoningContent);
                _pending = _pending[(tagIndex + tag.Length)..];
                _insideThinking = !_insideThinking;
                continue;
            }

            int retainedLength = GetTrailingTagPrefixLength(_pending, tag);
            int emittedLength = _pending.Length - retainedLength;
            AppendCurrentContent(_pending[..emittedLength], visibleContent, reasoningContent);
            _pending = retainedLength > 0 ? _pending[^retainedLength..] : string.Empty;
            break;
        }

        return new ChatStreamDelta(visibleContent.ToString(), reasoningContent.ToString());
    }

    public ChatStreamDelta Complete()
    {
        ChatStreamDelta result = _insideThinking
            ? new ChatStreamDelta(string.Empty, _pending)
            : new ChatStreamDelta(_pending, string.Empty);
        _pending = string.Empty;
        _insideThinking = false;
        return result;
    }

    public static ChatStreamDelta Parse(string content)
    {
        ThinkingContentParser parser = new();
        ChatStreamDelta parsed = parser.Append(content);
        ChatStreamDelta completed = parser.Complete();
        return new ChatStreamDelta(
            parsed.Content + completed.Content,
            parsed.ReasoningContent + completed.ReasoningContent);
    }

    private void AppendCurrentContent(
        string content,
        StringBuilder visibleContent,
        StringBuilder reasoningContent)
    {
        (_insideThinking ? reasoningContent : visibleContent).Append(content);
    }

    private static int GetTrailingTagPrefixLength(string content, string tag)
    {
        int maximumLength = Math.Min(content.Length, tag.Length - 1);
        for (int length = maximumLength; length > 0; length--)
        {
            if (content.EndsWith(tag[..length], StringComparison.OrdinalIgnoreCase))
            {
                return length;
            }
        }

        return 0;
    }
}
