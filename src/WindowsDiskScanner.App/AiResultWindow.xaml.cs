using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace WindowsDiskScanner.App;

public partial class AiResultWindow : Window
{
    private static readonly FontFamily ReportFontFamily = new("Segoe UI, Microsoft YaHei UI");
    private readonly string _modelName;
    private readonly StringBuilder _content = new();
    private readonly StringBuilder _reasoning = new();
    private readonly DispatcherTimer _renderTimer;
    private bool _isCompleted;

    public AiResultWindow(string title, string modelName)
    {
        InitializeComponent();
        _modelName = modelName;
        _renderTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _renderTimer.Tick += (_, _) => RenderOutput();
        Closed += (_, _) => _renderTimer.Stop();
        Title = title;
        TitleText.Text = title;
        ModelText.Text = $"模型：{modelName} · 正在生成…";
    }

    public void AppendContent(string content)
    {
        if (content.Length == 0)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            _content.Append(content);
            if (!_renderTimer.IsEnabled)
            {
                _renderTimer.Start();
            }

            CopyButton.IsEnabled = true;
        });
    }

    public void AppendReasoning(string content)
    {
        if (content.Length == 0)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            _reasoning.Append(content);
            ReasoningExpander.Visibility = Visibility.Visible;
            if (!_renderTimer.IsEnabled)
            {
                _renderTimer.Start();
            }
        });
    }

    public void MarkCompleted() => RunOnUiThread(() =>
    {
        _isCompleted = true;
        RenderOutput();
        ModelText.Text = $"模型：{_modelName} · 已完成";
    });

    public void ShowCompletedContent(string content, string reasoningContent = "") => RunOnUiThread(() =>
    {
        _content.Clear();
        _content.Append(content);
        _reasoning.Clear();
        _reasoning.Append(reasoningContent);
        _isCompleted = true;
        ReasoningExpander.Visibility = reasoningContent.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        CopyButton.IsEnabled = content.Length > 0;
        RenderOutput(scrollToEnd: false);
        ModelText.Text = $"模型：{_modelName} · 历史记录";
    });

    public void ShowError(string message) =>
        RunOnUiThread(() =>
        {
            _isCompleted = true;
            if (_content.Length > 0)
            {
                _content.AppendLine().AppendLine();
            }

            _content.Append("> **请求失败：** ").Append(message);
            CopyButton.IsEnabled = true;
            RenderOutput();
            ModelText.Text = $"模型：{_modelName} · 请求失败";
        });

    private void RenderOutput(bool scrollToEnd = true)
    {
        _renderTimer.Stop();
        if (_reasoning.Length > 0)
        {
            string reasoning = _reasoning.ToString();
            try
            {
                ReasoningViewer.Markdown = NormalizeMarkdownListIndentation(reasoning);
            }
            catch (Exception)
            {
                ReasoningViewer.Markdown = EscapeMarkdown(reasoning);
            }

            ApplyDocumentStyles(ReasoningViewer.Document, 12);
        }

        string content = _content.ToString();
        try
        {
            ContentViewer.Markdown = NormalizeMarkdownListIndentation(content);
        }
        catch (Exception)
        {
            ContentViewer.Markdown = EscapeMarkdown(content);
        }

        ApplyDocumentStyles(ContentViewer.Document, 14);
        ThinkingPlaceholder.Text = _isCompleted ? "未返回内容" : "思考中…";
        ThinkingPlaceholder.Visibility = content.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        Dispatcher.BeginInvoke(
            () =>
            {
                ScrollViewer? contentScrollViewer = FindDescendant<ScrollViewer>(ContentViewer);
                if (scrollToEnd)
                {
                    contentScrollViewer?.ScrollToEnd();
                }
                else
                {
                    contentScrollViewer?.ScrollToHome();
                }

                if (ReasoningExpander.IsExpanded)
                {
                    ScrollViewer? reasoningScrollViewer = FindDescendant<ScrollViewer>(ReasoningViewer);
                    if (scrollToEnd)
                    {
                        reasoningScrollViewer?.ScrollToEnd();
                    }
                    else
                    {
                        reasoningScrollViewer?.ScrollToHome();
                    }
                }
            },
            DispatcherPriority.Background);
    }

    private static string NormalizeMarkdownListIndentation(string content)
    {
        string[] lines = content.ReplaceLineEndings("\n").Split('\n');
        bool insideCodeFence = false;
        for (int index = 0; index < lines.Length; index++)
        {
            string trimmedLine = lines[index].TrimStart();
            if (trimmedLine.StartsWith("```", StringComparison.Ordinal) ||
                trimmedLine.StartsWith("~~~", StringComparison.Ordinal))
            {
                insideCodeFence = !insideCodeFence;
                continue;
            }

            if (!insideCodeFence &&
                lines[index].Length > 3 &&
                lines[index][0] == ' ' &&
                lines[index][1] == ' ' &&
                lines[index][2] is '-' or '+' or '*' &&
                lines[index][3] == ' ')
            {
                lines[index] = "  " + lines[index];
            }
        }

        return string.Join('\n', lines);
    }

    private static void ApplyDocumentStyles(FlowDocument? document, double fontSize)
    {
        if (document is null)
        {
            return;
        }

        document.FontFamily = ReportFontFamily;
        document.FontSize = fontSize;
        ApplyListStyles(document.Blocks, 0);
    }

    private static void ApplyListStyles(BlockCollection blocks, int depth)
    {
        foreach (Block block in blocks)
        {
            if (block is Section section)
            {
                ApplyListStyles(section.Blocks, depth);
                continue;
            }

            if (block is not System.Windows.Documents.List list)
            {
                continue;
            }

            if (list.MarkerStyle is TextMarkerStyle.Disc or TextMarkerStyle.Circle or TextMarkerStyle.Square or TextMarkerStyle.Box)
            {
                list.MarkerStyle = depth switch
                {
                    0 => TextMarkerStyle.Disc,
                    1 => TextMarkerStyle.Circle,
                    _ => TextMarkerStyle.Square
                };
            }

            foreach (ListItem item in list.ListItems)
            {
                ApplyListStyles(item.Blocks, depth + 1);
            }
        }
    }

    private static string EscapeMarkdown(string content)
    {
        char[] specialCharacters = ['\\', '`', '*', '_', '{', '}', '[', ']', '(', ')', '#', '+', '-', '.', '!', '>'];
        StringBuilder escaped = new(content.Length);
        foreach (char character in content)
        {
            if (specialCharacters.Contains(character))
            {
                escaped.Append('\\');
            }

            escaped.Append(character);
        }

        return escaped.ToString();
    }

    private static T? FindDescendant<T>(DependencyObject source)
        where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(source);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(source, index);
            if (child is T match)
            {
                return match;
            }

            T? descendant = FindDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private void RunOnUiThread(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.Invoke(action);
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_content.Length > 0)
        {
            Clipboard.SetText(_content.ToString());
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        Close();
}
