using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
    private CancellationTokenSource? _scanCancellation;
    private ScanResult? _currentResult;
    private bool _fileOperationInProgress;

    public MainWindow()
    {
        InitializeComponent();
        Rows = [];
        DataContext = this;
    }

    public List<TreeRow> Rows { get; }

    private async void ScanButton_Click(object sender, RoutedEventArgs e) =>
        await StartScanAsync();

    private void CancelButton_Click(object sender, RoutedEventArgs e) =>
        _scanCancellation?.Cancel();

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
            row.IsSelected = true;
            row.Focus();
        }
    }

    private void DirectoryGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (Mouse.DirectlyOver is not DependencyObject source || FindDataGridRow(source) is null)
        {
            e.Handled = true;
        }
    }

    private void ShowInExplorerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (DirectoryGrid.SelectedItem is not TreeRow row)
        {
            return;
        }

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

    private async void MoveToRecycleBinMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCancellation is not null ||
            _fileOperationInProgress ||
            DirectoryGrid.SelectedItem is not TreeRow row)
        {
            return;
        }

        DeleteConfirmationDialog dialog = new(row.Node)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        bool deleted = false;
        _fileOperationInProgress = true;
        SetFileOperationState(isBusy: true);
        StatusText.Text = $"正在移到回收站：{row.Node.FullPath}";
        Mouse.OverrideCursor = Cursors.Wait;

        try
        {
            await Task.Run(() => MoveNodeToRecycleBin(row.Node));
            deleted = true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消删除";
        }
        catch (Exception exception)
        {
            StatusText.Text = "删除失败";
            MessageBox.Show(this, exception.Message, "无法移到回收站", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            _fileOperationInProgress = false;
            SetFileOperationState(isBusy: false);
        }

        if (!deleted)
        {
            return;
        }

        if (row.Depth == 0)
        {
            ClearResultsAfterRootDeletion(row.Node.FullPath);
        }
        else
        {
            UpdateResultsAfterDeletion(row);
        }
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

    private void SetFileOperationState(bool isBusy)
    {
        ScanButton.IsEnabled = !isBusy;
        RootPathTextBox.IsEnabled = !isBusy;
        DirectoryGrid.IsEnabled = !isBusy;
        CancelButton.IsEnabled = false;
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

    private void Window_Closing(object? sender, CancelEventArgs e) =>
        _scanCancellation?.Cancel();
}
