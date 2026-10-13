using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GoForge.Dialogs;
using GoForge.Editing;
using GoForge.Models;
using GoForge.Services;

namespace GoForge;

public partial class MainWindow
{
    bool _forceClose;

    // ------------------------------------------------------------ startup / shutdown

    async Task OnOpenedAsync()
    {
        _hoverTimer.Tick += OnHoverTick;

        var goExe = GoToolchain.FindExecutable("go", _s.GoPath);
        await Task.Run(() => GoToolchain.DetectEnv(goExe));
        await RefreshGoVersionAsync();

        string? folder = null;
        var files = new List<string>();
        foreach (var a in Program.StartupArgs)
        {
            if (Directory.Exists(a)) folder = Path.GetFullPath(a);
            else if (File.Exists(a)) files.Add(Path.GetFullPath(a));
        }
        if (folder == null && files.Count == 0)
        {
            if (_s.LastFolder != null && Directory.Exists(_s.LastFolder)) folder = _s.LastFolder;
            files.AddRange(_s.OpenFiles.Where(File.Exists));
        }

        if (folder != null) await OpenFolderAsync(folder, restartLsp: false);
        foreach (var f in files) await OpenFileAsync(f, activate: false);
        if (_editors.Count > 0) _tabs.SelectedItem = _editors[0].Item;

        await StartLspAsync();
    }

    async Task RefreshGoVersionAsync()
    {
        var go = GoToolchain.FindExecutable("go", _s.GoPath);
        if (go == null)
        {
            _goVersion = null;
            _stGo.Text = "Go: not found";
            return;
        }
        try
        {
            var (code, outp, _) = await GoToolchain.CaptureAsync(go, new[] { "version" }, null, null, 10000);
            if (code == 0)
            {
                _goVersion = outp.Trim();
                var parts = _goVersion.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                _stGo.Text = parts.Length >= 3 ? "Go " + parts[2].Replace("go", "") : _goVersion;
                return;
            }
        }
        catch
        {
            // fall through
        }
        _stGo.Text = "Go: unknown";
    }

    void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_forceClose && _editors.Any(t => t.IsDirty))
        {
            e.Cancel = true;
            Fire(async () =>
            {
                int n = _editors.Count(t => t.IsDirty);
                var r = await Dlg.ChoiceAsync(this, "Unsaved changes", $"{n} file(s) have unsaved changes.", "Save All", "Discard", "Cancel");
                if (r == 0) { if (!await SaveAllAsync(promptUntitled: true)) return; }
                else if (r != 1) return;
                _forceClose = true;
                Close();
            });
            return;
        }

        _s.Maximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            _s.WindowWidth = Width;
            _s.WindowHeight = Height;
        }
        _s.OpenFiles = _editors.Where(t => t.FilePath != null).Select(t => t.FilePath!).ToList();
        _s.LastFolder = _root;
        SettingsStore.Save(_s);

        _runner.Kill();
        _lsp?.Dispose();
    }

    // ------------------------------------------------------------ tabs

    EditorTab? FindTab(string path) => _editors.FirstOrDefault(t => PathUtil.Same(t.FilePath, path));

    void AttachTab(EditorTab tab)
    {
        tab.CloseRequested += t => Fire(() => CloseTabAsync(t));
        tab.TextSettled += OnTextSettled;

        var ed = tab.Editor;
        ed.TextArea.Caret.PositionChanged += (_, _) => { if (ReferenceEquals(Current, tab)) UpdateStatus(); };
        ed.Document.TextChanged += (_, _) => { if (ReferenceEquals(Current, tab)) UpdateTitle(); };
        ed.TextArea.TextEntered += (_, e) =>
        {
            if (e.Text == "." && tab.IsGo) Fire(() => ShowCompletionAsync(tab, false));
        };

        ed.AddHandler(PointerWheelChangedEvent, (object? _, PointerWheelEventArgs e) =>
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                Zoom(e.Delta.Y > 0 ? 1 : -1);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);

        ed.AddHandler(PointerPressedEvent, (object? _, PointerPressedEventArgs e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            if (!e.GetCurrentPoint(ed).Properties.IsLeftButtonPressed) return;
            var pos = ed.GetPositionFromPoint(e.GetPosition(ed));
            if (pos == null) return;
            var p = pos.Value;
            e.Handled = true; // ctrl+click = go to definition, don't move the caret
            Fire(() => GoToDefinitionAsync(tab, p.Line - 1, p.Column - 1));
        }, RoutingStrategies.Tunnel);

        ed.PointerMoved += (_, e) => OnEditorPointerMoved(tab, e);
        ed.PointerExited += (_, _) =>
        {
            _hoverTimer.Stop();
            ToolTip.SetIsOpen(ed, false);
        };

        _editors.Add(tab);
        _tabs.Items.Add(tab.Item);

        if (_lsp != null && tab.IsGo && tab.FilePath != null)
        {
            _lsp.DidOpen(tab.FilePath, ed.Text, tab.Version);
            tab.LspOpen = true;
        }
        UpdateWelcome();
    }

    void OnTextSettled(EditorTab tab)
    {
        if (_lsp != null && tab.LspOpen && tab.FilePath != null)
            _lsp.DidChange(tab.FilePath, tab.Editor.Text, tab.Version);
        if (ReferenceEquals(Current, tab)) Fire(RefreshOutlineAsync);
    }

    void NewFile()
    {
        var tab = new EditorTab(null, "package main\n\nfunc main() {\n\t\n}\n", _s, _s.IsDark);
        AttachTab(tab);
        _tabs.SelectedItem = tab.Item;
        tab.GoTo(4, 2);
    }

    async Task<EditorTab?> OpenFileAsync(string path, bool activate = true)
    {
        path = PathUtil.Norm(path);
        var existing = FindTab(path);
        if (existing != null)
        {
            if (activate) _tabs.SelectedItem = existing.Item;
            return existing;
        }

        if (!File.Exists(path))
        {
            await Dlg.InfoAsync(this, "File not found", path);
            return null;
        }

        if (new FileInfo(path).Length > 10_000_000)
        {
            var r = await Dlg.ChoiceAsync(this, "Large file", $"{Path.GetFileName(path)} is larger than 10 MB. Open it anyway?", "Open Anyway", "Cancel");
            if (r != 0) return null;
        }

        string text;
        try { text = await File.ReadAllTextAsync(path); }
        catch (Exception ex)
        {
            await Dlg.InfoAsync(this, "Could not open file", ex.Message);
            return null;
        }

        if (text.Contains('\0'))
        {
            var r = await Dlg.ChoiceAsync(this, "Binary file", $"{Path.GetFileName(path)} looks like a binary file. Open it as text?", "Open Anyway", "Cancel");
            if (r != 0) return null;
        }

        var tab = new EditorTab(path, text, _s, _s.IsDark);
        AttachTab(tab);
        if (activate) _tabs.SelectedItem = tab.Item;
        return tab;
    }

    async Task OpenLocationAsync(string path, int line1, int col1)
    {
        var tab = await OpenFileAsync(path);
        if (tab == null) return;
        // let the tab content attach and lay out before scrolling
        Dispatcher.UIThread.Post(() => tab.GoTo(line1, Math.Max(1, col1)), DispatcherPriority.Background);
    }

    static FilePickerFileType GoFiles => new("Go files") { Patterns = new[] { "*.go" } };

    async Task OpenFileDialogAsync()
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open File",
            AllowMultiple = true,
            FileTypeFilter = new[] { GoFiles, FilePickerFileTypes.All }
        });
        foreach (var f in picked)
        {
            var p = f.TryGetLocalPath();
            if (p != null) await OpenFileAsync(p);
        }
    }

    async Task OpenFolderDialogAsync()
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open Folder",
            AllowMultiple = false
        });
        var p = picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
        if (p != null) await OpenFolderAsync(p);
    }

    async Task OpenFolderAsync(string path, bool restartLsp = true)
    {
        path = PathUtil.Norm(path);
        if (!Directory.Exists(path)) return;

        _root = path;
        _s.LastFolder = path;
        _s.RecentFolders.RemoveAll(p => PathUtil.Same(p, path));
        _s.RecentFolders.Insert(0, path);
        if (_s.RecentFolders.Count > 10) _s.RecentFolders.RemoveRange(10, _s.RecentFolders.Count - 10);
        SettingsStore.Save(_s);

        BuildTree(null);
        UpdateWelcome();
        UpdateTitle();
        if (restartLsp) await StartLspAsync();
    }

    // ------------------------------------------------------------ save / close

    Task SaveCurrentAsync() => Current is { } t ? SaveTabAsync(t) : Task.CompletedTask;

    Task SaveCurrentAsAsync() => Current is { } t ? SaveAsAsync(t) : Task.CompletedTask;

    async Task<bool> SaveTabAsync(EditorTab tab, bool format = true)
    {
        if (tab.FilePath == null) return await SaveAsAsync(tab);

        if (format && _s.FormatOnSave && tab.IsGo)
        {
            try { await FormatTabAsync(tab, quiet: true); }
            catch { /* never block saving on a formatter failure */ }
        }

        try
        {
            await File.WriteAllTextAsync(tab.FilePath, tab.Editor.Text, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            await Dlg.InfoAsync(this, "Save failed", ex.Message);
            return false;
        }

        tab.MarkSaved();
        if (_lsp != null && tab.LspOpen)
        {
            _lsp.DidChange(tab.FilePath, tab.Editor.Text, tab.Version);
            _lsp.DidSave(tab.FilePath);
        }
        if (ReferenceEquals(Current, tab)) UpdateTitle();
        return true;
    }

    async Task<bool> SaveAsAsync(EditorTab tab)
    {
        IStorageFolder? start = null;
        if (_root != null) start = await StorageProvider.TryGetFolderFromPathAsync(_root);

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save As",
            SuggestedFileName = tab.DisplayName,
            DefaultExtension = "go",
            SuggestedStartLocation = start,
            FileTypeChoices = new[] { GoFiles, FilePickerFileTypes.All }
        });
        var path = file?.TryGetLocalPath();
        if (path == null) return false;

        bool wasOpenInLsp = tab.LspOpen && tab.FilePath != null;
        if (wasOpenInLsp && _lsp != null) _lsp.DidClose(tab.FilePath!);

        tab.SetPath(PathUtil.Norm(path));
        tab.LspOpen = false;
        bool ok = await SaveTabAsync(tab, format: true);

        if (_lsp != null && tab.IsGo && tab.FilePath != null)
        {
            _lsp.DidOpen(tab.FilePath, tab.Editor.Text, tab.Version);
            tab.LspOpen = true;
        }
        RefreshTree();
        UpdateTitle();
        return ok;
    }

    async Task<bool> SaveAllAsync(bool format = true, bool promptUntitled = false)
    {
        foreach (var t in _editors.ToList())
        {
            if (!t.IsDirty) continue;
            if (t.FilePath == null && !promptUntitled) continue;
            _tabs.SelectedItem = t.Item;
            if (!await SaveTabAsync(t, format)) return false;
        }
        return true;
    }

    Task CloseCurrentAsync() => Current is { } t ? CloseTabAsync(t) : Task.CompletedTask;

    async Task CloseAllAsync()
    {
        foreach (var t in _editors.ToList())
            if (!await CloseTabAsync(t)) return;
    }

    async Task<bool> CloseTabAsync(EditorTab tab)
    {
        if (tab.IsDirty)
        {
            _tabs.SelectedItem = tab.Item;
            var r = await Dlg.ChoiceAsync(this, "Unsaved changes", $"Save changes to {tab.DisplayName}?", "Save", "Don't Save", "Cancel");
            if (r == 0) { if (!await SaveTabAsync(tab)) return false; }
            else if (r != 1) return false;
        }

        if (_lsp != null && tab.LspOpen && tab.FilePath != null) _lsp.DidClose(tab.FilePath);
        _tabs.Items.Remove(tab.Item);
        _editors.Remove(tab);

        if (_tabs.SelectedItem == null && _editors.Count > 0)
            _tabs.SelectedItem = _editors[^1].Item;

        UpdateWelcome();
        UpdateStatus();
        UpdateTitle();
        if (_editors.Count == 0) _symbols.Clear();
        return true;
    }

    // ------------------------------------------------------------ explorer tree

    void BuildTree(HashSet<string>? expand)
    {
        _tree.Items.Clear();
        if (_root == null) return;
        var rootItem = MakeDirItem(_root, expand, isRoot: true);
        _tree.Items.Add(rootItem);
        rootItem.IsExpanded = true;
    }

    void RefreshTree()
    {
        if (_root == null) return;
        var expanded = new HashSet<string>(PathUtil.Cmp);
        CollectExpanded(_tree.Items, expanded);
        BuildTree(expanded);
    }

    static void CollectExpanded(System.Collections.IEnumerable items, HashSet<string> into)
    {
        foreach (var o in items)
        {
            if (o is not TreeViewItem ti) continue;
            if (ti.IsExpanded && ti.Tag is string p && p != "__ph") into.Add(p);
            CollectExpanded(ti.Items, into);
        }
    }

    static Control TreeHeader(string badge, string badgeColor, string name)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        sp.Children.Add(new TextBlock
        {
            Text = badge,
            Foreground = Br(badgeColor),
            FontWeight = FontWeight.Bold,
            FontSize = 11,
            MinWidth = 24,
            VerticalAlignment = VerticalAlignment.Center
        });
        sp.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    TreeViewItem MakeDirItem(string path, HashSet<string>? expand, bool isRoot)
    {
        var name = isRoot ? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) : Path.GetFileName(path);
        if (string.IsNullOrEmpty(name)) name = path;

        var item = new TreeViewItem { Header = TreeHeader("▸", "#8AA4B8", name), Tag = path };
        item.Items.Add(new TreeViewItem { Header = "…", Tag = "__ph" });

        bool loaded = false;
        item.PropertyChanged += (_, e) =>
        {
            if (e.Property == TreeViewItem.IsExpandedProperty && item.IsExpanded && !loaded)
            {
                loaded = true;
                PopulateDir(item, path, expand);
            }
        };

        var menu = new ContextMenu();
        menu.Items.Add(MI("New File…", () => Fire(() => NewFileInAsync(path))));
        menu.Items.Add(MI("New Folder…", () => Fire(() => NewFolderAsync(path))));
        menu.Items.Add(new Separator());
        if (!isRoot) menu.Items.Add(MI("Rename…", () => Fire(() => RenameAsync(path))));
        if (!isRoot) menu.Items.Add(MI("Delete", () => Fire(() => DeleteAsync(path))));
        menu.Items.Add(MI("Reveal in File Manager", () => Reveal(path)));
        menu.Items.Add(MI("Copy Path", () => Fire(() => CopyTextAsync(path))));
        menu.Items.Add(MI("Refresh", RefreshTree));
        item.ContextMenu = menu;

        if (expand != null && expand.Contains(path)) item.IsExpanded = true;
        return item;
    }

    TreeViewItem MakeFileItem(string path)
    {
        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var (badge, color) = ext switch
        {
            ".go" => ("go", "#00ADD8"),
            ".mod" or ".sum" or ".work" => ("mod", "#C586C0"),
            ".md" => ("md", "#6A9955"),
            ".json" or ".yaml" or ".yml" or ".toml" => ("{}", "#DCDCAA"),
            _ => ("·", "#808080")
        };

        var item = new TreeViewItem { Header = TreeHeader(badge, color, name), Tag = path };
        var menu = new ContextMenu();
        menu.Items.Add(MI("Open", () => Fire(() => OpenFileAsync(path))));
        menu.Items.Add(new Separator());
        menu.Items.Add(MI("Rename…", () => Fire(() => RenameAsync(path))));
        menu.Items.Add(MI("Delete", () => Fire(() => DeleteAsync(path))));
        menu.Items.Add(MI("Reveal in File Manager", () => Reveal(path)));
        menu.Items.Add(MI("Copy Path", () => Fire(() => CopyTextAsync(path))));
        item.ContextMenu = menu;
        return item;
    }

    void PopulateDir(TreeViewItem item, string path, HashSet<string>? expand)
    {
        item.Items.Clear();
        try
        {
            foreach (var d in Directory.GetDirectories(path).OrderBy(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase))
            {
                if (PathUtil.IgnoredDirs.Contains(Path.GetFileName(d))) continue;
                item.Items.Add(MakeDirItem(d, expand, isRoot: false));
            }
            foreach (var f in Directory.GetFiles(path).OrderBy(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase))
                item.Items.Add(MakeFileItem(f));
        }
        catch (Exception ex)
        {
            AppendLine("Could not read folder: " + ex.Message, Kind.Error);
        }
    }

    void OpenSelectedTreeFile()
    {
        if (_tree.SelectedItem is TreeViewItem { Tag: string p } && File.Exists(p))
            Fire(() => OpenFileAsync(p));
    }

    // ------------------------------------------------------------ file operations

    static string GuessPackage(string dir)
    {
        try
        {
            foreach (var f in Directory.GetFiles(dir, "*.go"))
            {
                foreach (var line in File.ReadLines(f).Take(40))
                {
                    var t = line.Trim();
                    if (t.StartsWith("package ", StringComparison.Ordinal))
                    {
                        var name = t.Substring(8).Split(' ', '\t', '/')[0];
                        if (name.Length > 0) return name;
                    }
                }
            }
        }
        catch
        {
            // fall through
        }

        var folder = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).ToLowerInvariant();
        var sb = new StringBuilder();
        foreach (var c in folder) if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
        if (sb.Length == 0 || char.IsDigit(sb[0])) return "main";
        return sb.ToString();
    }

    async Task NewFileInAsync(string? dir)
    {
        if (dir == null) { await Dlg.InfoAsync(this, "New File", "Open a folder first."); return; }
        var name = await Dlg.PromptAsync(this, "New File", "File name (you can include sub-folders, e.g. internal/db/db.go):", "main.go");
        if (string.IsNullOrWhiteSpace(name)) return;

        var path = PathUtil.Norm(Path.Combine(dir, name.Trim()));
        if (!PathUtil.IsUnder(path, dir)) { await Dlg.InfoAsync(this, "New File", "The file must be inside the selected folder."); return; }
        if (File.Exists(path) || Directory.Exists(path)) { await Dlg.InfoAsync(this, "New File", "That path already exists."); return; }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var content = PathUtil.IsGo(path)
                ? $"package {GuessPackage(Path.GetDirectoryName(path)!)}\n\n"
                : "";
            if (PathUtil.IsGo(path) && content.StartsWith("package main", StringComparison.Ordinal)
                && Path.GetFileName(path).Equals("main.go", StringComparison.OrdinalIgnoreCase))
                content += "func main() {\n\t\n}\n";
            await File.WriteAllTextAsync(path, content, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            await Dlg.InfoAsync(this, "New File", ex.Message);
            return;
        }

        RefreshTree();
        await OpenFileAsync(path);
    }

    async Task NewFolderAsync(string dir)
    {
        var name = await Dlg.PromptAsync(this, "New Folder", "Folder name:");
        if (string.IsNullOrWhiteSpace(name)) return;
        var path = PathUtil.Norm(Path.Combine(dir, name.Trim()));
        if (!PathUtil.IsUnder(path, dir)) { await Dlg.InfoAsync(this, "New Folder", "The folder must be inside the selected folder."); return; }
        try { Directory.CreateDirectory(path); }
        catch (Exception ex) { await Dlg.InfoAsync(this, "New Folder", ex.Message); return; }
        RefreshTree();
    }

    async Task RenameAsync(string path)
    {
        var isDir = Directory.Exists(path);
        var current = Path.GetFileName(path);
        var name = await Dlg.PromptAsync(this, "Rename", "New name:", current);
        if (string.IsNullOrWhiteSpace(name) || name == current) return;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            await Dlg.InfoAsync(this, "Rename", "That name contains invalid characters.");
            return;
        }

        var target = Path.Combine(Path.GetDirectoryName(path)!, name.Trim());
        if (File.Exists(target) || Directory.Exists(target)) { await Dlg.InfoAsync(this, "Rename", "A file or folder with that name already exists."); return; }

        try
        {
            if (isDir) Directory.Move(path, target);
            else File.Move(path, target);
        }
        catch (Exception ex)
        {
            await Dlg.InfoAsync(this, "Rename", ex.Message);
            return;
        }

        foreach (var t in _editors.Where(t => t.FilePath != null && PathUtil.IsUnder(t.FilePath, path)).ToList())
        {
            var rest = Path.GetRelativePath(path, t.FilePath!);
            var newPath = rest == "." ? target : Path.Combine(target, rest);
            if (_lsp != null && t.LspOpen) _lsp.DidClose(t.FilePath!);
            t.SetPath(PathUtil.Norm(newPath));
            t.LspOpen = false;
            if (_lsp != null && t.IsGo)
            {
                _lsp.DidOpen(t.FilePath!, t.Editor.Text, t.Version);
                t.LspOpen = true;
            }
        }
        RefreshTree();
        UpdateTitle();
    }

    async Task DeleteAsync(string path)
    {
        var isDir = Directory.Exists(path);
        var r = await Dlg.ChoiceAsync(this, "Delete",
            $"Permanently delete {(isDir ? "the folder" : "the file")} \"{Path.GetFileName(path)}\"?\nThis cannot be undone.",
            "Cancel", "Delete");
        if (r != 1) return;

        try
        {
            if (isDir) Directory.Delete(path, true);
            else File.Delete(path);
        }
        catch (Exception ex)
        {
            await Dlg.InfoAsync(this, "Delete failed", ex.Message);
            return;
        }

        foreach (var t in _editors.Where(t => t.FilePath != null && PathUtil.IsUnder(t.FilePath, path)).ToList())
        {
            // the file is gone: close without prompting
            if (_lsp != null && t.LspOpen) _lsp.DidClose(t.FilePath!);
            _tabs.Items.Remove(t.Item);
            _editors.Remove(t);
        }
        if (_tabs.SelectedItem == null && _editors.Count > 0) _tabs.SelectedItem = _editors[^1].Item;
        UpdateWelcome();
        UpdateStatus();
        RefreshTree();
    }

    static void Reveal(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start("explorer.exe", File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"");
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", File.Exists(path) ? new[] { "-R", path } : new[] { path });
            else
                Process.Start("xdg-open", File.Exists(path) ? Path.GetDirectoryName(path)! : path);
        }
        catch
        {
            // no file manager available
        }
    }

    async Task CopyTextAsync(string text)
    {
        if (Clipboard != null) await Clipboard.SetTextAsync(text);
    }

    async Task QuickOpenAsync()
    {
        if (_root == null)
        {
            await Dlg.InfoAsync(this, "Quick Open", "Open a folder first (Ctrl+Shift+O).");
            return;
        }
        var win = new QuickOpenWindow(_root);
        var path = await win.ShowDialog<string?>(this);
        if (path != null) await OpenFileAsync(path);
    }
}
