using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using GoForge.Dialogs;
using GoForge.Editing;
using GoForge.Models;
using GoForge.Services;

namespace GoForge;

public partial class MainWindow
{
    Popup? _sigPopup;
    EditorTab? _sigTab;
    int _sigLine;
    readonly DispatcherTimer _sigTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };

    // ------------------------------------------------------------ applying workspace edits

    /// <summary>
    /// Applies edits from gopls. Every affected file is opened in a tab and edited there, so the
    /// changes stay unsaved and can be reviewed or undone with Ctrl+Z.
    /// </summary>
    async Task ApplyWorkspaceEditAsync(Dictionary<string, List<LspTextEdit>> edit)
    {
        int files = 0, edits = 0;
        foreach (var (path, list) in edit)
        {
            if (list.Count == 0) continue;
            var tab = await OpenFileAsync(path, activate: false);
            if (tab == null) continue;
            tab.ApplyEdits(list);
            files++;
            edits += list.Count;
        }
        if (files > 0)
            FlashGopls($"Applied {edits} edit(s) in {files} file(s). Save to keep them.");
    }

    void SyncAllDocuments()
    {
        foreach (var t in _editors) LspSync(t);
    }

    // ------------------------------------------------------------ organize imports

    Task OrganizeImportsCurrentAsync()
        => Current is { } t ? OrganizeImportsAsync(t, quiet: false) : Task.CompletedTask;

    async Task OrganizeImportsAsync(EditorTab tab, bool quiet)
    {
        if (!tab.IsGo) return;
        if (_lsp == null || !tab.LspOpen || tab.FilePath == null)
        {
            if (!quiet) FlashGopls("gopls: not running, cannot organize imports");
            return;
        }

        LspSync(tab);
        int version = tab.Version;
        var doc = tab.Editor.Document;
        var last = doc.GetLineByNumber(doc.LineCount);

        var actions = await _lsp.CodeActionAsync(tab.FilePath, 0, 0, doc.LineCount - 1, last.Length, null,
            new[] { "source.organizeImports" });
        if (tab.Version != version) return; // the user kept typing

        var action = actions.FirstOrDefault(a => a.Kind != null && a.Kind.StartsWith("source.organizeImports", StringComparison.Ordinal))
                     ?? actions.FirstOrDefault();
        if (action == null)
        {
            if (!quiet) FlashGopls("gopls: imports are already organized");
            return;
        }

        if (action.Edit != null)
        {
            if (action.Edit.TryGetValue(PathUtil.Norm(tab.FilePath), out var own) && action.Edit.Count == 1)
                tab.ApplyEdits(own);
            else
                await ApplyWorkspaceEditAsync(action.Edit);
        }
        if (action.Command != null)
            await _lsp.ExecuteCommandAsync(action.Command, action.CommandArgs);
    }

    // ------------------------------------------------------------ quick fix / code actions

    async Task QuickFixAsync()
    {
        if (Current is not { } tab) return;
        if (_lsp == null || !tab.LspOpen || tab.FilePath == null)
        {
            FlashGopls("gopls: not running, quick fixes unavailable");
            return;
        }

        SyncAllDocuments();
        var ed = tab.Editor;
        var doc = ed.Document;
        int s = ed.SelectionLength > 0 ? ed.SelectionStart : ed.CaretOffset;
        int e = ed.SelectionLength > 0 ? s + ed.SelectionLength : s;
        var sl = doc.GetLocation(s);
        var el = doc.GetLocation(e);

        // gopls matches quick fixes against the diagnostics we send back, so include the ones on these lines
        var diags = tab.Diagnostics.Items
            .Where(d => d.Line <= el.Line - 1 && d.EndLine >= sl.Line - 1)
            .Select(d => d.Raw)
            .ToList();

        var actions = await _lsp.CodeActionAsync(tab.FilePath, sl.Line - 1, sl.Column - 1, el.Line - 1, el.Column - 1, diags, null);
        if (actions.Count == 0)
        {
            FlashGopls("gopls: no quick fixes or refactorings here");
            return;
        }

        actions = actions
            .OrderByDescending(a => a.Preferred)
            .ThenBy(a => a.Kind == null || !a.Kind.StartsWith("quickfix", StringComparison.Ordinal))
            .ToList();

        var titles = actions.Select(a => (a.Preferred ? "★  " : "     ") + a.Title).ToList();
        var win = new PickerWindow("Quick Fix", titles);
        int idx = await win.ShowDialog<int?>(this) ?? -1;
        if (idx < 0) return;

        var chosen = actions[idx];
        if (chosen.Edit != null) await ApplyWorkspaceEditAsync(chosen.Edit);
        if (chosen.Command != null) await _lsp.ExecuteCommandAsync(chosen.Command, chosen.CommandArgs);
    }

    // ------------------------------------------------------------ rename

    async Task RenameSymbolAsync()
    {
        if (Current is not { } tab) return;
        if (_lsp == null || !tab.LspOpen || tab.FilePath == null)
        {
            FlashGopls("gopls: not running, rename unavailable");
            return;
        }

        var word = tab.WordAt(tab.Editor.CaretOffset);
        if (word.Length == 0)
        {
            FlashGopls("Place the caret on a symbol to rename it");
            return;
        }

        var name = await Dlg.PromptAsync(this, "Rename Symbol", $"Rename '{word}' to:", word,
            "Every reference in the project is renamed. The edits stay unsaved so you can review and undo them.");
        if (string.IsNullOrWhiteSpace(name) || name == word) return;

        SyncAllDocuments();
        var loc = tab.Editor.Document.GetLocation(tab.Editor.CaretOffset);
        var edit = await _lsp.RenameAsync(tab.FilePath, loc.Line - 1, loc.Column - 1, name.Trim());
        if (edit == null || edit.Count == 0)
        {
            await Dlg.InfoAsync(this, "Rename", _lsp.LastError ?? "gopls could not rename this symbol.");
            return;
        }
        await ApplyWorkspaceEditAsync(edit);
    }

    // ------------------------------------------------------------ signature help

    void OnTextEnteredForSignature(EditorTab tab, string? text)
    {
        if (!tab.IsGo || string.IsNullOrEmpty(text) || text.Length != 1) return;
        if (text == "(" || text == ",")
        {
            _sigTab = tab;
            _sigTimer.Stop();
            _sigTimer.Start();
        }
        else if (text == ")")
        {
            HideSignature();
        }
    }

    void OnSigTimer(object? sender, EventArgs e)
    {
        _sigTimer.Stop();
        var tab = _sigTab;
        if (tab == null || !ReferenceEquals(Current, tab)) return;
        Fire(() => ShowSignatureAsync(tab));
    }

    void HideSignature()
    {
        if (_sigPopup == null) return;
        _sigPopup.IsOpen = false;
        _sigPopup = null;
    }

    void HideSignatureIfMoved(EditorTab tab)
    {
        if (_sigPopup == null) return;
        if (!ReferenceEquals(_sigTab, tab) || tab.Editor.Document.GetLocation(tab.Editor.CaretOffset).Line != _sigLine)
            HideSignature();
    }

    async Task ShowSignatureAsync(EditorTab tab)
    {
        if (_lsp == null || !tab.LspOpen || tab.FilePath == null) return;

        LspSync(tab);
        var ed = tab.Editor;
        var loc = ed.Document.GetLocation(ed.CaretOffset);
        var sig = await _lsp.SignatureHelpAsync(tab.FilePath, loc.Line - 1, loc.Column - 1);
        if (!ReferenceEquals(Current, tab)) return;

        HideSignature();
        if (sig == null) return;

        bool dark = _s.IsDark;
        var fg = Br(dark ? "#D4D4D4" : "#1F1F1F");
        var accent = Br(dark ? "#4FC1FF" : "#0070C1");

        TextBlock Part(string text, bool bold) => new()
        {
            Text = text,
            FontFamily = Mono,
            FontSize = 12.5,
            Foreground = bold ? accent : fg,
            FontWeight = bold ? FontWeight.Bold : FontWeight.Normal
        };

        var label = sig.Label;
        var (start, end) = sig.ActiveParam >= 0 && sig.ActiveParam < sig.Params.Count
            ? sig.Params[sig.ActiveParam]
            : (-1, -1);

        // monospace parts side by side: the active parameter is bold and coloured
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (start >= 0 && end <= label.Length && start < end)
        {
            row.Children.Add(Part(label.Substring(0, start), false));
            row.Children.Add(Part(label.Substring(start, end - start), true));
            row.Children.Add(Part(label.Substring(end), false));
        }
        else row.Children.Add(Part(label, false));

        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(row);
        if (!string.IsNullOrWhiteSpace(sig.Doc))
            stack.Children.Add(new TextBlock
            {
                Text = sig.Doc.Trim(),
                Foreground = fg,
                Opacity = 0.75,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 520
            });

        var border = new Border
        {
            Background = Br(dark ? "#252526" : "#F3F3F3"),
            BorderBrush = Br(dark ? "#454545" : "#C8C8C8"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 5),
            Child = stack
        };

        var tv = ed.TextArea.TextView;
        var pos = tv.GetVisualPosition(new TextViewPosition(loc.Line, loc.Column), VisualYPosition.LineTop);
        var rect = new Rect(pos.X - tv.ScrollOffset.X, pos.Y - tv.ScrollOffset.Y, 2, tv.DefaultLineHeight);

        _sigPopup = new Popup
        {
            PlacementTarget = tv,
            Placement = PlacementMode.Top,
            PlacementRect = rect,
            IsLightDismissEnabled = false,
            Child = border
        };
        _sigTab = tab;
        _sigLine = loc.Line;
        _sigPopup.IsOpen = true;
    }
}
