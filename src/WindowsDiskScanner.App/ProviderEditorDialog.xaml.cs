using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace WindowsDiskScanner.App;

public partial class ProviderEditorDialog : UserControl
{
    private readonly ProviderStore _store;
    private readonly OpenAiChatClient _client;
    private readonly bool _isNew;
    private readonly LlmProvider _provider;
    private readonly CancellationTokenSource _operationCancellation = new();
    private Point _dragStartPoint;
    private LlmModel? _draggedModel;
    private ListBoxItem? _draggedModelContainer;
    private ListBoxItem? _dropTargetContainer;
    private AdornerLayer? _dropAdornerLayer;
    private DropInsertionAdorner? _dropInsertionAdorner;
    private bool _dropAfterTarget;
    private bool _isBusy;

    public ProviderEditorDialog(ProviderStore store, OpenAiChatClient client, LlmProvider? provider)
    {
        InitializeComponent();
        _store = store;
        _client = client;
        _isNew = provider is null;
        _provider = provider?.Clone() ?? new LlmProvider();
        DataContext = _provider;

        TitleText.Text = _isNew ? "新增 Provider" : $"编辑 Provider：{_provider.Name}";
        ApiKeyBox.Password = _provider.ApiKey;
        VerifySslCheckBox.IsChecked = _provider.VerifySsl;
        ProxyModeComboBox.ItemsSource = new[]
        {
            new ProxyModeOption(ProviderProxyMode.Direct, "直连"),
            new ProxyModeOption(ProviderProxyMode.System, "系统代理"),
            new ProxyModeOption(ProviderProxyMode.Custom, "自定义代理")
        };
        ProxyModeComboBox.SelectedValue = _provider.ProxyMode;
        UpdateCustomProxyVisibility();
        Unloaded += (_, _) => _operationCancellation.Cancel();
    }

    public event EventHandler? CloseRequested;

    private void ProxyModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateCustomProxyVisibility();

    private void UpdateCustomProxyVisibility()
    {
        ProviderProxyMode mode = ProxyModeComboBox.SelectedValue is ProviderProxyMode selectedMode
            ? selectedMode
            : ProviderProxyMode.Direct;
        CustomProxyPanel.Visibility = mode == ProviderProxyMode.Custom ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddModelButton_Click(object sender, RoutedEventArgs e) =>
        AddModelFromInput();

    private void NewModelTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddModelFromInput();
            e.Handled = true;
        }
    }

    private void AddModelFromInput()
    {
        string name = NewModelTextBox.Text.Trim();
        if (name.Length == 0)
        {
            return;
        }

        if (_provider.Models.Any(model => string.Equals(model.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText.Text = $"模型已存在：{name}";
            return;
        }

        LlmModel model = new() { Name = name };
        _provider.Models.Add(model);
        ModelsList.SelectedItem = model;
        NewModelTextBox.Clear();
        StatusText.Text = $"已添加模型：{name}";
    }

    private void RemoveModelButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isBusy && sender is FrameworkElement { DataContext: LlmModel model })
        {
            _provider.Models.Remove(model);
            StatusText.Text = $"已移除模型：{model.Name}";
        }
    }

    private async void FetchModelsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        LlmProvider provider = ReadForm();
        if (!ValidateNetworkFields(provider))
        {
            return;
        }

        SetBusy(true, "正在拉取模型…");
        using CancellationTokenSource fetchCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(_operationCancellation.Token);
        fetchCancellation.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            IReadOnlyList<string> fetchedModels = await _client.FetchModelsAsync(provider, fetchCancellation.Token);
            ModelPickerDialog dialog = new(fetchedModels, _provider.Models.Select(model => model.Name))
            {
                Owner = Window.GetWindow(this)
            };
            if (dialog.ShowDialog() == true)
            {
                HashSet<string> fetchedSet = fetchedModels.ToHashSet(StringComparer.OrdinalIgnoreCase);
                HashSet<string> selectedSet = dialog.SelectedModels.ToHashSet(StringComparer.OrdinalIgnoreCase);
                LlmModel[] removedModels = _provider.Models
                    .Where(model => fetchedSet.Contains(model.Name.Trim()) && !selectedSet.Contains(model.Name.Trim()))
                    .ToArray();
                foreach (LlmModel model in removedModels)
                {
                    _provider.Models.Remove(model);
                }

                int addedCount = 0;
                foreach (string modelName in dialog.SelectedModels)
                {
                    if (_provider.Models.Any(model => string.Equals(model.Name.Trim(), modelName, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    _provider.Models.Add(new LlmModel { Name = modelName });
                    addedCount++;
                }

                StatusText.Text = $"模型清单已更新：新增 {addedCount} 个，移除 {removedModels.Length} 个。";
            }
            else
            {
                StatusText.Text = $"已拉取 {fetchedModels.Count} 个模型，未修改当前清单。";
            }
        }
        catch (OperationCanceledException) when (_operationCancellation.IsCancellationRequested)
        {
            StatusText.Text = "已取消拉取模型。";
        }
        catch (Exception)
        {
            StatusText.Text = "拉取模型失败。";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void TestModelsButton_Click(object sender, RoutedEventArgs e) =>
        await TestModelsAsync(ModelsList.SelectedItems.Cast<LlmModel>().ToArray());

    private async void TestModelButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LlmModel model })
        {
            await TestModelsAsync([model]);
        }
    }

    private async Task TestModelsAsync(IReadOnlyList<LlmModel> selectedModels)
    {
        if (_isBusy)
        {
            return;
        }

        if (selectedModels.Count == 0)
        {
            StatusText.Text = "请选择要测试的模型。";
            return;
        }

        LlmProvider provider = ReadForm();
        if (!ValidateNetworkFields(provider))
        {
            return;
        }

        SetBusy(true, $"正在测试 {selectedModels.Count} 个模型…");
        try
        {
            foreach (LlmModel model in selectedModels)
            {
                model.IsTesting = true;
                model.TestStatus = "测试中…";
                model.TestMessage = string.Empty;
                ModelTestResult result = await _client.TestModelAsync(provider, model.Name.Trim(), _operationCancellation.Token);
                model.IsTesting = false;
                model.TestStatus = result.Success ? $"可用 · {result.Message}" : "不可用";
                model.TestMessage = result.Message;
            }

            int successCount = selectedModels.Count(model => model.TestStatus.StartsWith("可用", StringComparison.Ordinal));
            StatusText.Text = $"测试完成：{successCount}/{selectedModels.Count} 个模型可用。";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消模型测试。";
        }
        finally
        {
            foreach (LlmModel model in selectedModels)
            {
                model.IsTesting = false;
            }

            SetBusy(false);
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        ReadForm();
        NormalizeModels();
        try
        {
            if (_isNew)
            {
                _store.Add(_provider);
            }
            else
            {
                _store.Update(_provider);
            }

            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            MessageBox.Show(Window.GetWindow(this), exception.Message, "无法保存 Provider", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

    private void ProviderEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !_isBusy)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    private LlmProvider ReadForm()
    {
        _provider.ApiKey = ApiKeyBox.Password.Trim();
        _provider.VerifySsl = VerifySslCheckBox.IsChecked == true;
        _provider.ProxyMode = ProxyModeComboBox.SelectedValue is ProviderProxyMode mode
            ? mode
            : ProviderProxyMode.Direct;
        return _provider;
    }

    private bool ValidateNetworkFields(LlmProvider provider)
    {
        if (!Uri.TryCreate(provider.ApiUrl.Trim(), UriKind.Absolute, out Uri? apiUri) ||
            apiUri.Scheme is not ("http" or "https"))
        {
            StatusText.Text = "请填写有效的 HTTP 或 HTTPS API 地址。";
            return false;
        }

        if (provider.ProxyMode == ProviderProxyMode.Custom &&
            !Uri.TryCreate(provider.CustomProxy.Trim(), UriKind.Absolute, out _))
        {
            StatusText.Text = "请填写有效的自定义代理地址。";
            return false;
        }

        return true;
    }

    private void NormalizeModels()
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        LlmModel[] normalized = _provider.Models
            .Select(model => new LlmModel { Name = model.Name.Trim() })
            .Where(model => model.Name.Length > 0 && seen.Add(model.Name))
            .ToArray();
        _provider.Models.Clear();
        foreach (LlmModel model in normalized)
        {
            _provider.Models.Add(model);
        }
    }

    private void SetBusy(bool isBusy, string? status = null)
    {
        _isBusy = isBusy;
        SaveButton.IsEnabled = !isBusy;
        FetchModelsButton.IsEnabled = !isBusy;
        TestModelsButton.IsEnabled = !isBusy;
        AddModelButton.IsEnabled = !isBusy;
        NewModelTextBox.IsEnabled = !isBusy;
        ModelsList.IsEnabled = !isBusy;
        if (status is not null)
        {
            StatusText.Text = status;
        }
    }

    private void ModelsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(ModelsList);
        _draggedModelContainer = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        _draggedModel = _draggedModelContainer?.DataContext as LlmModel;
    }

    private void ModelsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed ||
            _draggedModel is null ||
            FindAncestor<TextBox>(e.OriginalSource as DependencyObject) is not null ||
            FindAncestor<CheckBox>(e.OriginalSource as DependencyObject) is not null ||
            FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        Point currentPoint = e.GetPosition(ModelsList);
        if (Math.Abs(currentPoint.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(currentPoint.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        try
        {
            _draggedModelContainer?.SetCurrentValue(UIElement.OpacityProperty, 0.55);
            DragDrop.DoDragDrop(ModelsList, _draggedModel, DragDropEffects.Move);
        }
        finally
        {
            _draggedModelContainer?.ClearValue(UIElement.OpacityProperty);
            _draggedModelContainer = null;
            _draggedModel = null;
            ClearDropPreview();
        }
    }

    private void ModelsList_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(LlmModel)))
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

    private void ModelsList_DragLeave(object sender, DragEventArgs e)
    {
        Point position = e.GetPosition(ModelsList);
        if (position.X < 0 || position.Y < 0 || position.X > ModelsList.ActualWidth || position.Y > ModelsList.ActualHeight)
        {
            ClearDropPreview();
        }
    }

    private void ModelsList_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetData(typeof(LlmModel)) is not LlmModel draggedModel)
            {
                return;
            }

            ListBoxItem? targetContainer = _dropTargetContainer ?? ResolveDropTarget(e, out _dropAfterTarget);
            if (targetContainer?.DataContext is not LlmModel targetModel)
            {
                return;
            }

            int oldIndex = _provider.Models.IndexOf(draggedModel);
            int targetIndex = _provider.Models.IndexOf(targetModel);
            if (_dropAfterTarget)
            {
                targetIndex++;
            }

            if (oldIndex < targetIndex)
            {
                targetIndex--;
            }

            targetIndex = Math.Clamp(targetIndex, 0, _provider.Models.Count - 1);
            if (oldIndex != targetIndex)
            {
                _provider.Models.Move(oldIndex, targetIndex);
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
        if (ModelsList.Items.Count == 0)
        {
            return null;
        }

        ListBoxItem? firstContainer = ModelsList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
        if (firstContainer is not null)
        {
            double firstTop = firstContainer.TranslatePoint(new Point(), ModelsList).Y;
            if (e.GetPosition(ModelsList).Y < firstTop)
            {
                return firstContainer;
            }
        }

        insertAfter = true;
        return ModelsList.ItemContainerGenerator.ContainerFromIndex(ModelsList.Items.Count - 1) as ListBoxItem;
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

    private sealed record ProxyModeOption(ProviderProxyMode Mode, string DisplayName);

    private sealed class DropInsertionAdorner : Adorner
    {
        private readonly bool _insertAfter;

        public DropInsertionAdorner(UIElement adornedElement, bool insertAfter)
            : base(adornedElement)
        {
            _insertAfter = insertAfter;
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            SolidColorBrush brush = new(Color.FromRgb(47, 111, 237));
            Pen pen = new(brush, 2);
            double y = _insertAfter ? Math.Max(1, AdornedElement.RenderSize.Height - 1) : 1;
            double width = AdornedElement.RenderSize.Width;
            drawingContext.DrawLine(pen, new Point(0, y), new Point(width, y));
            drawingContext.DrawEllipse(brush, null, new Point(2, y), 2, 2);
            drawingContext.DrawEllipse(brush, null, new Point(Math.Max(2, width - 2), y), 2, 2);
        }
    }
}
