using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using AvaloniaEdit.CodeCompletion;
using GoForge.Dialogs;
using GoForge.Editing;
using GoForge.Models;
using GoForge.Services;

namespace GoForge;

public partial class MainWindow
{
    LspClient? _lsp;
    bool _lspStarting;
    int _outlineToken;
    CompletionWindow? _completion;

    readonly DispatcherTimer _completionTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    EditorTab? _completionTab;

    readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(550) };
    EditorTab? _hoverTab;
    Point _hoverPoint;

    // ------------------------------------------------------------ lifecycle

    void SetLspStatus(string text) => _stGopls.Text = text;

    async void FlashGopls(string message)
    {
        _stGopls.Text = message;
        await Task.Delay(3500);
        if (_lsp != null && _stGopls.Text == message) _stGopls.Text = "gopls: ready";
    }

    /// <summary>Sends the document to gopls (didOpen) and remembers which version it has.</summary>
    void LspOpenDoc(EditorTab t)
    {
        if (_lsp == null || t.FilePath == null) return;
        _lsp.DidOpen(t.FilePath, t.Editor.Text, t.Version);
        t.SentVersion = t.Version;
        t.LspOpen = true;
    }

    /// <summary>Pushes pending edits to gopls, but only when the text really changed since the last send.</summary>
    void LspSync(EditorTab t)
    {
        if (_lsp == null || !t.LspOpen || t.FilePath == null || t.SentVersion == t.Version) return;
        _lsp.DidChange(t.FilePath, t.Editor.Text, t.Version);
        t.SentVersion = t.Version;
    }

    async Task StartLspAsync()
    {
        if (_lspStarting) return;
        _lspStarting = true;
        try
        {
            await StopLspAsync();
            if (!_s.UseGopls)
            {
                SetLspStatus("gopls: off");
                return;
            }

            var gopls = GoToolchain.FindExecutable("gopls", _s.GoplsPath);
            if (gopls == null)
            {
                SetLspStatus("gopls: not found");
                AppendLine("gopls was not found. Use Go ▸ Install / Update gopls to enable completion, diagnostics and navigation.", Kind.Info);
                return;
            }

            string? rootDir = GoToolchain.ModuleRoot(_root) ?? _root;
            if (rootDir == null && Current?.FilePath is { } cp)
                rootDir = GoToolchain.ModuleRoot(cp) ?? Path.GetDirectoryName(cp);
            if (rootDir == null)
            {
                SetLspStatus("gopls: idle");
                return;
            }

            SetLspStatus("gopls: starting…");
            var client = new LspClient();
            client.DiagnosticsPublished += (path, list) => Dispatcher.UIThread.Post(() => OnDiagnostics(path, list));
            client.Message += msg => Dispatcher.UIThread.Post(() => AppendLine(msg, Kind.Info));
            client.Progress += text => Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_lsp, client) && !_lspStarting) return;
                if (text == null) SetLspStatus("gopls: ready");
                else SetLspStatus("gopls: " + (text.Length > 70 ? text.Substring(0, 70) + "…" : text));
            });
            client.Exited += () => Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_lsp, client)) return;
                _lsp = null;
                foreach (var t in _editors) t.LspOpen = false;
                SetLspStatus("gopls: stopped");
            });

            var ok = await client.StartAsync(gopls, rootDir);
            if (!ok)
            {
                SetLspStatus("gopls: failed");
                return;
            }

            _lsp = client;
            SetLspStatus("gopls: ready");
            foreach (var t in _editors.Where(e => e.IsGo && e.FilePath != null))
            {
                LspOpenDoc(t);
                t.LspOpen = true;
            }
            await RefreshOutlineAsync();
        }
        finally
        {
            _lspStarting = false;
        }
    }

    async Task StopLspAsync()
    {
        var client = _lsp;
        _lsp = null;
        foreach (var t in _editors)
        {
            t.LspOpen = false;
            t.SetDiagnostics(new List<LspDiagnostic>());
        }
        _lspProblems.Clear();
        RebuildProblems();
        if (client != null) await client.ShutdownAsync();
    }

    void OnDiagnostics(string path, List<LspDiagnostic> list)
    {
        FindTab(path)?.SetDiagnostics(list);

        _lspProblems[PathUtil.Norm(path)] = list.Select(d => new ProblemItem
        {
            FilePath = path,
            Line = d.Line + 1,
            Column = d.Col + 1,
            Severity = d.Severity,
            Message = d.Message,
            Source = d.Source ?? "gopls"
        }).ToList();
        RebuildProblems();
    }

    // ------------------------------------------------------------ completion

    void OnTextEnteredForCompletion(EditorTab tab, string? text)
    {
        if (!tab.IsGo || string.IsNullOrEmpty(text) || text.Length != 1) return;
        char c = text[0];

        if (c == '.')
        {
            _completionTimer.Stop();
            Fire(() => ShowCompletionAsync(tab, false));
            return;
        }
        if (!EditorTab.IsIdent(c)) return;

        // as-you-type: pop up once at least two identifier characters were typed
        var doc = tab.Editor.Document;
        int caret = tab.Editor.CaretOffset;
        int n = 0;
        while (n < 2 && caret - n > 0 && EditorTab.IsIdent(doc.GetCharAt(caret - n - 1))) n++;
        if (n < 2) return;

        _completionTab = tab;
        _completionTimer.Stop();
        _completionTimer.Start();
    }

    void OnCompletionTimer(object? sender, EventArgs e)
    {
        _completionTimer.Stop();
        var tab = _completionTab;
        if (tab == null || !ReferenceEquals(Current, tab) || _completion != null) return;
        Fire(() => ShowCompletionAsync(tab, false));
    }

    async Task ShowCompletionAsync(EditorTab? tab, bool manual)
    {
        if (tab == null || !tab.IsGo) return;
        if (_lsp == null || !tab.LspOpen || tab.FilePath == null)
        {
            if (manual) FlashGopls("gopls: not running, completion unavailable");
            return;
        }

        var ed = tab.Editor;
        LspSync(tab);

        int requestCaret = ed.CaretOffset;
        var loc = ed.Document.GetLocation(requestCaret);
        int requestLine = loc.Line;
        var items = await _lsp.CompletionAsync(tab.FilePath, loc.Line - 1, loc.Column - 1);

        if (!ReferenceEquals(Current, tab)) return;
        var doc = ed.Document;
        int caret = Math.Min(ed.CaretOffset, doc.TextLength);
        if (doc.GetLocation(caret).Line != requestLine) return; // the user moved on

        if (items.Count == 0)
        {
            if (manual) FlashGopls("gopls: no completions here (still indexing?)");
            return;
        }

        if (!manual && caret > 0)
        {
            var prev = doc.GetCharAt(caret - 1);
            if (!EditorTab.IsIdent(prev) && prev != '.') return; // typed something else meanwhile
        }

        int start = caret;
        while (start > 0 && EditorTab.IsIdent(doc.GetCharAt(start - 1))) start--;

        _completion?.Close();
        var win = new CompletionWindow(ed.TextArea) { CloseWhenCaretAtBeginning = true };
        win.StartOffset = start;
        foreach (var item in items)
            win.CompletionList.CompletionData.Add(new GoCompletionData(item));

        win.Closed += (_, _) =>
        {
            if (ReferenceEquals(_completion, win)) _completion = null;
        };
        _completion = win;
        win.Show();

        if (start < caret)
            win.CompletionList.SelectItem(doc.GetText(start, caret - start));
    }

    // ------------------------------------------------------------ hover

    void OnEditorPointerMoved(EditorTab tab, PointerEventArgs e)
    {
        ToolTip.SetIsOpen(tab.Editor, false);
        _hoverTab = tab;
        _hoverPoint = e.GetPosition(tab.Editor);
        _hoverTimer.Stop();
        _hoverTimer.Start();
    }

    static string CleanMarkdown(string text)
    {
        var lines = text.Replace("\r", "").Split('\n')
            .Where(l => !l.TrimStart().StartsWith("```", StringComparison.Ordinal));
        return string.Join("\n", lines).Trim();
    }

    async void OnHoverTick(object? sender, EventArgs e)
    {
        _hoverTimer.Stop();
        try
        {
            var tab = _hoverTab;
            if (tab == null || !ReferenceEquals(Current, tab)) return;

            var point = _hoverPoint;
            var pos = tab.Editor.GetPositionFromPoint(point);
            if (pos == null) return;

            int line1 = pos.Value.Line, col1 = pos.Value.Column;
            var doc = tab.Editor.Document;
            int offset = doc.GetOffset(line1, col1);

            var parts = new List<string>();
            parts.AddRange(tab.DiagnosticsAt(line1 - 1, col1 - 1).Select(m => "⚠ " + m));

            if (_lsp != null && tab.LspOpen && tab.FilePath != null
                && offset < doc.TextLength && EditorTab.IsIdent(doc.GetCharAt(offset)))
            {
                var hover = await _lsp.HoverAsync(tab.FilePath, line1 - 1, col1 - 1);
                if (!string.IsNullOrWhiteSpace(hover)) parts.Add(CleanMarkdown(hover));
            }

            // the pointer may have moved while we waited for gopls
            if (parts.Count == 0 || !ReferenceEquals(_hoverTab, tab) || _hoverPoint != point || !ReferenceEquals(Current, tab)) return;

            var tip = new TextBlock
            {
                Text = string.Join("\n\n", parts),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                MaxWidth = 640,
                FontFamily = Mono,
                FontSize = 12.5
            };
            ToolTip.SetTip(tab.Editor, tip);
            ToolTip.SetIsOpen(tab.Editor, true);
        }
        catch
        {
            // hover is best effort
        }
    }

    // ------------------------------------------------------------ navigation

    async Task GoToDefinitionAsync(EditorTab tab, int line0, int col0)
    {
        if (_lsp == null || tab.FilePath == null || !tab.LspOpen)
        {
            AppendLine("Go to Definition needs gopls (see Go ▸ Install / Update gopls).", Kind.Info);
            return;
        }

        LspSync(tab);
        var locs = await _lsp.DefinitionAsync(tab.FilePath, line0, col0);
        if (locs.Count == 0)
        {
            _stGopls.Text = "gopls: no definition found";
            return;
        }
        if (locs.Count == 1)
        {
            var l = locs[0];
            await OpenLocationAsync(l.FilePath, l.Line + 1, l.Col + 1);
            return;
        }
        ShowResults(ToResults(locs), $"{locs.Count} definitions");
    }

    async Task GoToDefinitionAtCaretAsync()
    {
        if (Current is not { } t) return;
        var loc = t.Editor.Document.GetLocation(t.Editor.CaretOffset);
        await GoToDefinitionAsync(t, loc.Line - 1, loc.Column - 1);
    }

    async Task FindReferencesAtCaretAsync()
    {
        if (Current is not { } t) return;
        if (_lsp == null || t.FilePath == null || !t.LspOpen)
        {
            AppendLine("Find References needs gopls (see Go ▸ Install / Update gopls).", Kind.Info);
            return;
        }

        var word = t.WordAt(t.Editor.CaretOffset);
        var loc = t.Editor.Document.GetLocation(t.Editor.CaretOffset);
        LspSync(t);
        var refs = await _lsp.ReferencesAsync(t.FilePath, loc.Line - 1, loc.Column - 1);
        var files = refs.Select(r => r.FilePath).Distinct(PathUtil.Cmp).Count();
        ShowResults(ToResults(refs), $"{refs.Count} reference(s) to '{word}' in {files} file(s)");
    }

    List<ResultItem> ToResults(List<LspLocation> locs)
    {
        var results = new List<ResultItem>();
        foreach (var group in locs.GroupBy(l => l.FilePath, PathUtil.Cmp))
        {
            string[] lines;
            var open = FindTab(group.Key);
            try
            {
                lines = open != null
                    ? open.Editor.Text.Replace("\r", "").Split('\n')
                    : File.ReadAllLines(group.Key);
            }
            catch
            {
                lines = Array.Empty<string>();
            }

            foreach (var l in group.OrderBy(x => x.Line).ThenBy(x => x.Col))
            {
                var text = l.Line >= 0 && l.Line < lines.Length ? lines[l.Line].Trim() : "";
                if (text.Length > 200) text = text.Substring(0, 200) + "…";
                var rel = _root != null && PathUtil.IsUnder(l.FilePath, _root)
                    ? Path.GetRelativePath(_root, l.FilePath)
                    : l.FilePath;
                results.Add(new ResultItem
                {
                    FilePath = l.FilePath,
                    Line = l.Line + 1,
                    Column = l.Col + 1,
                    Display = $"{rel}:{l.Line + 1}:{l.Col + 1}    {text}"
                });
            }
        }
        return results;
    }

    // ------------------------------------------------------------ formatting

    async Task FormatCurrentAsync()
    {
        if (Current is { } t) await FormatTabAsync(t, quiet: false);
    }

    async Task FormatTabAsync(EditorTab tab, bool quiet)
    {
        if (!tab.IsGo) return;

        if (_lsp != null && tab.LspOpen && tab.FilePath != null)
        {
            LspSync(tab);
            int version = tab.Version;
            var edits = await _lsp.FormattingAsync(tab.FilePath, _s.TabSize);
            if (edits != null)
            {
                // only apply if the user hasn't typed while we waited
                if (edits.Count > 0 && tab.Version == version) tab.ApplyEdits(edits);
                return;
            }
        }

        var gofmt = GoToolchain.FindExecutable("gofmt");
        if (gofmt == null)
        {
            if (!quiet) AppendLine("Formatting needs gopls or gofmt, and neither was found.", Kind.Error);
            return;
        }

        int v = tab.Version;
        var (code, outp, err) = await GoToolchain.CaptureAsync(gofmt, Array.Empty<string>(), null, tab.Editor.Text);
        if (code == 0)
        {
            if (tab.Version == v) tab.ReplaceAll(outp);
        }
        else if (!quiet)
        {
            ShowBottom(_outputTab);
            AppendLine("gofmt: " + err.Trim(), Kind.Error);
        }
    }

    // ------------------------------------------------------------ outline

    static string SymbolGlyph(int kind) => kind switch
    {
        6 or 12 => "ƒ",
        5 or 23 => "S",
        11 => "I",
        13 => "v",
        14 => "c",
        26 => "T",
        2 => "pkg",
        _ => "·"
    };

    static readonly Regex FuncRx = new(@"^func\s+(\((?<recv>[^)]*)\)\s*)?(?<name>[A-Za-z_]\w*)", RegexOptions.Compiled);
    static readonly Regex TypeRx = new(@"^type\s+(?<name>[A-Za-z_]\w*)\s+(?<kind>struct|interface)?", RegexOptions.Compiled);

    static List<SymbolItem> ParseOutline(string text)
    {
        var items = new List<SymbolItem>();
        var lines = text.Replace("\r", "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var f = FuncRx.Match(lines[i]);
            if (f.Success)
            {
                var recv = f.Groups["recv"].Success ? $"({f.Groups["recv"].Value.Trim()}) " : "";
                items.Add(new SymbolItem { Display = $"ƒ  {recv}{f.Groups["name"].Value}", Line = i + 1, Column = 1 });
                continue;
            }
            var t = TypeRx.Match(lines[i]);
            if (t.Success)
            {
                var glyph = t.Groups["kind"].Value == "interface" ? "I" : "S";
                items.Add(new SymbolItem { Display = $"{glyph}  {t.Groups["name"].Value}", Line = i + 1, Column = 1 });
            }
        }
        return items;
    }

    async Task RefreshOutlineAsync()
    {
        var tab = Current;
        int token = ++_outlineToken;

        if (tab == null || !tab.IsGo)
        {
            _symbols.Clear();
            return;
        }

        List<SymbolItem>? items = null;
        if (_lsp != null && tab.LspOpen && tab.FilePath != null)
        {
            var syms = await _lsp.DocumentSymbolsAsync(tab.FilePath);
            items = syms
                .Where(s => s.Kind != 8) // skip struct fields to keep the outline compact
                .Select(s => new SymbolItem
                {
                    Display = $"{SymbolGlyph(s.Kind)}  {s.Name}",
                    Line = s.Line + 1,
                    Column = s.Col + 1
                })
                .ToList();
        }
        if (items == null || items.Count == 0) items = ParseOutline(tab.Editor.Text);

        if (token != _outlineToken) return; // a newer refresh superseded this one
        _symbols.Clear();
        foreach (var s in items) _symbols.Add(s);
    }

    void GoToSelectedSymbol()
    {
        if (_symbolsList.SelectedItem is SymbolItem s) Current?.GoTo(s.Line, s.Column);
    }
}
