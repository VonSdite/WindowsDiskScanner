using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace WindowsDiskScanner.App;

public partial class AiHistoryWindow : Window
{
    private readonly AiHistoryStore _store;
    private readonly ObservableCollection<HistoryListItem> _items = [];
    private readonly ICollectionView _view;
    private bool _syncingSelectAll;

    public AiHistoryWindow(AiHistoryStore store)
    {
        InitializeComponent();
        _store = store;
        HistoryList.ItemsSource = _items;
        _view = CollectionViewSource.GetDefaultView(_items);
        _view.Filter = FilterItem;
        KindFilterComboBox.ItemsSource = new[]
        {
            new HistoryFilterOption(null, "全部类型"),
            new HistoryFilterOption(AiHistoryKind.Report, "分析报告"),
            new HistoryFilterOption(AiHistoryKind.Inquiry, "AI 询问")
        };
        KindFilterComboBox.SelectedIndex = 0;
        _store.Changed += Store_Changed;
        Closed += (_, _) => _store.Changed -= Store_Changed;
        StatusText.Text = $"保存位置：{_store.HistoryDirectory}";
        ReloadRecords();
    }

    private bool FilterItem(object item)
    {
        if (item is not HistoryListItem historyItem)
        {
            return false;
        }

        if (KindFilterComboBox.SelectedItem is HistoryFilterOption { Kind: { } kind } &&
            historyItem.Record.Kind != kind)
        {
            return false;
        }

        string query = SearchTextBox.Text.Trim();
        return query.Length == 0 ||
               historyItem.Record.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               historyItem.Record.ModelName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               historyItem.Record.Content.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void ReloadRecords()
    {
        HashSet<Guid> selectedIds = _items.Where(item => item.IsSelected).Select(item => item.Record.Id).ToHashSet();
        foreach (HistoryListItem item in _items)
        {
            item.PropertyChanged -= Item_PropertyChanged;
        }

        _items.Clear();
        foreach (AiHistoryRecord record in _store.LoadAll())
        {
            HistoryListItem item = new(record) { IsSelected = selectedIds.Contains(record.Id) };
            item.PropertyChanged += Item_PropertyChanged;
            _items.Add(item);
        }

        _view.Refresh();
        UpdateState();
    }

    private void Store_Changed(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            ReloadRecords();
        }
        else
        {
            Dispatcher.Invoke(ReloadRecords);
        }
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _view.Refresh();
        UpdateState();
    }

    private void KindFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _view?.Refresh();
        UpdateState();
    }

    private void SelectAllCheckBox_StateChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingSelectAll)
        {
            return;
        }

        HistoryListItem[] visibleItems = _view.Cast<HistoryListItem>().ToArray();
        bool select = visibleItems.Any(item => !item.IsSelected);
        _syncingSelectAll = true;
        foreach (HistoryListItem item in visibleItems)
        {
            item.IsSelected = select;
        }

        _syncingSelectAll = false;
        UpdateState();
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_syncingSelectAll && e.PropertyName == nameof(HistoryListItem.IsSelected))
        {
            UpdateState();
        }
    }

    private void UpdateState()
    {
        HistoryListItem[] visibleItems = _view.Cast<HistoryListItem>().ToArray();
        int selectedCount = _items.Count(item => item.IsSelected);
        int selectedVisibleCount = visibleItems.Count(item => item.IsSelected);
        SummaryText.Text = $"共 {_items.Count} 条记录，当前显示 {visibleItems.Length} 条，已选 {selectedCount} 条";
        EmptyState.Visibility = visibleItems.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryList.Visibility = visibleItems.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        DeleteSelectedButton.IsEnabled = selectedCount > 0;

        _syncingSelectAll = true;
        SelectAllCheckBox.IsEnabled = visibleItems.Length > 0;
        SelectAllCheckBox.IsChecked = visibleItems.Length == 0 || selectedVisibleCount == 0
            ? false
            : selectedVisibleCount == visibleItems.Length
                ? true
                : null;
        _syncingSelectAll = false;
    }

    private void OpenHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HistoryListItem item })
        {
            OpenRecord(item.Record);
        }
    }

    private void HistoryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (HistoryList.SelectedItem is HistoryListItem item &&
            FindAncestor<Button>(e.OriginalSource as DependencyObject) is null &&
            FindAncestor<CheckBox>(e.OriginalSource as DependencyObject) is null)
        {
            OpenRecord(item.Record);
        }
    }

    private void OpenRecord(AiHistoryRecord record)
    {
        AiResultWindow window = new(record.Title, record.ModelName);
        window.ShowCompletedContent(record.Content, record.ReasoningContent);
        window.Show();
    }

    private void DeleteHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HistoryListItem item })
        {
            DeleteRecords([item.Record]);
        }
    }

    private void DeleteSelectedButton_Click(object sender, RoutedEventArgs e) =>
        DeleteRecords(_items.Where(item => item.IsSelected).Select(item => item.Record).ToArray());

    private void DeleteRecords(IReadOnlyList<AiHistoryRecord> records)
    {
        if (records.Count == 0)
        {
            return;
        }

        HistoryDeleteConfirmationDialog dialog = new(records)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            int deletedCount = _store.Delete(records.Select(record => record.Id));
            StatusText.Text = $"已删除 {deletedCount} 条历史记录。";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"删除失败：{exception.Message}";
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        Close();

    private static T? FindAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        for (DependencyObject? current = source; current is not null; current = GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static DependencyObject? GetParent(DependencyObject source) =>
        source switch
        {
            ContentElement contentElement =>
                ContentOperations.GetParent(contentElement) ??
                (contentElement as FrameworkContentElement)?.Parent,
            Visual or Visual3D => VisualTreeHelper.GetParent(source),
            _ => LogicalTreeHelper.GetParent(source)
        };

    private sealed record HistoryFilterOption(AiHistoryKind? Kind, string DisplayName);

    private sealed class HistoryListItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public HistoryListItem(AiHistoryRecord record)
        {
            Record = record;
        }

        public AiHistoryRecord Record { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
