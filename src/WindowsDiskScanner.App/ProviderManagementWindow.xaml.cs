using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace WindowsDiskScanner.App;

public partial class ProviderManagementWindow : Window
{
    private readonly ProviderStore _store;
    private readonly OpenAiChatClient _client;
    private Point _dragStartPoint;
    private LlmProvider? _draggedProvider;
    private ListBoxItem? _draggedProviderContainer;
    private ListBoxItem? _dropTargetContainer;
    private AdornerLayer? _dropAdornerLayer;
    private DropInsertionAdorner? _dropInsertionAdorner;
    private bool _dropAfterTarget;
    private ProviderEditorDialog? _editor;

    public ProviderManagementWindow(ProviderStore store, OpenAiChatClient client)
    {
        InitializeComponent();
        _store = store;
        _client = client;
        ProvidersList.ItemsSource = store.Providers;
        _store.Providers.CollectionChanged += Providers_CollectionChanged;
        Closed += (_, _) => _store.Providers.CollectionChanged -= Providers_CollectionChanged;
        UpdateEmptyState();
    }

    private void AddProviderButton_Click(object sender, RoutedEventArgs e) =>
        OpenEditor(provider: null);

    private void EditProviderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LlmProvider provider })
        {
            OpenEditor(provider);
        }
    }

    private void DeleteProviderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LlmProvider provider })
        {
            return;
        }

        MessageBoxResult result = MessageBox.Show(
            this,
            $"确定删除 Provider“{provider.Name}”吗？",
            "删除 Provider",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _store.Remove(provider);
            StatusText.Text = $"已删除 Provider：{provider.Name}";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "删除失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ProvidersList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProvidersList.SelectedItem is LlmProvider provider && !IsInteractiveElement(e.OriginalSource as DependencyObject))
        {
            OpenEditor(provider);
        }
    }

    private void OpenEditor(LlmProvider? provider)
    {
        if (_editor is not null)
        {
            return;
        }

        _editor = new ProviderEditorDialog(_store, _client, provider);
        _editor.CloseRequested += Editor_CloseRequested;
        EditorHost.Content = _editor;
        EditorHost.Visibility = Visibility.Visible;
        ProviderListPage.Visibility = Visibility.Collapsed;
        Title = provider is null ? "新增 Provider" : $"编辑 Provider：{provider.Name}";
    }

    private void Editor_CloseRequested(object? sender, EventArgs e)
    {
        if (_editor is null)
        {
            return;
        }

        _editor.CloseRequested -= Editor_CloseRequested;
        EditorHost.Content = null;
        EditorHost.Visibility = Visibility.Collapsed;
        ProviderListPage.Visibility = Visibility.Visible;
        _editor = null;
        Title = "LLM Provider 管理";
        UpdateEmptyState();
    }

    private void ProvidersList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(ProvidersList);
        _draggedProviderContainer = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        _draggedProvider = _draggedProviderContainer?.DataContext as LlmProvider;
    }

    private void ProvidersList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed ||
            _draggedProvider is null ||
            IsInteractiveElement(e.OriginalSource as DependencyObject))
        {
            return;
        }

        Point currentPoint = e.GetPosition(ProvidersList);
        if (Math.Abs(currentPoint.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(currentPoint.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        try
        {
            _draggedProviderContainer?.SetCurrentValue(UIElement.OpacityProperty, 0.55);
            DragDrop.DoDragDrop(ProvidersList, _draggedProvider, DragDropEffects.Move);
        }
        finally
        {
            _draggedProviderContainer?.ClearValue(UIElement.OpacityProperty);
            _draggedProviderContainer = null;
            _draggedProvider = null;
            ClearDropPreview();
        }
    }

    private void ProvidersList_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(LlmProvider)))
        {
            e.Effects = DragDropEffects.None;
            ClearDropPreview();
            return;
        }

        e.Effects = DragDropEffects.Move;
        ListBoxItem? targetContainer = ResolveDropTarget(e, out bool insertAfter);
        if (targetContainer is null)
        {
            ClearDropPreview();
        }
        else
        {
            ShowDropPreview(targetContainer, insertAfter);
        }

        e.Handled = true;
    }

    private void ProvidersList_DragLeave(object sender, DragEventArgs e)
    {
        Point position = e.GetPosition(ProvidersList);
        if (position.X < 0 || position.Y < 0 || position.X > ProvidersList.ActualWidth || position.Y > ProvidersList.ActualHeight)
        {
            ClearDropPreview();
        }
    }

    private void ProvidersList_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetData(typeof(LlmProvider)) is not LlmProvider draggedProvider)
            {
                return;
            }

            ListBoxItem? targetContainer = _dropTargetContainer ?? ResolveDropTarget(e, out _dropAfterTarget);
            if (targetContainer?.DataContext is not LlmProvider targetProvider)
            {
                return;
            }

            int oldIndex = _store.Providers.IndexOf(draggedProvider);
            int targetIndex = _store.Providers.IndexOf(targetProvider);
            if (_dropAfterTarget)
            {
                targetIndex++;
            }

            if (oldIndex < targetIndex)
            {
                targetIndex--;
            }

            targetIndex = Math.Clamp(targetIndex, 0, _store.Providers.Count - 1);
            if (oldIndex != targetIndex)
            {
                _store.Move(oldIndex, targetIndex);
                StatusText.Text = "Provider 顺序已保存。";
            }
        }
        finally
        {
            ClearDropPreview();
        }
    }

    private ListBoxItem? ResolveDropTarget(DragEventArgs e, out bool insertAfter)
    {
        ListBoxItem? targetContainer = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (targetContainer is not null)
        {
            insertAfter = e.GetPosition(targetContainer).Y > targetContainer.ActualHeight / 2;
            return targetContainer;
        }

        insertAfter = false;
        if (ProvidersList.Items.Count == 0)
        {
            return null;
        }

        ListBoxItem? firstContainer = ProvidersList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
        if (firstContainer is not null)
        {
            double firstTop = firstContainer.TranslatePoint(new Point(), ProvidersList).Y;
            if (e.GetPosition(ProvidersList).Y < firstTop)
            {
                return firstContainer;
            }
        }

        insertAfter = true;
        return ProvidersList.ItemContainerGenerator.ContainerFromIndex(ProvidersList.Items.Count - 1) as ListBoxItem;
    }

    private void ShowDropPreview(ListBoxItem targetContainer, bool insertAfter)
    {
        if (ReferenceEquals(_dropTargetContainer, targetContainer) && _dropAfterTarget == insertAfter)
        {
            return;
        }

        ClearDropPreview();
        AdornerLayer? adornerLayer = AdornerLayer.GetAdornerLayer(targetContainer);
        if (adornerLayer is null)
        {
            return;
        }

        _dropTargetContainer = targetContainer;
        _dropAfterTarget = insertAfter;
        _dropAdornerLayer = adornerLayer;
        _dropInsertionAdorner = new DropInsertionAdorner(targetContainer, insertAfter);
        adornerLayer.Add(_dropInsertionAdorner);
    }

    private void ClearDropPreview()
    {
        if (_dropAdornerLayer is not null && _dropInsertionAdorner is not null)
        {
            _dropAdornerLayer.Remove(_dropInsertionAdorner);
        }

        _dropTargetContainer = null;
        _dropAdornerLayer = null;
        _dropInsertionAdorner = null;
        _dropAfterTarget = false;
    }

    private void UpdateEmptyState() =>
        EmptyState.Visibility = _store.Providers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Providers_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        UpdateEmptyState();

    private static bool IsInteractiveElement(DependencyObject? source) =>
        FindAncestor<Button>(source) is not null ||
        FindAncestor<TextBox>(source) is not null ||
        FindAncestor<PasswordBox>(source) is not null;

    private static T? FindAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }
}
