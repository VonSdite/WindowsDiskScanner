using System.Windows;
using System.Windows.Input;

namespace WindowsDiskScanner.App;

public partial class DeleteConfirmationDialog : Window
{
    public DeleteConfirmationDialog(ScanNode node)
    {
        InitializeComponent();
        PathText.Text = node.FullPath;
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
