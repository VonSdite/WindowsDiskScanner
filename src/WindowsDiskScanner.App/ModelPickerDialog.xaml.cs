using System.ComponentModel;
using System.Windows;
using System.Windows.Data;

namespace WindowsDiskScanner.App;

public partial class ModelPickerDialog : Window
{
    private readonly ModelPickerItem[] _items;
    private ICollectionView? _view;
    private bool _syncingSelectAll;

    public ModelPickerDialog(IEnumerable<string> fetchedModels, IEnumerable<string> existingModels)
    {
        InitializeComponent();
        HashSet<string> existing = existingModels
            .Select(model => model.Trim())
            .Where(model => model.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        _items = fetchedModels
            .Select(model => model.Trim())
            .Where(model => model.Length > 0 && seen.Add(model))
            .Select(model => new ModelPickerItem(model, existing.Contains(model)))
            .ToArray();
        foreach (ModelPickerItem item in _items)
        {
            item.PropertyChanged += Item_PropertyChanged;
        }

        ModelsList.ItemsSource = _items;
        _view = CollectionViewSource.GetDefaultView(ModelsList.ItemsSource);
        _view.Filter = FilterItem;
        UpdateSummaryAndSelectAll();
    }

    public IReadOnlyList<string> SelectedModels =>
        _items.Where(item => item.IsSelected).Select(item => item.Name).ToArray();

    private bool FilterItem(object item)
    {
        string query = SearchTextBox.Text.Trim();
        return query.Length == 0 ||
               item is ModelPickerItem model && model.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void SearchTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _view?.Refresh();
        UpdateSummaryAndSelectAll();
    }

    private void SelectAllCheckBox_StateChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingSelectAll)
        {
            return;
        }

        ModelPickerItem[] visibleItems = _items.Where(item => FilterItem(item)).ToArray();
        bool select = visibleItems.Any(item => !item.IsSelected);
        _syncingSelectAll = true;
        foreach (ModelPickerItem item in visibleItems)
        {
            item.IsSelected = select;
        }

        _syncingSelectAll = false;
        UpdateSummaryAndSelectAll();
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_syncingSelectAll && e.PropertyName == nameof(ModelPickerItem.IsSelected))
        {
            UpdateSummaryAndSelectAll();
        }
    }

    private void UpdateSummaryAndSelectAll()
    {
        ModelPickerItem[] visible = _items.Where(item => FilterItem(item)).ToArray();
        int selectedCount = _items.Count(item => item.IsSelected);
        int existingCount = _items.Count(item => item.IsExisting);
        SummaryText.Text = $"共 {_items.Length} 个模型，已选 {selectedCount} 个，其中 {existingCount} 个已在清单";

        int selectedVisibleCount = visible.Count(item => item.IsSelected);
        _syncingSelectAll = true;
        SelectAllCheckBox.IsEnabled = visible.Length > 0;
        SelectAllCheckBox.IsChecked = visible.Length == 0 || selectedVisibleCount == 0
            ? false
            : selectedVisibleCount == visible.Length
                ? true
                : null;
        _syncingSelectAll = false;
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e) =>
        DialogResult = true;

    private sealed class ModelPickerItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public ModelPickerItem(string name, bool isExisting)
        {
            Name = name;
            IsExisting = isExisting;
            _isSelected = isExisting;
        }

        public string Name { get; }

        public bool IsExisting { get; }

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
