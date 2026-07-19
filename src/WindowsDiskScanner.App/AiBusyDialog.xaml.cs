using System.Windows;
using System.Windows.Input;

namespace WindowsDiskScanner.App;

public partial class AiBusyDialog : Window
{
    public AiBusyDialog()
    {
        InitializeComponent();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        Close();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }
}
