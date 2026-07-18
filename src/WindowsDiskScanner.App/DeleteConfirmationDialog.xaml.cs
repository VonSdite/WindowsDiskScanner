using System.Windows;
using System.Windows.Input;

namespace WindowsDiskScanner.App;

public partial class DeleteConfirmationDialog : Window
{
    public DeleteConfirmationDialog(IReadOnlyList<ScanNode> nodes)
    {
        InitializeComponent();
        PromptText.Text = nodes.Count == 1
            ? "确定要移动以下项目吗？"
            : $"确定要移动以下 {nodes.Count} 个项目吗？";
        string[] visiblePaths = nodes.Take(12).Select(node => node.FullPath).ToArray();
        PathText.Text = string.Join(Environment.NewLine, visiblePaths);
        if (nodes.Count > visiblePaths.Length)
        {
            PathText.Text += $"{Environment.NewLine}…另有 {nodes.Count - visiblePaths.Length} 个项目";
        }
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e) =>
        DialogResult = true;

    private void CancelButton_Click(object sender, RoutedEventArgs e) =>
        DialogResult = false;

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }
}
