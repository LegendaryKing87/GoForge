using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Indentation.CSharp;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.Search;
using GoForge.Models;
using GoForge.Services;

namespace GoForge.Editing;

/// <summary>One open document: an AvaloniaEdit editor plus its tab header, dirty state and editing helpers.</summary>
public sealed class EditorTab
{
    static int _untitledCounter;

    readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly DispatcherTimer _settleTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    AppSettings _settings;
    bool _dark;
    string? _untitledName;

    public TextEditor Editor { get; }
    public TabItem Item { get; }
    public SearchPanel Search { get; }
    public DiagnosticRenderer Diagnostics { get; }
    public BreakpointMargin Gutter { get; } = new();
    readonly DebugLineRenderer _debugLine = new();

    public string? FilePath { get; private set; }
    public bool IsDirty { get; private set; }
    public int Version { get; private set; } = 1;
    public bool LspOpen { get; set; }
    /// <summary>Last document version sent to gopls, so unchanged text is never re-sent.</summary>
    public int SentVersion { get; set; }

    public event Action<EditorTab>? CloseRequested;
    /// <summary>The user clicked the breakpoint gutter on this 1-based line.</summary>
    public event Action<EditorTab, int>? BreakpointClicked;
    /// <summary>Fires ~350ms after the last edit - used to sync with gopls and refresh the outline.</summary>
    public event Action<EditorTab>? TextSettled;

    public string DisplayName => FilePath != null ? Path.GetFileName(FilePath) : _untitledName ?? "untitled.go";
    public bool IsGo => PathUtil.IsGo(FilePath ?? _untitledName);

    public EditorTab(string? path, string text, AppSettings settings, bool dark)
    {
        _settings = settings;
        _dark = dark;
        FilePath = path;
        if (path == null) _untitledName = $"untitled-{++_untitledCounter}.go";

        Editor = new TextEditor
        {
            Document = new TextDocument(text),
            ShowLineNumbers = true,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Editor.Options.ConvertTabsToSpaces = false;
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        Editor.Options.HighlightCurrentLine = true;
        Editor.Options.AllowScrollBelowDocument = true;
        Editor.TextArea.IndentationStrategy = new CSharpIndentationStrategy(Editor.Options);

        Diagnostics = new DiagnosticRenderer();
        Editor.TextArea.TextView.BackgroundRenderers.Add(Diagnostics);
        Search = SearchPanel.Install(Editor);

        Editor.TextArea.LeftMargins.Insert(0, Gutter);
        Editor.TextArea.TextView.BackgroundRenderers.Add(_debugLine);
        Gutter.LineClicked += line => BreakpointClicked?.Invoke(this, line);

        var close = new Button
        {
            Content = "✕",
            FontSize = 10,
            Padding = new Thickness(4, 1),
            Margin = new Thickness(8, 0, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };
        close.Click += (_, _) => CloseRequested?.Invoke(this);

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(_title);
        header.Children.Add(close);
        header.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(header).Properties.IsMiddleButtonPressed) CloseRequested?.Invoke(this);
        };

        Item = new TabItem { Header = header, Content = Editor, FontSize = 13 };

        Editor.Document.TextChanged += (_, _) =>
        {
            Version++;
            if (!IsDirty) { IsDirty = true; UpdateTitle(); }
            _settleTimer.Stop();
            _settleTimer.Start();
        };
        _settleTimer.Tick += (_, _) =>
        {
            _settleTimer.Stop();
            TextSettled?.Invoke(this);
        };

        Editor.TextArea.TextEntering += OnTextEntering;
        Editor.TextArea.TextEntered += OnTextEntered;

        ApplyStyle(settings, dark);
        UpdateTitle();
    }

    // ------------------------------------------------------------ state

    void UpdateTitle() => _title.Text = (IsDirty ? "● " : "") + DisplayName;

    public void MarkSaved()
    {
        IsDirty = false;
        UpdateTitle();
    }

    public void SetPath(string path)
    {
        FilePath = path;
        _untitledName = null;
        UpdateTitle();
        ApplyHighlighting();
    }

    // ------------------------------------------------------------ appearance

    public void ApplyStyle(AppSettings s, bool dark)
    {
        _settings = s;
        _dark = dark;

        Editor.FontFamily = new FontFamily(s.FontFamily);
        Editor.FontSize = s.FontSize;
        Editor.WordWrap = s.WordWrap;
        Editor.Options.IndentationSize = Math.Clamp(s.TabSize, 1, 16);
        Editor.Options.ShowSpaces = s.ShowWhitespace;
        Editor.Options.ShowTabs = s.ShowWhitespace;

        Editor.Background = Brush(dark ? "#1E1E1E" : "#FFFFFF");
        Editor.Foreground = Brush(dark ? "#D4D4D4" : "#1F1F1F");
        Editor.LineNumbersForeground = Brush(dark ? "#858585" : "#237893");
        Editor.TextArea.SelectionBrush = Brush(dark ? "#264F78" : "#ADD6FF");
        Editor.TextArea.TextView.CurrentLineBackground = Brush(dark ? "#282828" : "#F3F6FA");

        ApplyHighlighting();
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    void ApplyHighlighting()
    {
        var fullName = FilePath ?? _untitledName ?? "";
        var fileName = Path.GetFileName(fullName).ToLowerInvariant();
        var ext = Path.GetExtension(fullName).ToLowerInvariant();
        if (ext == ".go") Editor.SyntaxHighlighting = GoHighlighting.Get(_dark);
        else if (fileName == "go.sum" || fileName.EndsWith(".sum", StringComparison.Ordinal)) Editor.SyntaxHighlighting = GoHighlighting.GetSum(_dark);
        else if (fileName == "go.mod" || fileName == "go.work" || ext == ".mod" || ext == ".work") Editor.SyntaxHighlighting = GoHighlighting.GetMod(_dark);
        else if (ext.Length == 0) Editor.SyntaxHighlighting = null;
        else Editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinitionByExtension(ext);
    }

    public void SetBreakpoints(IEnumerable<int> lines)
    {
        Gutter.Lines.Clear();
        foreach (var l in lines) Gutter.Lines.Add(l);
        Gutter.InvalidateVisual();
    }

    /// <summary>Marks the line the debugger is stopped at (1-based); 0 clears the marker.</summary>
    public void SetDebugLine(int line)
    {
        Gutter.DebugLine = line;
        _debugLine.Line = line;
        Gutter.InvalidateVisual();
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    public void SetDiagnostics(List<LspDiagnostic> list)
    {
        Diagnostics.Set(list);
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    // ------------------------------------------------------------ typing helpers

    void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (!_settings.AutoCloseBrackets || string.IsNullOrEmpty(e.Text) || e.Text.Length != 1) return;

        // typing a closing char right before the same auto-inserted char just steps over it
        if (e.Text is ")" or "]" or "}" or "\"" or "`" or "'")
        {
            var doc = Editor.Document;
            int caret = Editor.CaretOffset;
            if (caret < doc.TextLength && doc.GetCharAt(caret) == e.Text[0] && Editor.SelectionLength == 0)
            {
                Editor.CaretOffset = caret + 1;
                e.Handled = true;
            }
        }
    }

    void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (!_settings.AutoCloseBrackets || string.IsNullOrEmpty(e.Text) || e.Text.Length != 1) return;

        string? close = e.Text switch
        {
            "(" => ")",
            "[" => "]",
            "{" => "}",
            "\"" => "\"",
            "`" => "`",
            "'" => "'",
            _ => null
        };
        if (close == null) return;

        var doc = Editor.Document;
        int caret = Editor.CaretOffset;

        if (caret < doc.TextLength)
        {
            char next = doc.GetCharAt(caret);
            if (char.IsLetterOrDigit(next) || next == '_') return;
        }
        if (close == e.Text && caret >= 2)
        {
            char prev = doc.GetCharAt(caret - 2);
            if (char.IsLetterOrDigit(prev) || prev == '_') return; // e.g. don't pair apostrophes in words
        }

        doc.Insert(caret, close);
        Editor.CaretOffset = caret;
    }

    // ------------------------------------------------------------ navigation

    public void GoTo(int line, int col)
    {
        var doc = Editor.Document;
        line = Math.Clamp(line, 1, doc.LineCount);
        var dl = doc.GetLineByNumber(line);
        col = Math.Clamp(col, 1, dl.Length + 1);
        Editor.CaretOffset = dl.Offset + col - 1;
        Editor.ScrollTo(line, col);
        Editor.TextArea.Focus();
    }

    // ------------------------------------------------------------ editing commands

    static string NewLine(TextDocument doc)
    {
        if (doc.LineCount > 1)
        {
            var l = doc.GetLineByNumber(1);
            if (l.DelimiterLength > 0) return doc.GetText(l.EndOffset, l.DelimiterLength);
        }
        return "\n";
    }

    (int First, int Last) SelectedLines()
    {
        var doc = Editor.Document;
        if (Editor.SelectionLength == 0)
        {
            int l = doc.GetLocation(Editor.CaretOffset).Line;
            return (l, l);
        }
        var a = doc.GetLocation(Editor.SelectionStart);
        var b = doc.GetLocation(Editor.SelectionStart + Editor.SelectionLength);
        int last = b.Line;
        if (b.Column == 1 && last > a.Line) last--;
        return (a.Line, last);
    }

    public void ToggleComment()
    {
        var doc = Editor.Document;
        var (first, last) = SelectedLines();

        bool allCommented = true;
        bool anyText = false;
        for (int n = first; n <= last; n++)
        {
            var text = doc.GetText(doc.GetLineByNumber(n));
            var trimmed = text.TrimStart();
            if (trimmed.Length == 0) continue;
            anyText = true;
            if (!trimmed.StartsWith("//")) { allCommented = false; break; }
        }
        if (!anyText) return;

        doc.BeginUpdate();
        try
        {
            for (int n = last; n >= first; n--)
            {
                var line = doc.GetLineByNumber(n);
                var text = doc.GetText(line);
                int ws = text.Length - text.TrimStart().Length;
                if (text.Trim().Length == 0) continue;

                if (allCommented)
                {
                    int remove = text.Substring(ws).StartsWith("// ") ? 3 : 2;
                    doc.Remove(line.Offset + ws, remove);
                }
                else doc.Insert(line.Offset + ws, "// ");
            }
        }
        finally { doc.EndUpdate(); }
    }

    public void DuplicateLine()
    {
        var doc = Editor.Document;
        var (first, last) = SelectedLines();
        var a = doc.GetLineByNumber(first);
        var b = doc.GetLineByNumber(last);
        var block = doc.GetText(a.Offset, b.EndOffset - a.Offset);
        doc.Insert(b.EndOffset, NewLine(doc) + block);
    }

    public void DeleteLine()
    {
        var doc = Editor.Document;
        var (first, last) = SelectedLines();
        var a = doc.GetLineByNumber(first);
        var b = doc.GetLineByNumber(last);
        int end = b.EndOffset + b.DelimiterLength;
        int start = a.Offset;
        if (b.DelimiterLength == 0 && start > 0 && first > 1)
            start = doc.GetLineByNumber(first - 1).EndOffset; // last line: also drop the preceding delimiter
        doc.Remove(start, end - start);
    }

    public void MoveLines(bool up)
    {
        var doc = Editor.Document;
        var (first, last) = SelectedLines();
        int caretLine = doc.GetLocation(Editor.CaretOffset).Line;
        int col = Editor.CaretOffset - doc.GetLineByNumber(caretLine).Offset;
        var nl = NewLine(doc);

        if (up)
        {
            if (first <= 1) return;
            var prev = doc.GetLineByNumber(first - 1);
            var a = doc.GetLineByNumber(first);
            var b = doc.GetLineByNumber(last);
            var block = doc.GetText(a.Offset, b.EndOffset - a.Offset);
            var prevText = doc.GetText(prev.Offset, prev.Length);
            doc.Replace(prev.Offset, b.EndOffset - prev.Offset, block + nl + prevText);
            var nlLine = doc.GetLineByNumber(caretLine - 1);
            Editor.CaretOffset = nlLine.Offset + Math.Min(col, nlLine.Length);
        }
        else
        {
            if (last >= doc.LineCount) return;
            var next = doc.GetLineByNumber(last + 1);
            var a = doc.GetLineByNumber(first);
            var b = doc.GetLineByNumber(last);
            var block = doc.GetText(a.Offset, b.EndOffset - a.Offset);
            var nextText = doc.GetText(next.Offset, next.Length);
            doc.Replace(a.Offset, next.EndOffset - a.Offset, nextText + nl + block);
            var nlLine = doc.GetLineByNumber(caretLine + 1);
            Editor.CaretOffset = nlLine.Offset + Math.Min(col, nlLine.Length);
        }
    }

    // ------------------------------------------------------------ programmatic edits

    /// <summary>Replaces the whole text but only touches the changed middle, so caret and scroll position survive.</summary>
    public void ReplaceAll(string text)
    {
        var doc = Editor.Document;
        var old = doc.Text;
        if (old == text) return;

        int max = Math.Min(old.Length, text.Length);
        int pre = 0;
        while (pre < max && old[pre] == text[pre]) pre++;
        int suf = 0;
        while (suf < max - pre && old[old.Length - 1 - suf] == text[text.Length - 1 - suf]) suf++;

        doc.Replace(pre, old.Length - pre - suf, text.Substring(pre, text.Length - pre - suf));
    }

    public void ApplyEdits(List<LspTextEdit> edits)
    {
        var doc = Editor.Document;
        var ordered = edits
            .Select(e => (Start: DiagnosticRenderer.ToOffset(doc, e.Line, e.Col),
                          End: DiagnosticRenderer.ToOffset(doc, e.EndLine, e.EndCol),
                          e.NewText))
            .OrderByDescending(e => e.Start)
            .ToList();

        doc.BeginUpdate();
        try
        {
            foreach (var e in ordered)
                doc.Replace(e.Start, Math.Max(0, e.End - e.Start), e.NewText);
        }
        finally { doc.EndUpdate(); }
    }

    public string WordAt(int offset)
    {
        var doc = Editor.Document;
        int s = Math.Clamp(offset, 0, doc.TextLength);
        int e = s;
        while (s > 0 && IsIdent(doc.GetCharAt(s - 1))) s--;
        while (e < doc.TextLength && IsIdent(doc.GetCharAt(e))) e++;
        return doc.GetText(s, e - s);
    }

    public static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Messages of all diagnostics covering the 0-based position.</summary>
    public IEnumerable<string> DiagnosticsAt(int line0, int col0)
    {
        foreach (var d in Diagnostics.Items)
        {
            bool afterStart = line0 > d.Line || (line0 == d.Line && col0 >= d.Col);
            bool beforeEnd = line0 < d.EndLine || (line0 == d.EndLine && col0 <= d.EndCol);
            if (afterStart && beforeEnd) yield return d.Message;
        }
    }
}
