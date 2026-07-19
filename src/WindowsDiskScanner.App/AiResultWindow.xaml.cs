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

namespace WindowsDiskScanner.App;

public partial class AiResultWindow : Window
{
    private static readonly Regex WindowsPathPattern = new(
        @"(?<![A-Za-z0-9_])(?:[A-Za-z]:\\|\\\\)[^\r\n`|<>""，。；：！？、（）]+",
        RegexOptions.Compiled);
    private static readonly FontFamily ReportFontFamily = new("Segoe UI, Microsoft YaHei UI");
    private static readonly Brush PathLinkBrush = new SolidColorBrush(Color.FromRgb(47, 111, 237));
    private readonly string _modelName;
    private readonly StringBuilder _content = new();
    private readonly StringBuilder _reasoning = new();
    private readonly Dictionary<string, string?> _resolvedAbbreviatedPaths = new(StringComparer.OrdinalIgnoreCase);
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
        ScrollViewer? contentScrollBeforeRender = FindDescendant<ScrollViewer>(ContentViewer);
        double contentVerticalOffset = contentScrollBeforeRender?.VerticalOffset ?? 0;
        bool followContentOutput = scrollToEnd && IsNearBottom(contentScrollBeforeRender);
        ScrollViewer? reasoningScrollBeforeRender = FindDescendant<ScrollViewer>(ReasoningViewer);
        double reasoningVerticalOffset = reasoningScrollBeforeRender?.VerticalOffset ?? 0;
        bool followReasoningOutput = scrollToEnd && IsNearBottom(reasoningScrollBeforeRender);

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
                if (!scrollToEnd)
                {
                    contentScrollViewer?.ScrollToHome();
                }
                else if (followContentOutput)
                {
                    contentScrollViewer?.ScrollToEnd();
                }
                else
                {
                    contentScrollViewer?.ScrollToVerticalOffset(contentVerticalOffset);
                }

                if (ReasoningExpander.IsExpanded)
                {
                    ScrollViewer? reasoningScrollViewer = FindDescendant<ScrollViewer>(ReasoningViewer);
                    if (!scrollToEnd)
                    {
                        reasoningScrollViewer?.ScrollToHome();
                    }
                    else if (followReasoningOutput)
                    {
                        reasoningScrollViewer?.ScrollToEnd();
                    }
                    else
                    {
                        reasoningScrollViewer?.ScrollToVerticalOffset(reasoningVerticalOffset);
                    }
                }
            },
            DispatcherPriority.Background);
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
        ApplyListStyles(document.Blocks, 0);
        LinkFileSystemPaths(document);
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
