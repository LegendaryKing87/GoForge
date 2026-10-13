using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using GoForge.Models;

namespace GoForge.Editing;

/// <summary>Draws wavy underlines under diagnostic ranges reported by gopls.</summary>
public sealed class DiagnosticRenderer : IBackgroundRenderer
{
    List<LspDiagnostic> _items = new();

    public IReadOnlyList<LspDiagnostic> Items => _items;

    public KnownLayer Layer => KnownLayer.Selection;

    public void Set(List<LspDiagnostic> items) => _items = items;

    public static int ToOffset(TextDocument doc, int line0, int col0)
    {
        int line = Math.Clamp(line0 + 1, 1, doc.LineCount);
        var dl = doc.GetLineByNumber(line);
        return dl.Offset + Math.Clamp(col0, 0, dl.Length);
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        var doc = textView.Document;
        if (doc == null || _items.Count == 0) return;

        foreach (var d in _items)
        {
            int start = ToOffset(doc, d.Line, d.Col);
            int end = ToOffset(doc, d.EndLine, d.EndCol);

            if (end <= start)
            {
                // zero-width diagnostic: underline the character at (or before) the position
                if (start < doc.TextLength) end = start + 1;
                else if (start > 0) { end = start; start--; }
                else continue;
            }

            var color = d.Severity switch
            {
                1 => Color.Parse("#F14C4C"),
                2 => Color.Parse("#CCA700"),
                3 => Color.Parse("#3794FF"),
                _ => Color.Parse("#8A8A8A")
            };
            var pen = new Pen(new SolidColorBrush(color), 1);

            var segment = new TextSegment { StartOffset = start, EndOffset = end };
            foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                DrawWave(drawingContext, pen, r.BottomLeft, r.BottomRight);
        }
    }

    static void DrawWave(DrawingContext dc, IPen pen, Point a, Point b)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            double x = a.X;
            double y = a.Y - 2;
            bool down = true;
            ctx.BeginFigure(new Point(x, y), false);
            while (x < b.X)
            {
                x = Math.Min(x + 2, b.X);
                ctx.LineTo(new Point(x, y + (down ? 2 : 0)));
                down = !down;
            }
            ctx.EndFigure(false);
        }
        dc.DrawGeometry(null, pen, g);
    }
}
