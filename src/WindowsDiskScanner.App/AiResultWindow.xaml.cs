using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MdXaml;

namespace WindowsDiskScanner.App;

public partial class AiResultWindow : Window
{
    private static readonly Regex WindowsPathPattern = new(
        @"(?<![A-Za-z0-9_])(?:[A-Za-z]:\\|\\\\)[^\r\n`|<>""，。；：！？、（）]+",
        RegexOptions.Compiled);
    private static readonly Regex FollowUpSectionPattern = new(
        @"(?:\r?\n)+---\r?\n\r?\n## 追问\r?\n",
        RegexOptions.Compiled);
    private static readonly Regex FollowUpAnswerPattern = new(
        @"(?:\r?\n)+## 回答\r?\n(?:\r?\n)?",
        RegexOptions.Compiled);
    private static readonly Regex FollowUpReasoningPattern = new(
        @"(?:^|(?:\r?\n)+---\r?\n\r?\n)### 追问思考\r?\n(?:\r?\n)?",
        RegexOptions.Compiled);
    private static readonly FontFamily ReportFontFamily = new("Segoe UI, Microsoft YaHei UI");
    private static readonly Brush PathLinkBrush = new SolidColorBrush(Color.FromRgb(47, 111, 237));
    private static readonly Brush ReasoningHeaderBrush = new SolidColorBrush(Color.FromRgb(105, 121, 145));
    private static readonly Brush ReasoningHintBrush = new SolidColorBrush(Color.FromRgb(154, 166, 184));
    private static readonly Brush ReasoningTextBrush = new SolidColorBrush(Color.FromRgb(123, 135, 153));
    private static readonly Brush ReasoningBackgroundBrush = new SolidColorBrush(Color.FromRgb(247, 249, 252));
    private static readonly Brush ReasoningBorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 241));
    private static readonly Brush AssistantBubbleBackgroundBrush = Brushes.White;
    private static readonly Brush AssistantBubbleBorderBrush = new SolidColorBrush(Color.FromRgb(214, 224, 231));
    private static readonly Brush UserBubbleBackgroundBrush = new SolidColorBrush(Color.FromRgb(237, 248, 246));
    private static readonly Brush UserBubbleBorderBrush = new SolidColorBrush(Color.FromRgb(202, 224, 219));
    private static readonly Brush AssistantAvatarBackgroundBrush = new SolidColorBrush(Color.FromRgb(232, 247, 244));
    private static readonly Brush AssistantAvatarForegroundBrush = new SolidColorBrush(Color.FromRgb(14, 137, 130));
    private static readonly Brush UserAvatarBackgroundBrush = new SolidColorBrush(Color.FromRgb(242, 245, 249));
    private readonly string _modelName;
    private readonly StringBuilder _content = new();
    private readonly List<ResponseSection> _responseSections = [new(string.Empty)];
    private readonly Dictionary<string, string?> _resolvedAbbreviatedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _renderTimer;
    private bool _isCompleted;

    public event EventHandler<FollowUpRequestedEventArgs>? FollowUpRequested;

    public string ResultContent => _content.ToString();

    public string ReasoningContent => BuildReasoningContent();

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
            _responseSections[^1].Content.Append(content);
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
            _responseSections[^1].Reasoning.Append(content);
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
        SetFollowUpBusy(false);
    });

    public void ShowCompletedContent(string content, string reasoningContent = "") => RunOnUiThread(() =>
    {
        LoadResponseSections(content, reasoningContent);
        _isCompleted = true;
        FollowUpPanel.Visibility = Visibility.Collapsed;
        CopyButton.IsEnabled = _content.Length > 0;
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
                _responseSections[^1].Content.AppendLine().AppendLine();
            }

            _content.Append("> **请求失败：** ").Append(message);
            _responseSections[^1].Content.Append("> **请求失败：** ").Append(message);
            CopyButton.IsEnabled = true;
            RenderOutput();
            ModelText.Text = $"模型：{_modelName} · 请求失败";
        });

    public void BeginFollowUp(string question) => RunOnUiThread(() =>
    {
        _isCompleted = false;
        StringBuilder prefix = new();
        prefix.AppendLine().AppendLine("---").AppendLine().AppendLine("## 追问").AppendLine();
        foreach (string line in question.ReplaceLineEndings("\n").Split('\n'))
        {
            prefix.Append("> ").AppendLine(line);
        }

        prefix.AppendLine().AppendLine("## 回答").AppendLine();
        _content.Append(prefix);
        _responseSections.Add(new ResponseSection(prefix.ToString(), question: question));

        ConversationScrollViewer.ScrollToEnd();
        RenderOutput();
        ModelText.Text = $"模型：{_modelName} · 正在追问…";
    });

    public void ShowFollowUpError(string message) => RunOnUiThread(() =>
    {
        _content.Append("> **追问失败：** ").AppendLine(message);
        _responseSections[^1].Content.Append("> **追问失败：** ").AppendLine(message);
        _isCompleted = true;
        RenderOutput();
        ModelText.Text = $"模型：{_modelName} · 追问失败";
        SetFollowUpBusy(false);
    });

    public void MarkFollowUpCancelled() => RunOnUiThread(() =>
    {
        _content.AppendLine("> *追问已取消。*");
        _responseSections[^1].Content.AppendLine("> *追问已取消。*");
        _isCompleted = true;
        RenderOutput();
        ModelText.Text = $"模型：{_modelName} · 追问已取消";
        SetFollowUpBusy(false);
    });

    private void RenderOutput(bool scrollToEnd = true)
    {
        _renderTimer.Stop();
        CaptureReasoningScrollState();
        double verticalOffset = ConversationScrollViewer.VerticalOffset;
        bool followOutput = scrollToEnd && IsNearBottom(ConversationScrollViewer);
        for (int index = 0; index < _responseSections.Count; index++)
        {
            ResponseSection section = _responseSections[index];
            if (section.Question is { Length: > 0 } question && section.UserMessage is null)
            {
                section.UserMessage = CreateUserMessage(question);
                ConversationPanel.Children.Add(section.UserMessage);
            }

            if (section.AssistantMessage is null)
            {
                section.AssistantMessage = CreateAssistantMessage(section);
                ConversationPanel.Children.Add(section.AssistantMessage);
            }

            UpdateAssistantMessage(section, index);
        }

        RestoreReasoningScrollState();

        Dispatcher.BeginInvoke(
            () =>
            {
                if (!scrollToEnd)
                {
                    ConversationScrollViewer.ScrollToHome();
                }
                else if (followOutput)
                {
                    ConversationScrollViewer.ScrollToEnd();
                }
                else
                {
                    ConversationScrollViewer.ScrollToVerticalOffset(verticalOffset);
                }
            },
            DispatcherPriority.Background);
    }

    private void UpdateAssistantMessage(ResponseSection section, int sectionIndex)
    {
        if (section.AssistantBody is null)
        {
            return;
        }

        string reasoning = section.Reasoning.ToString();
        if (reasoning.Length > 0)
        {
            if (section.ReasoningPanel is null)
            {
                section.ReasoningPanel = CreateReasoningPanel(section);
                section.ReasoningPanel.Margin = new Thickness(0, 9, 0, 0);
                section.AssistantBody.Children.Insert(1, section.ReasoningPanel);
                section.RenderedReasoning = reasoning;
            }
            else if (!string.Equals(section.RenderedReasoning, reasoning, StringComparison.Ordinal))
            {
                UpdateMarkdownViewer(section.ReasoningViewer!, reasoning, 12);
                section.RenderedReasoning = reasoning;
            }
        }

        string content = section.Content.ToString();
        if (content.Length > 0)
        {
            if (section.AnswerViewer is null)
            {
                if (section.StatusText is not null)
                {
                    section.AssistantBody.Children.Remove(section.StatusText);
                    section.StatusText = null;
                }

                section.AnswerViewer = CreateMarkdownViewer(content, 14);
                section.AnswerViewer.Margin = new Thickness(0, 8, 0, 0);
                section.AssistantBody.Children.Add(section.AnswerViewer);
                section.RenderedContent = content;
            }
            else if (!string.Equals(section.RenderedContent, content, StringComparison.Ordinal))
            {
                UpdateMarkdownViewer(section.AnswerViewer, content, 14);
                section.RenderedContent = content;
            }
        }
        else if (section.StatusText is not null)
        {
            section.StatusText.Text = sectionIndex == _responseSections.Count - 1 && !_isCompleted
                ? "正在生成回答…"
                : "未返回内容";
        }
    }

    private void CaptureReasoningScrollState()
    {
        foreach (ResponseSection section in _responseSections)
        {
            if (!section.IsReasoningExpanded || section.ReasoningViewer is null)
            {
                continue;
            }

            ScrollViewer? scrollViewer = FindDescendant<ScrollViewer>(section.ReasoningViewer);
            if (scrollViewer is null)
            {
                continue;
            }

            section.ReasoningVerticalOffset = scrollViewer.VerticalOffset;
            section.FollowReasoningOutput = IsNearBottom(scrollViewer);
        }
    }

    private void RestoreReasoningScrollState()
    {
        foreach (ResponseSection section in _responseSections)
        {
            if (!section.IsReasoningExpanded || section.ReasoningViewer is null)
            {
                continue;
            }

            section.ReasoningViewer.UpdateLayout();
            ScrollViewer? scrollViewer = FindDescendant<ScrollViewer>(section.ReasoningViewer);
            if (section.FollowReasoningOutput)
            {
                scrollViewer?.ScrollToEnd();
            }
            else
            {
                scrollViewer?.ScrollToVerticalOffset(section.ReasoningVerticalOffset);
            }
        }
    }

    private FrameworkElement CreateUserMessage(string question)
    {
        Border bubble = new()
        {
            MaxWidth = 560,
            Padding = new Thickness(13, 10, 13, 10),
            Background = UserBubbleBackgroundBrush,
            BorderBrush = UserBubbleBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Child = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(38, 55, 81)),
                FontSize = 14,
                Text = question,
                TextWrapping = TextWrapping.Wrap
            }
        };
        Border avatar = CreateAvatar("我", UserAvatarBackgroundBrush, ReasoningHeaderBrush);
        Grid row = new()
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(72, 0, 4, 14)
        };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bubble.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(bubble, 0);
        Grid.SetColumn(avatar, 1);
        row.Children.Add(bubble);
        row.Children.Add(avatar);
        return row;
    }

    private FrameworkElement CreateAssistantMessage(ResponseSection section)
    {
        StackPanel body = new();
        section.AssistantBody = body;
        body.Children.Add(new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(36, 55, 81)),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Text = _modelName,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        section.StatusText = new TextBlock
        {
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = ReasoningHintBrush,
            FontSize = 13
        };
        body.Children.Add(section.StatusText);

        Border bubble = new()
        {
            Padding = new Thickness(13, 10, 13, 12),
            Background = AssistantBubbleBackgroundBrush,
            BorderBrush = AssistantBubbleBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Child = body
        };
        Border avatar = CreateAvatar("AI", AssistantAvatarBackgroundBrush, AssistantAvatarForegroundBrush);
        Grid row = new()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(2, 0, 4, 18)
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        bubble.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(avatar, 0);
        Grid.SetColumn(bubble, 1);
        row.Children.Add(avatar);
        row.Children.Add(bubble);
        return row;
    }

    private static Border CreateAvatar(string text, Brush background, Brush foreground) =>
        new()
        {
            Width = 30,
            Height = 30,
            VerticalAlignment = VerticalAlignment.Top,
            Background = background,
            BorderBrush = new SolidColorBrush(Color.FromRgb(211, 225, 226)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(15),
            Child = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = foreground,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Text = text
            }
        };

    private MarkdownScrollViewer CreateMarkdownViewer(string markdown, double fontSize)
    {
        MarkdownScrollViewer viewer = new()
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FocusVisualStyle = null,
            Foreground = new SolidColorBrush(Color.FromRgb(38, 55, 81)),
            FontFamily = ReportFontFamily,
            FontSize = fontSize,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        viewer.PreviewMouseWheel += MarkdownViewer_PreviewMouseWheel;
        UpdateMarkdownViewer(viewer, markdown, fontSize);
        return viewer;
    }

    private void UpdateMarkdownViewer(
        MarkdownScrollViewer viewer,
        string markdown,
        double fontSize)
    {
        try
        {
            viewer.Markdown = NormalizeMarkdownListIndentation(markdown);
        }
        catch (Exception)
        {
            viewer.Markdown = EscapeMarkdown(markdown);
        }

        ApplyDocumentStyles(viewer.Document, fontSize);
    }

    private string BuildReasoningContent()
    {
        int lastReasoningSection = -1;
        for (int index = _responseSections.Count - 1; index >= 0; index--)
        {
            if (_responseSections[index].Reasoning.Length > 0)
            {
                lastReasoningSection = index;
                break;
            }
        }

        if (lastReasoningSection < 0)
        {
            return string.Empty;
        }

        StringBuilder reasoning = new();
        reasoning.Append(_responseSections[0].Reasoning);

        for (int index = 1; index <= lastReasoningSection; index++)
        {
            if (reasoning.Length > 0)
            {
                reasoning.AppendLine().AppendLine("---").AppendLine();
            }

            reasoning.AppendLine("### 追问思考").AppendLine();
            reasoning.Append(_responseSections[index].Reasoning);
        }

        return reasoning.ToString();
    }

    private void LoadResponseSections(string content, string reasoningContent)
    {
        ConversationPanel.Children.Clear();
        _responseSections.Clear();
        MatchCollection followUpMatches = FollowUpSectionPattern.Matches(content);
        int initialContentLength = followUpMatches.Count > 0 ? followUpMatches[0].Index : content.Length;
        _responseSections.Add(new ResponseSection(string.Empty, content[..initialContentLength]));
        for (int index = 0; index < followUpMatches.Count; index++)
        {
            int sectionStart = followUpMatches[index].Index;
            int sectionEnd = index + 1 < followUpMatches.Count
                ? followUpMatches[index + 1].Index
                : content.Length;
            string sectionContent = content[sectionStart..sectionEnd];
            Match answerMatch = FollowUpAnswerPattern.Match(sectionContent);
            int prefixLength = answerMatch.Success ? answerMatch.Index + answerMatch.Length : 0;
            string prefix = sectionContent[..prefixLength];
            _responseSections.Add(new ResponseSection(
                prefix,
                sectionContent[prefixLength..],
                ExtractQuestion(prefix)));
        }

        string[] embeddedReasoning = new string[_responseSections.Count];
        for (int index = 0; index < _responseSections.Count; index++)
        {
            ResponseSection section = _responseSections[index];
            ChatStreamDelta parsedContent = ThinkingContentParser.Parse(section.Content.ToString());
            section.Content.Clear();
            section.Content.Append(parsedContent.Content);
            embeddedReasoning[index] = parsedContent.ReasoningContent;
        }

        LoadReasoningSections(reasoningContent);
        for (int index = 0; index < _responseSections.Count; index++)
        {
            if (_responseSections[index].Reasoning.Length == 0 && embeddedReasoning[index].Length > 0)
            {
                _responseSections[index].Reasoning.Append(embeddedReasoning[index]);
            }
        }

        _content.Clear();
        foreach (ResponseSection section in _responseSections)
        {
            _content.Append(section.Prefix).Append(section.Content);
        }
    }

    private static string ExtractQuestion(string prefix)
    {
        StringBuilder question = new();
        foreach (string line in prefix.ReplaceLineEndings("\n").Split('\n'))
        {
            string trimmedLine = line.TrimStart();
            if (!trimmedLine.StartsWith('>'))
            {
                continue;
            }

            if (question.Length > 0)
            {
                question.AppendLine();
            }

            question.Append(trimmedLine[1..].TrimStart());
        }

        return question.ToString();
    }

    private void LoadReasoningSections(string reasoningContent)
    {
        if (reasoningContent.Length == 0)
        {
            return;
        }

        MatchCollection followUpMatches = FollowUpReasoningPattern.Matches(reasoningContent);
        if (followUpMatches.Count == 0)
        {
            _responseSections[0].Reasoning.Append(reasoningContent);
            return;
        }

        if (followUpMatches[0].Index > 0)
        {
            _responseSections[0].Reasoning.Append(
                reasoningContent[..followUpMatches[0].Index].Trim('\r', '\n'));
        }

        for (int index = 0; index < followUpMatches.Count && index + 1 < _responseSections.Count; index++)
        {
            int sectionStart = followUpMatches[index].Index + followUpMatches[index].Length;
            int sectionEnd = index + 1 < followUpMatches.Count
                ? followUpMatches[index + 1].Index
                : reasoningContent.Length;
            string sectionReasoning = reasoningContent[sectionStart..sectionEnd].Trim('\r', '\n');
            if (sectionReasoning.Length > 0)
            {
                _responseSections[index + 1].Reasoning.Append(sectionReasoning);
            }
        }
    }

    private FrameworkElement CreateReasoningPanel(ResponseSection section)
    {
        TextBlock arrow = new()
        {
            Foreground = ReasoningHintBrush,
            FontSize = 16,
            Text = section.IsReasoningExpanded ? "⌃" : "⌄"
        };
        Grid headerContent = new();
        headerContent.ColumnDefinitions.Add(new ColumnDefinition());
        headerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        StackPanel title = new()
        {
            Orientation = Orientation.Horizontal
        };
        TextBlock hint = new()
        {
            Margin = new Thickness(6, 0, 0, 0),
            Foreground = ReasoningHintBrush,
            FontSize = 11,
            Text = section.IsReasoningExpanded ? "点击收起" : "点击展开"
        };
        title.Children.Add(new TextBlock
        {
            Foreground = ReasoningHeaderBrush,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Text = "思考过程"
        });
        title.Children.Add(hint);
        Grid.SetColumn(title, 0);
        Grid.SetColumn(arrow, 1);
        headerContent.Children.Add(title);
        headerContent.Children.Add(arrow);
        Border header = new()
        {
            Padding = new Thickness(9, 7, 9, 7),
            Background = ReasoningBackgroundBrush,
            BorderBrush = ReasoningBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Cursor = Cursors.Hand,
            Child = headerContent
        };

        MarkdownScrollViewer viewer = new()
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FocusVisualStyle = null,
            Foreground = ReasoningTextBrush,
            FontFamily = ReportFontFamily,
            FontSize = 12,
            MarkdownStyleName = "Compact",
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        viewer.PreviewMouseWheel += (_, e) =>
            ReasoningViewer_PreviewMouseWheel(section, viewer, e);
        string reasoning = section.Reasoning.ToString();
        UpdateMarkdownViewer(viewer, reasoning, 12);
        section.ReasoningViewer = viewer;
        Border content = new()
        {
            Margin = new Thickness(0, 8, 0, 0),
            MaxHeight = 190,
            Padding = new Thickness(12, 10, 12, 10),
            Background = ReasoningBackgroundBrush,
            BorderBrush = ReasoningBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Child = viewer,
            Visibility = section.IsReasoningExpanded ? Visibility.Visible : Visibility.Collapsed
        };
        StackPanel panel = new();
        panel.Children.Add(header);
        panel.Children.Add(content);
        header.MouseLeftButtonUp += (_, e) =>
        {
            section.IsReasoningExpanded = !section.IsReasoningExpanded;
            content.Visibility = section.IsReasoningExpanded ? Visibility.Visible : Visibility.Collapsed;
            arrow.Text = section.IsReasoningExpanded ? "⌃" : "⌄";
            hint.Text = section.IsReasoningExpanded ? "点击收起" : "点击展开";
            if (section.IsReasoningExpanded)
            {
                section.FollowReasoningOutput = true;
                Dispatcher.BeginInvoke(
                    () =>
                    {
                        content.UpdateLayout();
                        ScrollViewer? scrollViewer = FindDescendant<ScrollViewer>(viewer);
                        scrollViewer?.ScrollToEnd();
                        section.ReasoningVerticalOffset = scrollViewer?.VerticalOffset ?? 0;
                    },
                    DispatcherPriority.Background);
            }

            e.Handled = true;
        };
        return panel;
    }

    private void MarkdownViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ScrollWithMouseWheel(ConversationScrollViewer, e.Delta);
        e.Handled = true;
    }

    private void ReasoningViewer_PreviewMouseWheel(
        ResponseSection section,
        MarkdownScrollViewer viewer,
        MouseWheelEventArgs e)
    {
        ScrollViewer? scrollViewer = FindDescendant<ScrollViewer>(viewer);
        bool canScrollReasoning = scrollViewer is not null &&
                                  (e.Delta > 0
                                      ? scrollViewer.VerticalOffset > 0
                                      : scrollViewer.VerticalOffset < scrollViewer.ScrollableHeight);
        if (canScrollReasoning)
        {
            ScrollWithMouseWheel(scrollViewer!, e.Delta);
            section.ReasoningVerticalOffset = scrollViewer!.VerticalOffset;
            section.FollowReasoningOutput = IsNearBottom(scrollViewer);
        }
        else
        {
            ScrollWithMouseWheel(ConversationScrollViewer, e.Delta);
        }

        e.Handled = true;
    }

    private static void ScrollWithMouseWheel(ScrollViewer scrollViewer, int delta)
    {
        const double pixelsPerWheelStep = 48;
        double targetOffset = scrollViewer.VerticalOffset -
                              delta / 120.0 * pixelsPerWheelStep;
        scrollViewer.ScrollToVerticalOffset(Math.Clamp(targetOffset, 0, scrollViewer.ScrollableHeight));
    }

    private static bool IsNearBottom(ScrollViewer? scrollViewer) =>
        scrollViewer is null || scrollViewer.ScrollableHeight - scrollViewer.VerticalOffset <= 8;

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

    private void ApplyDocumentStyles(FlowDocument? document, double fontSize)
    {
        if (document is null)
        {
            return;
        }

        document.FontFamily = ReportFontFamily;
        document.FontSize = fontSize;
        document.PagePadding = new Thickness(0);
        document.ColumnWidth = double.PositiveInfinity;
        ApplyMarkdownBlockStyles(document.Blocks, fontSize);
        ApplyListStyles(document.Blocks, 0);
        LinkFileSystemPaths(document);
    }

    private static void ApplyMarkdownBlockStyles(
        BlockCollection blocks,
        double baseFontSize,
        bool insideList = false)
    {
        foreach (Block block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    ApplyParagraphStyle(paragraph, baseFontSize, insideList);
                    break;
                case Section section:
                    ApplyMarkdownBlockStyles(section.Blocks, baseFontSize, insideList);
                    break;
                case System.Windows.Documents.List list:
                    list.FontSize = baseFontSize;
                    list.Margin = new Thickness(2, 2, 0, insideList ? 3 : 9);
                    foreach (ListItem item in list.ListItems)
                    {
                        ApplyMarkdownBlockStyles(item.Blocks, baseFontSize, insideList: true);
                    }
                    break;
                case Table table:
                    table.FontSize = baseFontSize;
                    table.Margin = new Thickness(0, 4, 0, 10);
                    foreach (TableRowGroup rowGroup in table.RowGroups)
                    {
                        foreach (TableRow row in rowGroup.Rows)
                        {
                            foreach (TableCell cell in row.Cells)
                            {
                                ApplyMarkdownBlockStyles(cell.Blocks, baseFontSize);
                            }
                        }
                    }
                    break;
                case BlockUIContainer container when Equals(container.Tag, "RuleSingle"):
                    container.Margin = new Thickness(0, 8, 0, 10);
                    break;
            }
        }
    }

    private static void ApplyParagraphStyle(Paragraph paragraph, double baseFontSize, bool insideList)
    {
        string? tag = paragraph.Tag as string;
        switch (tag)
        {
            case "Heading1":
                paragraph.FontSize = baseFontSize <= 12 ? 18 : 26;
                paragraph.FontWeight = FontWeights.SemiBold;
                paragraph.Margin = new Thickness(0, 2, 0, 13);
                paragraph.KeepWithNext = true;
                break;
            case "Heading2":
                paragraph.FontSize = baseFontSize <= 12 ? 16 : 20;
                paragraph.FontWeight = FontWeights.SemiBold;
                paragraph.Margin = new Thickness(0, 13, 0, 8);
                paragraph.KeepWithNext = true;
                break;
            case "Heading3":
                paragraph.FontSize = baseFontSize <= 12 ? 14 : 17;
                paragraph.FontWeight = FontWeights.SemiBold;
                paragraph.Margin = new Thickness(0, 11, 0, 6);
                paragraph.KeepWithNext = true;
                break;
            case "Heading4":
            case "Heading5":
            case "Heading6":
                paragraph.FontSize = baseFontSize + 1;
                paragraph.FontWeight = FontWeights.SemiBold;
                paragraph.Margin = new Thickness(0, 9, 0, 5);
                paragraph.KeepWithNext = true;
                break;
            default:
                paragraph.FontSize = baseFontSize;
                paragraph.LineHeight = baseFontSize * 1.55;
                paragraph.Margin = new Thickness(0, 0, 0, insideList ? 2 : 9);
                break;
        }
    }

    private void LinkFileSystemPaths(FlowDocument document)
    {
        foreach (Run run in FindRuns(document.Blocks).ToArray())
        {
            IReadOnlyList<PathMatch> matches = FindExistingPathMatches(run.Text);
            if (matches.Count > 0)
            {
                ReplaceRunWithPathLinks(run, matches);
            }
        }
    }

    private void ReplaceRunWithPathLinks(Run run, IReadOnlyList<PathMatch> matches)
    {
        InlineCollection? inlines = run.Parent switch
        {
            Paragraph paragraph => paragraph.Inlines,
            Span span => span.Inlines,
            _ => null
        };
        if (inlines is null)
        {
            return;
        }

        int textIndex = 0;
        foreach (PathMatch match in matches)
        {
            if (match.Start > textIndex)
            {
                inlines.InsertBefore(run, CreateFormattedRun(run.Text[textIndex..match.Start], run));
            }

            Hyperlink link = new(new Run(run.Text.Substring(match.Start, match.Length)))
            {
                Cursor = Cursors.Hand,
                Foreground = PathLinkBrush,
                Tag = match.Path,
                TextDecorations = TextDecorations.Underline,
                ToolTip = File.Exists(match.Path) ? "在资源管理器中显示文件" : "在资源管理器中打开目录"
            };
            CopyRunFormatting(run, link, copyForeground: false);
            link.Click += FileSystemPathLink_Click;
            inlines.InsertBefore(run, link);
            textIndex = match.Start + match.Length;
        }

        if (textIndex < run.Text.Length)
        {
            inlines.InsertBefore(run, CreateFormattedRun(run.Text[textIndex..], run));
        }

        inlines.Remove(run);
    }

    private static IEnumerable<Run> FindRuns(BlockCollection blocks)
    {
        foreach (Block block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    foreach (Run run in FindRuns(paragraph.Inlines))
                    {
                        yield return run;
                    }
                    break;
                case Section section:
                    foreach (Run run in FindRuns(section.Blocks))
                    {
                        yield return run;
                    }
                    break;
                case System.Windows.Documents.List list:
                    foreach (ListItem item in list.ListItems)
                    {
                        foreach (Run run in FindRuns(item.Blocks))
                        {
                            yield return run;
                        }
                    }
                    break;
                case Table table:
                    foreach (TableRowGroup rowGroup in table.RowGroups)
                    {
                        foreach (TableRow row in rowGroup.Rows)
                        {
                            foreach (TableCell cell in row.Cells)
                            {
                                foreach (Run run in FindRuns(cell.Blocks))
                                {
                                    yield return run;
                                }
                            }
                        }
                    }
                    break;
            }
        }
    }

    private static IEnumerable<Run> FindRuns(InlineCollection inlines)
    {
        foreach (Inline inline in inlines)
        {
            if (inline is Run run)
            {
                yield return run;
            }
            else if (inline is Span and not Hyperlink)
            {
                foreach (Run nestedRun in FindRuns(((Span)inline).Inlines))
                {
                    yield return nestedRun;
                }
            }
        }
    }

    private static Run CreateFormattedRun(string text, Run source)
    {
        Run run = new(text);
        CopyRunFormatting(source, run, copyForeground: true);
        return run;
    }

    private static void CopyRunFormatting(Run source, Inline target, bool copyForeground)
    {
        target.Background = source.Background;
        target.BaselineAlignment = source.BaselineAlignment;
        target.FontFamily = source.FontFamily;
        target.FontSize = source.FontSize;
        target.FontStretch = source.FontStretch;
        target.FontStyle = source.FontStyle;
        target.FontWeight = source.FontWeight;
        if (copyForeground)
        {
            target.Foreground = source.Foreground;
        }
    }

    private IReadOnlyList<PathMatch> FindExistingPathMatches(string text)
    {
        List<PathMatch> matches = [];
        int searchIndex = 0;
        while (searchIndex < text.Length)
        {
            Match match = WindowsPathPattern.Match(text, searchIndex);
            if (!match.Success)
            {
                break;
            }

            ResolvedPath? resolvedPath = ResolvePath(match.Value);
            if (resolvedPath is null)
            {
                searchIndex = match.Index + 1;
                continue;
            }

            matches.Add(new PathMatch(match.Index, resolvedPath.Value.DisplayPath.Length, resolvedPath.Value.ActualPath));
            searchIndex = match.Index + resolvedPath.Value.DisplayPath.Length;
        }

        return matches;
    }

    private ResolvedPath? ResolvePath(string candidate)
    {
        foreach (string displayPath in GetPathCandidates(candidate))
        {
            string? existingPath = FindExistingPathPrefix(displayPath);
            if (existingPath is not null)
            {
                return new ResolvedPath(existingPath, existingPath);
            }

            if (displayPath.Contains("...", StringComparison.Ordinal) || displayPath.Contains('…'))
            {
                string? resolvedPath = ResolveAbbreviatedPath(displayPath);
                if (resolvedPath is not null)
                {
                    return new ResolvedPath(displayPath, resolvedPath);
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> GetPathCandidates(string candidate)
    {
        string trimmed = candidate.TrimEnd();
        yield return trimmed;

        int annotationIndex = trimmed.LastIndexOf(" (", StringComparison.Ordinal);
        if (annotationIndex > 2)
        {
            yield return trimmed[..annotationIndex].TrimEnd();
        }
    }

    private static string? FindExistingPathPrefix(string candidate)
    {
        for (int length = candidate.Length; length >= 3; length--)
        {
            string path = candidate[..length].TrimEnd();
            if (path.Length < 3 || (!Directory.Exists(path) && !File.Exists(path)))
            {
                continue;
            }

            if (path.Length == candidate.Length || IsAnnotationBoundary(candidate, path.Length))
            {
                return path;
            }
        }

        return null;
    }

    private static bool IsAnnotationBoundary(string candidate, int index)
    {
        if (index >= candidate.Length || candidate[index] is '(' or '[' or '{')
        {
            return true;
        }

        if (!char.IsWhiteSpace(candidate[index]))
        {
            return false;
        }

        while (index < candidate.Length && char.IsWhiteSpace(candidate[index]))
        {
            index++;
        }

        return index >= candidate.Length || candidate[index] is '(' or '[' or '{';
    }

    private string? ResolveAbbreviatedPath(string displayPath)
    {
        if (_resolvedAbbreviatedPaths.TryGetValue(displayPath, out string? cachedPath))
        {
            return cachedPath;
        }

        string normalizedPath = displayPath.Replace("…", "...", StringComparison.Ordinal);
        int ellipsisIndex = normalizedPath.IndexOf("...", StringComparison.Ordinal);
        int separatorIndex = normalizedPath.LastIndexOf('\\', ellipsisIndex);
        if (ellipsisIndex < 0 || separatorIndex < 2)
        {
            _resolvedAbbreviatedPaths[displayPath] = null;
            return null;
        }

        string searchRoot = normalizedPath[..separatorIndex];
        if (!Directory.Exists(searchRoot) || searchRoot.Length <= 3)
        {
            _resolvedAbbreviatedPaths[displayPath] = null;
            return null;
        }

        string pattern = "^" + Regex.Escape(normalizedPath).Replace(@"\.\.\.", ".*") + "$";
        Regex pathPattern = new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        bool recursive = normalizedPath.IndexOf('\\', ellipsisIndex + 3) >= 0;
        EnumerationOptions options = new()
        {
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
            RecurseSubdirectories = recursive,
            ReturnSpecialDirectories = false
        };

        string? matchedPath = null;
        int scannedCount = 0;
        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(searchRoot, "*", options))
            {
                if (++scannedCount > 20_000)
                {
                    matchedPath = searchRoot;
                    break;
                }

                if (!pathPattern.IsMatch(entry))
                {
                    continue;
                }

                if (matchedPath is not null)
                {
                    matchedPath = searchRoot;
                    break;
                }

                matchedPath = entry;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            matchedPath = searchRoot;
        }

        matchedPath ??= searchRoot;
        _resolvedAbbreviatedPaths[displayPath] = matchedPath;
        return matchedPath;
    }

    private void FileSystemPathLink_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Hyperlink { Tag: string path })
        {
            return;
        }

        try
        {
            bool isDirectory = Directory.Exists(path);
            if (!isDirectory && !File.Exists(path))
            {
                MessageBox.Show(this, "路径已经不存在。", "无法打开路径", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = isDirectory ? $"\"{path}\"" : $"/select,\"{path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法打开资源管理器", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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

    private sealed class ResponseSection
    {
        public ResponseSection(string prefix, string content = "", string? question = null)
        {
            Prefix = prefix;
            Question = question;
            Content.Append(content);
        }

        public string Prefix { get; }

        public string? Question { get; }

        public StringBuilder Content { get; } = new();

        public StringBuilder Reasoning { get; } = new();

        public FrameworkElement? UserMessage { get; set; }

        public FrameworkElement? AssistantMessage { get; set; }

        public StackPanel? AssistantBody { get; set; }

        public FrameworkElement? ReasoningPanel { get; set; }

        public bool IsReasoningExpanded { get; set; } = true;

        public MarkdownScrollViewer? ReasoningViewer { get; set; }

        public MarkdownScrollViewer? AnswerViewer { get; set; }

        public TextBlock? StatusText { get; set; }

        public string RenderedReasoning { get; set; } = string.Empty;

        public string RenderedContent { get; set; } = string.Empty;

        public double ReasoningVerticalOffset { get; set; }

        public bool FollowReasoningOutput { get; set; } = true;
    }

    private readonly record struct PathMatch(int Start, int Length, string Path);

    private readonly record struct ResolvedPath(string DisplayPath, string ActualPath);

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

    public void SetFollowUpBusy(bool isBusy) => RunOnUiThread(() =>
    {
        FollowUpTextBox.IsEnabled = !isBusy;
        FollowUpButton.IsEnabled = !isBusy;
        FollowUpButton.Content = isBusy ? "回答中…" : "追问";
        FollowUpPlaceholder.Text = isBusy
            ? "正在生成回答…"
            : "输入追问，Enter 发送，Shift+Enter 换行";
    });

    public void RestoreFollowUpQuestion(string question) => RunOnUiThread(() =>
    {
        SetFollowUpBusy(false);
        FollowUpTextBox.Text = question;
        FollowUpTextBox.CaretIndex = FollowUpTextBox.Text.Length;
        FollowUpTextBox.Focus();
    });

    private void FollowUpButton_Click(object sender, RoutedEventArgs e) =>
        SubmitFollowUp();

    private void FollowUpTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (e.KeyboardDevice.Modifiers & ModifierKeys.Shift) == 0)
        {
            SubmitFollowUp();
            e.Handled = true;
        }
    }

    private void SubmitFollowUp()
    {
        string question = FollowUpTextBox.Text.Trim();
        EventHandler<FollowUpRequestedEventArgs>? handler = FollowUpRequested;
        if (!FollowUpTextBox.IsEnabled || question.Length == 0 || handler is null)
        {
            return;
        }

        FollowUpTextBox.Clear();
        SetFollowUpBusy(true);
        handler.Invoke(this, new FollowUpRequestedEventArgs(question));
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

public sealed class FollowUpRequestedEventArgs : EventArgs
{
    public FollowUpRequestedEventArgs(string question)
    {
        Question = question;
    }

    public string Question { get; }
}
