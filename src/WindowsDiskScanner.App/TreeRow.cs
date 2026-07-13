using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WindowsDiskScanner.App;

public sealed class TreeRow : INotifyPropertyChanged
{
    private bool _isExpanded;

    public TreeRow(ScanNode node, int depth)
    {
        Node = node;
        Depth = depth;
    }

    public ScanNode Node { get; }

    public int Depth { get; }

    public double IndentWidth => Depth * 20;

    public bool HasChildren => Node.Children is { Count: > 0 };

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

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
