using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace GoForge.Editing;

/// <summary>Left gutter that shows breakpoints (red dots) and the current debugger line (yellow arrow).</summary>
public sealed class BreakpointMargin : AbstractMargin
{
    public HashSet<int> Lines { get; } = new();

    /// <summary>1-based line the debugger is stopped at, or 0.</summary>
    public int DebugLine { get; set; }

    public event Action<int>? LineClicked;

    static readonly IBrush BreakpointBrush = new SolidColorBrush(Color.Parse("#E51400"));
    static readonly IBrush ArrowBrush = new SolidColorBrush(Color.Parse("#FFCC00"));

    protected override Size MeasureOverride(Size availableSize) => new(18, 0);

    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        if (oldTextView != null) oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView != null) newTextView.VisualLinesChanged += OnVisualLinesChanged;
        InvalidateVisual();
    }

    void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        // a transparent fill makes the whole gutter clickable
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));

        var tv = TextView;
        if (tv == null || !tv.VisualLinesValid) return;

        foreach (var vl in tv.VisualLines)
        {
            int line = vl.FirstDocumentLine.LineNumber;
            bool bp = Lines.Contains(line);
            bool cur = line == DebugLine;
            if (!bp && !cur) continue;

            double top = vl.GetTextLineVisualYPosition(vl.TextLines[0], VisualYPosition.LineTop) - tv.VerticalOffset;
            double mid = top + tv.DefaultLineHeight / 2;

            if (bp) context.DrawEllipse(BreakpointBrush, null, new Point(9, mid), 5, 5);
            if (cur)
            {
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(3, mid - 5), true);
                    c.LineTo(new Point(13, mid));
                    c.LineTo(new Point(3, mid + 5));
                    c.EndFigure(true);
                }
                context.DrawGeometry(ArrowBrush, null, g);
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var tv = TextView;
        if (tv == null || !tv.VisualLinesValid) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        double y = e.GetPosition(this).Y + tv.VerticalOffset;
        foreach (var vl in tv.VisualLines)
        {
            if (y >= vl.VisualTop && y < vl.VisualTop + vl.Height)
            {
                LineClicked?.Invoke(vl.FirstDocumentLine.LineNumber);
                e.Handled = true;
                return;
            }
        }
    }
}

/// <summary>Highlights the line the debugger is currently stopped at.</summary>
public sealed class DebugLineRenderer : IBackgroundRenderer
{
    static readonly IBrush Fill = new SolidColorBrush(Color.Parse("#44FFCC00"));

    /// <summary>1-based line number, or 0 for none.</summary>
    public int Line { get; set; }

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        var doc = textView.Document;
        if (Line <= 0 || doc == null || Line > doc.LineCount) return;

        var dl = doc.GetLineByNumber(Line);
        var segment = new TextSegment { StartOffset = dl.Offset, Length = dl.Length };
        foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment, true))
            drawingContext.DrawRectangle(Fill, null, r);
    }
}
