using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace WindowsDiskScanner.App;

public partial class MainWindow : Window
{
    private readonly DiskScanner _scanner = new();
    private readonly ByteSizeConverter _byteSizeConverter = new();
    private CancellationTokenSource? _scanCancellation;

    public MainWindow()
    {
        InitializeComponent();
        Rows = [];
        DataContext = this;
        RootPathTextBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    }

    public List<TreeRow> Rows { get; }

    private async void ScanButton_Click(object sender, RoutedEventArgs e) =>
        await StartScanAsync();

    private void CancelButton_Click(object sender, RoutedEventArgs e) =>
        _scanCancellation?.Cancel();

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        OpenFolderDialog dialog = new()
        {
            Title = "选择要扫描的目录",
            InitialDirectory = Directory.Exists(RootPathTextBox.Text)
                ? RootPathTextBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.Windows)
        };

        if (dialog.ShowDialog(this) == true)
        {
            RootPathTextBox.Text = dialog.FolderName;
        }
    }

    private async Task StartScanAsync()
    {
        if (_scanCancellation is not null)
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
        Rows.Clear();
        DirectoryGrid.Items.Refresh();
        ResetSummary();

        Progress<ScanProgress> progress = new(UpdateProgress);

        try
        {
            ScanResult result = await _scanner.ScanAsync(path, progress, cancellationToken);
            Rows.Add(new TreeRow(result.Root, depth: 0));
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
                .Select(child => new TreeRow(child, row.Depth + 1))
                .ToArray();

            Rows.InsertRange(rowIndex + 1, childRows);
            row.IsExpanded = true;
        }

        DirectoryGrid.Items.Refresh();
        e.Handled = true;
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
        BrowseButton.IsEnabled = !isScanning;
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

    private void Window_Closing(object? sender, CancelEventArgs e) =>
        _scanCancellation?.Cancel();
}
