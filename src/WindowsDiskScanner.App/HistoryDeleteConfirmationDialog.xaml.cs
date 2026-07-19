using System.Windows;
using System.Windows.Input;

namespace WindowsDiskScanner.App;

public partial class HistoryDeleteConfirmationDialog : Window
{
    public HistoryDeleteConfirmationDialog(IReadOnlyList<AiHistoryRecord> records)
    {
        InitializeComponent();
        PromptText.Text = records.Count == 1
            ? "确定删除这条历史记录吗？"
            : $"确定删除所选的 {records.Count} 条历史记录吗？";
        string[] visibleTitles = records.Take(8).Select(record => record.Title).ToArray();
        RecordText.Text = string.Join(Environment.NewLine, visibleTitles);
        if (records.Count > visibleTitles.Length)
        {
            RecordText.Text += $"{Environment.NewLine}…另有 {records.Count - visibleTitles.Length} 条记录";
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
