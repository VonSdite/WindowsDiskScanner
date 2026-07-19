using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace WindowsDiskScanner.App;

internal sealed class DropInsertionAdorner : Adorner
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
