using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WindowsDiskScanner.App;

public sealed class TreeRow : INotifyPropertyChanged
{
    private bool _isExpanded;

    public TreeRow(ScanNode node, int depth, long rootSizeBytes)
    {
        Node = node;
        Depth = depth;
        RootSizeBytes = rootSizeBytes;
    }

    public ScanNode Node { get; }

    public int Depth { get; }

    public long RootSizeBytes { get; private set; }

    public double IndentWidth => Depth * 20;

    public bool HasChildren => Node.Children is { Count: > 0 };

    public double PercentOfRoot => RootSizeBytes == 0
        ? 0
        : Node.SizeBytes / (double)RootSizeBytes * 100;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ExpanderGlyph));
        }
    }

    public string ExpanderGlyph => IsExpanded ? "▼" : "▶";

    internal void UpdateRootSizeBytes(long rootSizeBytes)
    {
        if (RootSizeBytes == rootSizeBytes)
        {
            return;
        }

        RootSizeBytes = rootSizeBytes;
        OnPropertyChanged(nameof(RootSizeBytes));
        OnPropertyChanged(nameof(PercentOfRoot));
    }

    internal void RefreshChildrenState()
    {
        if (!HasChildren)
        {
            IsExpanded = false;
        }

        OnPropertyChanged(nameof(HasChildren));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
