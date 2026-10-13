using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using GoForge.Services;

namespace GoForge.Dialogs;

/// <summary>Fuzzy file picker over the open folder. Close result is the chosen full path (or null).</summary>
public sealed class QuickOpenWindow : Window
{
    sealed class Entry
    {
        public string Rel { get; init; } = "";
        public string Full { get; init; } = "";
        public string Name { get; init; } = "";
        public override string ToString() => $"{Name}    —    {Rel}";
    }

    readonly List<Entry> _all = new();
    readonly TextBox _box = new() { Watermark = "Type to search files…" };
    readonly ListBox _list = new();

    public QuickOpenWindow(string root)
    {
        Title = "Quick Open";
        Width = 680;
        Height = 440;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var dock = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(_box, Dock.Top);
        _box.Margin = new Thickness(0, 0, 0, 8);
        dock.Children.Add(_box);
        dock.Children.Add(_list);
        Content = dock;

        _box.TextChanged += (_, _) => Filter();
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Down) { Move(1); e.Handled = true; }
            else if (e.Key == Key.Up) { Move(-1); e.Handled = true; }
            else if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
        };
        _list.DoubleTapped += (_, _) => Accept();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(null); };
        Opened += (_, _) => _box.Focus();

        _ = LoadAsync(root);
    }

    async Task LoadAsync(string root)
    {
        var entries = await Task.Run(() =>
        {
            var list = new List<Entry>();
            Walk(root, root, list);
            return list;
        });
        _all.AddRange(entries);
        Filter();
    }

    static void Walk(string root, string dir, List<Entry> into)
    {
        if (into.Count >= 30000) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir))
                into.Add(new Entry { Full = f, Name = Path.GetFileName(f), Rel = Path.GetRelativePath(root, f) });
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                if (PathUtil.IgnoredDirs.Contains(Path.GetFileName(d))) continue;
                Walk(root, d, into);
            }
        }
        catch
        {
            // unreadable directory
        }
    }

    void Filter()
    {
        var terms = (_box.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<Entry> q = _all;
        if (terms.Length > 0)
            q = q.Where(e => terms.All(t => e.Rel.Contains(t, StringComparison.OrdinalIgnoreCase)));

        var first = terms.Length > 0 ? terms[0] : "";
        var result = q
            .OrderByDescending(e => first.Length > 0 && e.Name.StartsWith(first, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(e => first.Length > 0 && e.Name.Contains(first, StringComparison.OrdinalIgnoreCase))
            .ThenBy(e => e.Rel.Length)
            .Take(200)
            .ToList();

        _list.ItemsSource = result;
        if (result.Count > 0) _list.SelectedIndex = 0;
    }

    void Move(int delta)
    {
        int count = (_list.ItemsSource as List<Entry>)?.Count ?? 0;
        if (count == 0) return;
        _list.SelectedIndex = Math.Clamp(_list.SelectedIndex + delta, 0, count - 1);
        _list.ScrollIntoView(_list.SelectedIndex);
    }

    void Accept()
    {
        if (_list.SelectedItem is Entry e) Close(e.Full);
    }
}
