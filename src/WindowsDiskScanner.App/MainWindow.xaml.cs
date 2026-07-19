using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;

namespace WindowsDiskScanner.App;

public partial class MainWindow : Window
{
    private readonly DiskScanner _scanner = new();
    private readonly ByteSizeConverter _byteSizeConverter = new();
    private readonly AiHistoryStore _aiHistoryStore;
    private readonly ProviderStore _providerStore;
    private readonly OpenAiChatClient _openAiChatClient;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _aiCancellation;
    private ScanResult? _currentResult;
    private bool _aiOperationInProgress;
    private bool _fileOperationInProgress;
    private AiHistoryWindow? _aiHistoryWindow;

    public MainWindow()
    {
        InitializeComponent();
        App app = (App)Application.Current;
        _aiHistoryStore = app.AiHistoryStore;
        _providerStore = app.ProviderStore;
        _openAiChatClient = app.OpenAiChatClient;
        Rows = [];
        DataContext = this;
        _providerStore.Changed += ProviderStore_Changed;
        Loaded += (_, _) => RefreshAiModelOptions();
        Closed += (_, _) => _providerStore.Changed -= ProviderStore_Changed;
    }

    public List<TreeRow> Rows { get; }

    private async void ScanButton_Click(object sender, RoutedEventArgs e) =>
        await StartScanAsync();

    private void CancelButton_Click(object sender, RoutedEventArgs e) =>
        _scanCancellation?.Cancel();

    private void ProviderManagementButton_Click(object sender, RoutedEventArgs e)
    {
        ProviderManagementWindow window = new(_providerStore, _openAiChatClient)
        {
            Owner = this
        };
        window.ShowDialog();
        RefreshAiModelOptions();
    }

    private void AiHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_aiHistoryWindow is { IsVisible: true })
        {
            _aiHistoryWindow.Activate();
            return;
        }

        AiHistoryWindow window = new(_aiHistoryStore);
        _aiHistoryWindow = window;
        window.Closed += (_, _) => _aiHistoryWindow = null;
        window.Show();
    }

    private void ProviderStore_Changed(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            RefreshAiModelOptions();
        }
        else
        {
            Dispatcher.Invoke(RefreshAiModelOptions);
        }
    }

    private void RefreshAiModelOptions()
    {
        string? currentKey = AiModelComboBox.SelectedItem is AiModelOption current
            ? $"{current.Provider.Id}/{current.Model.Name}"
            : null;
        IReadOnlyList<AiModelOption> options = _providerStore.GetModelOptions();
        AiModelComboBox.ItemsSource = options;
        AiModelComboBox.SelectedItem = options.FirstOrDefault(option =>
            string.Equals($"{option.Provider.Id}/{option.Model.Name}", currentKey, StringComparison.OrdinalIgnoreCase))
            ?? options.FirstOrDefault();
        AiModelComboBox.ToolTip = options.Count == 0 ? "请先配置 Provider 和模型" : null;
        UpdateAiActionState();
    }

    private void AiModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateAiActionState();

    private async void GenerateReportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentResult is null || AiModelComboBox.SelectedItem is not AiModelOption option)
        {
            return;
        }

        await RunAiRequestAsync(
            "AI 磁盘分析报告",
            $"磁盘分析报告 · {_currentResult.Root.FullPath}",
            AiHistoryKind.Report,
            option,
            AiPromptBuilder.BuildScanReport(_currentResult));
    }

    private void RootPathTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        OpenFolderDialog dialog = new()
        {
            Title = "选择要扫描的目录"
        };

        if (Directory.Exists(RootPathTextBox.Text))
        {
            dialog.InitialDirectory = RootPathTextBox.Text;
        }

        if (dialog.ShowDialog(this) == true)
        {
            RootPathTextBox.Text = dialog.FolderName;
        }
    }

    private async Task StartScanAsync()
    {
        if (_scanCancellation is not null || _fileOperationInProgress)
        {
            return;
        }

        string path = RootPathTextBox.Text.Trim();
        if (!Directory.Exists(path))
        {
            MessageBox.Show(this, "请选择一个存在的目录。", "目录无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _scanCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _scanCancellation.Token;
        SetScanningState(isScanning: true);
        _currentResult = null;
        Rows.Clear();
        DirectoryGrid.Items.Refresh();
        ResetSummary();

        Progress<ScanProgress> progress = new(UpdateProgress);

        try
        {
            ScanResult result = await _scanner.ScanAsync(path, progress, cancellationToken);
            _currentResult = result;
            Rows.Add(new TreeRow(result.Root, depth: 0, result.Root.SizeBytes));
            DirectoryGrid.Items.Refresh();
            EmptyState.Visibility = Visibility.Collapsed;
            ShowResult(result);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "扫描已取消";
            EmptyState.Visibility = Visibility.Visible;
        }
        catch (Exception exception)
        {
            StatusText.Text = "扫描失败";
            EmptyState.Visibility = Visibility.Visible;
            MessageBox.Show(this, exception.Message, "扫描失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _scanCancellation.Dispose();
            _scanCancellation = null;
            SetScanningState(isScanning: false);
        }
    }

    private void ExpanderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TreeRow row })
        {
            return;
        }

        ToggleRow(row);
        e.Handled = true;
    }

    private void DirectoryRow_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGridRow { DataContext: TreeRow row } ||
            !row.Node.IsDirectory ||
            e.OriginalSource is not DependencyObject source ||
            IsInsideButton(source))
        {
            return;
        }

        ToggleRow(row);
        e.Handled = true;
    }

    private void DirectoryRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGridRow row)
        {
            if (!row.IsSelected)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
                {
                    DirectoryGrid.SelectedItems.Clear();
                }

                row.IsSelected = true;
            }

            row.Focus();
        }
    }

    private void DirectoryGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (Mouse.DirectlyOver is not DependencyObject source || FindDataGridRow(source) is null)
        {
            e.Handled = true;
            return;
        }

        int selectedCount = DirectoryGrid.SelectedItems.Count;
        ShowInExplorerMenuItem.Visibility = selectedCount == 1 ? Visibility.Visible : Visibility.Collapsed;
        AskAiMenuItem.IsEnabled = selectedCount > 0 && AiModelComboBox.SelectedItem is AiModelOption;
        MoveToRecycleBinMenuItem.Header = selectedCount > 1
            ? $"移到回收站（{selectedCount} 项）"
            : "移到回收站";
    }

    private void ShowInExplorerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedRows().Count != 1)
        {
            return;
        }

        TreeRow row = GetSelectedRows()[0];

        try
        {
            ProcessStartInfo startInfo = row.Node.IsDirectory
                ? new ProcessStartInfo(row.Node.FullPath)
                {
                    UseShellExecute = true
                }
                : new ProcessStartInfo("explorer.exe", $"/select,\"{row.Node.FullPath}\"")
                {
                    UseShellExecute = true
                };

            Process.Start(startInfo);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法打开资源管理器", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void AskAiMenuItem_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<TreeRow> selectedRows = GetSelectedRows();
        if (selectedRows.Count == 0 || AiModelComboBox.SelectedItem is not AiModelOption option)
        {
            return;
        }

        string subject = selectedRows.Count == 1
            ? selectedRows[0].Node.FullPath
            : $"{selectedRows.Count} 个项目 · {selectedRows[0].Node.Name}";
        await RunAiRequestAsync(
            "AI 文件解释",
            $"文件解释 · {subject}",
            AiHistoryKind.Inquiry,
            option,
            AiPromptBuilder.BuildNodeExplanation(selectedRows));
    }

    private async void MoveToRecycleBinMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCancellation is not null ||
            _fileOperationInProgress ||
            DirectoryGrid.SelectedItems.Count == 0)
        {
            return;
        }

        IReadOnlyList<TreeRow> selectedRows = GetTopLevelSelectedRows();
        if (selectedRows.Count == 0)
        {
            return;
        }

        DeleteConfirmationDialog dialog = new(selectedRows.Select(row => row.Node).ToArray())
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _fileOperationInProgress = true;
        SetFileOperationState(isBusy: true);
        StatusText.Text = selectedRows.Count == 1
            ? $"正在移到回收站：{selectedRows[0].Node.FullPath}"
            : $"正在将 {selectedRows.Count} 个项目移到回收站…";
        Mouse.OverrideCursor = Cursors.Wait;

        RecycleBatchResult recycleResult;

        try
        {
            recycleResult = await Task.Run(() => MoveNodesToRecycleBin(selectedRows));
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消删除";
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
            _fileOperationInProgress = false;
            SetFileOperationState(isBusy: false);
        }

        if (recycleResult.DeletedRows.Count == 0)
        {
            StatusText.Text = "删除失败";
            if (recycleResult.Errors.Count > 0)
            {
                MessageBox.Show(this, string.Join(Environment.NewLine, recycleResult.Errors), "无法移到回收站", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return;
        }

        TreeRow? deletedRoot = recycleResult.DeletedRows.FirstOrDefault(row => row.Depth == 0);
        if (deletedRoot is not null)
        {
            ClearResultsAfterRootDeletion(deletedRoot.Node.FullPath);
        }
        else
        {
            foreach (TreeRow deletedRow in recycleResult.DeletedRows.OrderByDescending(row => Rows.IndexOf(row)))
            {
                UpdateResultsAfterDeletion(deletedRow);
            }

            StatusText.Text = recycleResult.DeletedRows.Count == 1
                ? $"已移到回收站：{recycleResult.DeletedRows[0].Node.FullPath}"
                : $"已将 {recycleResult.DeletedRows.Count} 个项目移到回收站";
        }

        if (recycleResult.Errors.Count > 0)
        {
            MessageBox.Show(
                this,
                string.Join(Environment.NewLine, recycleResult.Errors),
                "部分项目未删除",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static RecycleBatchResult MoveNodesToRecycleBin(IReadOnlyList<TreeRow> rows)
    {
        List<TreeRow> deletedRows = [];
        List<string> errors = [];
        foreach (TreeRow row in rows)
        {
            try
            {
                MoveNodeToRecycleBin(row.Node);
                deletedRows.Add(row);
            }
            catch (Exception exception)
            {
                errors.Add($"{row.Node.FullPath}：{exception.Message}");
            }
        }

        return new RecycleBatchResult(deletedRows, errors);
    }

    private static void MoveNodeToRecycleBin(ScanNode node)
    {
        if (node.IsDirectory)
        {
            FileSystem.DeleteDirectory(
                node.FullPath,
                UIOption.OnlyErrorDialogs,
                RecycleOption.SendToRecycleBin,
                UICancelOption.ThrowException);
        }
        else
        {
            FileSystem.DeleteFile(
                node.FullPath,
                UIOption.OnlyErrorDialogs,
                RecycleOption.SendToRecycleBin,
                UICancelOption.ThrowException);
        }
    }

    private IReadOnlyList<TreeRow> GetSelectedRows() =>
        DirectoryGrid.SelectedItems
            .Cast<TreeRow>()
            .OrderBy(row => Rows.IndexOf(row))
            .ToArray();

    private IReadOnlyList<TreeRow> GetTopLevelSelectedRows()
    {
        IReadOnlyList<TreeRow> selectedRows = GetSelectedRows();
        HashSet<TreeRow> selectedSet = selectedRows.ToHashSet();
        List<TreeRow> result = [];
        foreach (TreeRow row in selectedRows)
        {
            int rowIndex = Rows.IndexOf(row);
            int ancestorDepth = row.Depth - 1;
            bool hasSelectedAncestor = false;
            for (int index = rowIndex - 1; index >= 0 && ancestorDepth >= 0; index--)
            {
                TreeRow candidate = Rows[index];
                if (candidate.Depth != ancestorDepth)
                {
                    continue;
                }

                if (selectedSet.Contains(candidate))
                {
                    hasSelectedAncestor = true;
                    break;
                }

                ancestorDepth--;
            }

            if (!hasSelectedAncestor)
            {
                result.Add(row);
            }
        }

        return result;
    }

    private async Task RunAiRequestAsync(
        string title,
        string historyTitle,
        AiHistoryKind historyKind,
        AiModelOption option,
        AiPrompt prompt)
    {
        if (_aiOperationInProgress)
        {
            return;
        }

        _aiOperationInProgress = true;
        CancellationTokenSource requestCancellation = new();
        _aiCancellation = requestCancellation;
        DateTimeOffset createdAt = DateTimeOffset.Now;
        StringBuilder streamedContent = new();
        StringBuilder streamedReasoning = new();
        LlmProvider providerSnapshot = option.Provider.Clone();
        List<ChatMessage> conversation =
        [
            new ChatMessage("system", prompt.SystemPrompt),
            new ChatMessage("user", prompt.UserPrompt)
        ];
        AiHistoryRecord? historyRecord = null;
        UpdateAiActionState();
        StatusText.Text = $"正在调用 AI：{option.DisplayName}";
        AiResultWindow resultWindow = new(title, option.DisplayName);
        async void FollowUpRequested(object? sender, FollowUpRequestedEventArgs e) =>
            await RunAiFollowUpAsync(
                resultWindow,
                providerSnapshot,
                option.Model.Name,
                option.DisplayName,
                conversation,
                historyRecord,
                e.Question);
        resultWindow.FollowUpRequested += FollowUpRequested;
        resultWindow.Closed += (_, _) => resultWindow.FollowUpRequested -= FollowUpRequested;
        EventHandler cancelOnClose = (_, _) => requestCancellation.Cancel();
        resultWindow.Closed += cancelOnClose;
        resultWindow.Show();
        try
        {
            await _openAiChatClient.SendChatStreamingAsync(
                providerSnapshot,
                option.Model.Name,
                prompt.SystemPrompt,
                prompt.UserPrompt,
                delta =>
                {
                    if (delta.ReasoningContent.Length > 0)
                    {
                        streamedReasoning.Append(delta.ReasoningContent);
                        resultWindow.AppendReasoning(delta.ReasoningContent);
                    }

                    if (delta.Content.Length > 0)
                    {
                        streamedContent.Append(delta.Content);
                        resultWindow.AppendContent(delta.Content);
                    }
                },
                requestCancellation.Token);
            conversation.Add(new ChatMessage("assistant", streamedContent.ToString()));
            if (resultWindow.IsVisible)
            {
                resultWindow.MarkCompleted();
            }

            try
            {
                historyRecord = _aiHistoryStore.Add(
                    historyKind,
                    historyTitle,
                    option.DisplayName,
                    createdAt,
                    streamedContent.ToString(),
                    streamedReasoning.ToString());
                StatusText.Text = $"AI 分析完成：{option.DisplayName}";
            }
            catch (Exception)
            {
                StatusText.Text = "AI 分析完成，历史保存失败";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "AI 请求已取消";
        }
        catch (Exception exception)
        {
            StatusText.Text = "AI 请求失败";
            if (resultWindow.IsVisible)
            {
                resultWindow.ShowError(exception.Message);
            }
        }
        finally
        {
            resultWindow.Closed -= cancelOnClose;
            if (ReferenceEquals(_aiCancellation, requestCancellation))
            {
                _aiCancellation = null;
            }

            requestCancellation.Dispose();
            _aiOperationInProgress = false;
            UpdateAiActionState();
        }
    }

    private async Task RunAiFollowUpAsync(
        AiResultWindow resultWindow,
        LlmProvider provider,
        string modelName,
        string modelDisplayName,
        List<ChatMessage> conversation,
        AiHistoryRecord? historyRecord,
        string question)
    {
        if (_aiOperationInProgress || !resultWindow.IsVisible)
        {
            resultWindow.SetFollowUpBusy(false);
            return;
        }

        _aiOperationInProgress = true;
        CancellationTokenSource requestCancellation = new();
        _aiCancellation = requestCancellation;
        ChatMessage userMessage = new("user", question);
        conversation.Add(userMessage);
        StringBuilder answer = new();
        EventHandler cancelOnClose = (_, _) => requestCancellation.Cancel();
        resultWindow.Closed += cancelOnClose;
        resultWindow.BeginFollowUp(question);
        UpdateAiActionState();
        StatusText.Text = $"正在追问 AI：{modelDisplayName}";
        try
        {
            await _openAiChatClient.SendChatStreamingAsync(
                provider,
                modelName,
                conversation,
                delta =>
                {
                    if (delta.ReasoningContent.Length > 0)
                    {
                        resultWindow.AppendReasoning(delta.ReasoningContent);
                    }

                    if (delta.Content.Length > 0)
                    {
                        answer.Append(delta.Content);
                        resultWindow.AppendContent(delta.Content);
                    }
                },
                requestCancellation.Token);

            conversation.Add(new ChatMessage("assistant", answer.ToString()));
            if (resultWindow.IsVisible)
            {
                resultWindow.MarkCompleted();
            }

            if (historyRecord is not null)
            {
                try
                {
                    _aiHistoryStore.UpdateContent(historyRecord, resultWindow.ResultContent, resultWindow.ReasoningContent);
                }
                catch (Exception)
                {
                    StatusText.Text = "AI 追问完成，历史更新失败";
                    return;
                }
            }

            StatusText.Text = $"AI 追问完成：{modelDisplayName}";
        }
        catch (OperationCanceledException)
        {
            RemovePendingUserMessage(conversation, userMessage);
            StatusText.Text = "AI 追问已取消";
            if (resultWindow.IsVisible)
            {
                resultWindow.MarkFollowUpCancelled();
            }
        }
        catch (Exception exception)
        {
            RemovePendingUserMessage(conversation, userMessage);
            StatusText.Text = "AI 追问失败";
            if (resultWindow.IsVisible)
            {
                resultWindow.ShowFollowUpError(exception.Message);
            }
        }
        finally
        {
            resultWindow.Closed -= cancelOnClose;
            if (ReferenceEquals(_aiCancellation, requestCancellation))
            {
                _aiCancellation = null;
            }

            requestCancellation.Dispose();
            _aiOperationInProgress = false;
            if (resultWindow.IsVisible)
            {
                resultWindow.SetFollowUpBusy(false);
            }

            UpdateAiActionState();
        }
    }

    private static void RemovePendingUserMessage(List<ChatMessage> conversation, ChatMessage userMessage)
    {
        if (conversation.Count > 0 && ReferenceEquals(conversation[^1], userMessage))
        {
            conversation.RemoveAt(conversation.Count - 1);
        }
    }

    private void UpdateAiActionState()
    {
        bool hasModel = AiModelComboBox.SelectedItem is AiModelOption;
        bool controlsAvailable = !_aiOperationInProgress && !_fileOperationInProgress && _scanCancellation is null;
        AiModelComboBox.IsEnabled = controlsAvailable && _providerStore.GetModelOptions().Count > 0;
        GenerateReportButton.IsEnabled = controlsAvailable && hasModel && _currentResult is not null;
    }

    private void SetFileOperationState(bool isBusy)
    {
        ScanButton.IsEnabled = !isBusy;
        RootPathTextBox.IsEnabled = !isBusy;
        DirectoryGrid.IsEnabled = !isBusy;
        CancelButton.IsEnabled = false;
        UpdateAiActionState();
    }

    private void ClearResultsAfterRootDeletion(string deletedPath)
    {
        _currentResult = null;
        RootPathTextBox.Text = string.Empty;
        Rows.Clear();
        DirectoryGrid.Items.Refresh();
        TotalSizeText.Text = "—";
        DirectoryCountText.Text = "—";
        FileCountText.Text = "—";
        ElapsedText.Text = "—";
        EmptyState.Visibility = Visibility.Visible;
        SkippedText.Text = string.Empty;
        StatusText.Text = $"已移到回收站：{deletedPath}";
        UpdateAiActionState();
    }

    private void UpdateResultsAfterDeletion(TreeRow deletedRow)
    {
        if (_currentResult is null)
        {
            return;
        }

        int rowIndex = Rows.IndexOf(deletedRow);
        if (rowIndex < 0)
        {
            return;
        }

        DeletedNodeStats deletedStats = CountNodeTree(deletedRow.Node);
        List<TreeRow> ancestors = [];
        int ancestorDepth = deletedRow.Depth - 1;
        for (int index = rowIndex - 1; index >= 0 && ancestorDepth >= 0; index--)
        {
            if (Rows[index].Depth == ancestorDepth)
            {
                ancestors.Add(Rows[index]);
                ancestorDepth--;
            }
        }

        if (ancestors.Count != deletedRow.Depth)
        {
            return;
        }

        TreeRow parentRow = ancestors[0];
        parentRow.Node.Children?.Remove(deletedRow.Node);
        foreach (TreeRow ancestor in ancestors)
        {
            ancestor.Node.SizeBytes = Math.Max(0, ancestor.Node.SizeBytes - deletedRow.Node.SizeBytes);
        }

        int removeCount = 1;
        while (rowIndex + removeCount < Rows.Count && Rows[rowIndex + removeCount].Depth > deletedRow.Depth)
        {
            removeCount++;
        }

        Rows.RemoveRange(rowIndex, removeCount);
        parentRow.RefreshChildrenState();

        long rootSizeBytes = _currentResult.Root.SizeBytes;
        foreach (TreeRow row in Rows)
        {
            row.UpdateRootSizeBytes(rootSizeBytes);
        }

        _currentResult = _currentResult with
        {
            DirectoryCount = Math.Max(0, _currentResult.DirectoryCount - deletedStats.DirectoryCount),
            FileCount = Math.Max(0, _currentResult.FileCount - deletedStats.FileCount),
            InaccessibleDirectoryCount = Math.Max(
                0,
                _currentResult.InaccessibleDirectoryCount - deletedStats.InaccessibleDirectoryCount)
        };

        TotalSizeText.Text = FormatBytes(rootSizeBytes);
        DirectoryCountText.Text = _currentResult.DirectoryCount.ToString("N0");
        FileCountText.Text = _currentResult.FileCount.ToString("N0");
        SkippedText.Text = _currentResult.InaccessibleDirectoryCount == 0
            ? string.Empty
            : $"{_currentResult.InaccessibleDirectoryCount:N0} 个目录无法读取";
        StatusText.Text = $"已移到回收站：{deletedRow.Node.FullPath}";
        DirectoryGrid.Items.Refresh();
    }

    private static DeletedNodeStats CountNodeTree(ScanNode root)
    {
        long directoryCount = 0;
        long fileCount = 0;
        long inaccessibleDirectoryCount = 0;
        Stack<ScanNode> pending = new();
        pending.Push(root);

        while (pending.TryPop(out ScanNode? node))
        {
            if (node.IsDirectory)
            {
                directoryCount++;
                if (!node.IsAccessible)
                {
                    inaccessibleDirectoryCount++;
                }

                if (node.Children is { } children)
                {
                    foreach (ScanNode child in children)
                    {
                        pending.Push(child);
                    }
                }
            }
            else
            {
                fileCount++;
            }
        }

        return new DeletedNodeStats(directoryCount, fileCount, inaccessibleDirectoryCount);
    }

    private void ToggleRow(TreeRow row)
    {
        int rowIndex = Rows.IndexOf(row);
        if (rowIndex < 0)
        {
            return;
        }

        if (row.IsExpanded)
        {
            int removeStart = rowIndex + 1;
            int removeCount = 0;
            while (removeStart + removeCount < Rows.Count && Rows[removeStart + removeCount].Depth > row.Depth)
            {
                removeCount++;
            }

            if (removeCount > 0)
            {
                Rows.RemoveRange(removeStart, removeCount);
            }

            row.IsExpanded = false;
        }
        else if (row.Node.Children is { Count: > 0 } children)
        {
            TreeRow[] childRows = children
                .Select(child => new TreeRow(child, row.Depth + 1, row.RootSizeBytes))
                .ToArray();

            Rows.InsertRange(rowIndex + 1, childRows);
            row.IsExpanded = true;
        }

        DirectoryGrid.Items.Refresh();
    }

    private static bool IsInsideButton(DependencyObject source)
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is Button)
            {
                return true;
            }

            if (current is DataGridRow)
            {
                return false;
            }
        }

        return false;
    }

    private static DataGridRow? FindDataGridRow(DependencyObject source)
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is DataGridRow row)
            {
                return row;
            }
        }

        return null;
    }

    private void UpdateProgress(ScanProgress progress)
    {
        DirectoryCountText.Text = progress.DirectoryCount.ToString("N0");
        FileCountText.Text = progress.FileCount.ToString("N0");
        TotalSizeText.Text = FormatBytes(progress.DiscoveredBytes);
        ElapsedText.Text = FormatElapsed(progress.Elapsed);
        StatusText.Text = $"正在扫描：{progress.CurrentPath}";
    }

    private void ShowResult(ScanResult result)
    {
        TotalSizeText.Text = FormatBytes(result.Root.SizeBytes);
        DirectoryCountText.Text = result.DirectoryCount.ToString("N0");
        FileCountText.Text = result.FileCount.ToString("N0");
        ElapsedText.Text = FormatElapsed(result.Elapsed);
        StatusText.Text = $"扫描完成：{result.Root.FullPath}";
        SkippedText.Text = result.InaccessibleDirectoryCount == 0
            ? string.Empty
            : $"{result.InaccessibleDirectoryCount:N0} 个目录无法读取";
        UpdateAiActionState();
    }

    private void SetScanningState(bool isScanning)
    {
        ScanButton.IsEnabled = !isScanning;
        RootPathTextBox.IsEnabled = !isScanning;
        CancelButton.IsEnabled = isScanning;
        ScanProgressBar.Visibility = isScanning ? Visibility.Visible : Visibility.Collapsed;
        if (isScanning)
        {
            EmptyState.Visibility = Visibility.Visible;
            StatusText.Text = "正在准备扫描…";
            SkippedText.Text = string.Empty;
        }

        UpdateAiActionState();
    }

    private void ResetSummary()
    {
        TotalSizeText.Text = "0 B";
        DirectoryCountText.Text = "0";
        FileCountText.Text = "0";
        ElapsedText.Text = "0.0 秒";
    }

    private string FormatBytes(long bytes) =>
        _byteSizeConverter.Convert(bytes, typeof(string), null!, System.Globalization.CultureInfo.CurrentCulture).ToString()!;

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalMinutes >= 1
            ? $"{(int)elapsed.TotalMinutes} 分 {elapsed.Seconds} 秒"
            : $"{elapsed.TotalSeconds:N1} 秒";

    private readonly record struct DeletedNodeStats(
        long DirectoryCount,
        long FileCount,
        long InaccessibleDirectoryCount);

    private readonly record struct RecycleBatchResult(
        IReadOnlyList<TreeRow> DeletedRows,
        IReadOnlyList<string> Errors);

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _scanCancellation?.Cancel();
        _aiCancellation?.Cancel();
    }
}
