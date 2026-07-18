using System.Windows;

namespace WindowsDiskScanner.App;

public partial class AiResultWindow : Window
{
    public AiResultWindow(string title, string modelName, string content)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        ModelText.Text = $"模型：{modelName}";
        ContentTextBox.Text = content;
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(ContentTextBox.Text);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        Close();
}
