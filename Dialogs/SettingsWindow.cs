using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using GoForge.Models;

namespace GoForge.Dialogs;

public sealed class SettingsWindow : Window
{
    readonly AppSettings _s;
    readonly ComboBox _theme = new();
    readonly TextBox _font = new();
    readonly NumericUpDown _size = new() { Minimum = 8, Maximum = 48, Increment = 1, FormatString = "0" };
    readonly NumericUpDown _tab = new() { Minimum = 1, Maximum = 16, Increment = 1, FormatString = "0" };
    readonly CheckBox _wrap = new() { Content = "Word wrap" };
    readonly CheckBox _ws = new() { Content = "Show whitespace characters" };
    readonly CheckBox _fmt = new() { Content = "Format Go files on save (gopls / gofmt)" };
    readonly CheckBox _saveRun = new() { Content = "Save all files before running or building" };
    readonly CheckBox _brackets = new() { Content = "Auto-close brackets and quotes" };
    readonly CheckBox _gopls = new() { Content = "Use gopls (completion, diagnostics, navigation)" };
    readonly CheckBox _organize = new() { Content = "Organize imports on save (gopls)" };
    readonly TextBox _dlvPath = new() { Watermark = "Auto-detect (PATH, GOPATH/bin)" };
    readonly TextBox _goPath = new() { Watermark = "Auto-detect (PATH, GOROOT, GOPATH)" };
    readonly TextBox _goplsPath = new() { Watermark = "Auto-detect (PATH, GOPATH/bin)" };

    public bool Saved { get; private set; }

    public SettingsWindow(AppSettings settings)
    {
        _s = settings;
        Title = "Settings";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _theme.Items.Add("Dark");
        _theme.Items.Add("Light");
        _theme.SelectedIndex = settings.IsDark ? 0 : 1;
        _theme.MinWidth = 160;

        _font.Text = settings.FontFamily;
        _size.Value = (decimal)settings.FontSize;
        _tab.Value = settings.TabSize;
        _wrap.IsChecked = settings.WordWrap;
        _ws.IsChecked = settings.ShowWhitespace;
        _fmt.IsChecked = settings.FormatOnSave;
        _saveRun.IsChecked = settings.SaveBeforeRun;
        _brackets.IsChecked = settings.AutoCloseBrackets;
        _gopls.IsChecked = settings.UseGopls;
        _organize.IsChecked = settings.OrganizeImportsOnSave;
        _dlvPath.Text = settings.DlvPath;
        _goPath.Text = settings.GoPath;
        _goplsPath.Text = settings.GoplsPath;

        var root = new StackPanel { Margin = new Thickness(22), Spacing = 6 };

        root.Children.Add(Section("Appearance"));
        root.Children.Add(Row("Theme", _theme));
        root.Children.Add(Row("Font family", _font));
        root.Children.Add(Row("Font size", _size));

        root.Children.Add(Section("Editor"));
        root.Children.Add(Row("Tab width", _tab));
        root.Children.Add(_wrap);
        root.Children.Add(_ws);
        root.Children.Add(_brackets);
        root.Children.Add(_fmt);
        root.Children.Add(_organize);

        root.Children.Add(Section("Go tooling"));
        root.Children.Add(_saveRun);
        root.Children.Add(_gopls);
        root.Children.Add(Row("go executable", _goPath));
        root.Children.Add(Row("gopls executable", _goplsPath));
        root.Children.Add(Row("dlv (Delve) executable", _dlvPath));

        var ok = new Button { Content = "Save", MinWidth = 90, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Classes.Add("accent");
        var cancel = new Button { Content = "Cancel", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) => { Apply(); Saved = true; Close(true); };
        cancel.Click += (_, _) => Close(false);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 14, 0, 0)
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(false); };
    }

    static Control Section(string title)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 2) };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = Avalonia.Media.FontWeight.SemiBold });
        panel.Children.Add(new Separator { Margin = new Thickness(0, 4, 0, 0) });
        return panel;
    }

    static Control Row(string label, Control editor)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*") };
        var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(editor, 1);
        editor.HorizontalAlignment = editor is TextBox ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        grid.Children.Add(l);
        grid.Children.Add(editor);
        return grid;
    }

    void Apply()
    {
        _s.Theme = _theme.SelectedIndex == 1 ? "Light" : "Dark";
        if (!string.IsNullOrWhiteSpace(_font.Text)) _s.FontFamily = _font.Text.Trim();
        _s.FontSize = (double)(_size.Value ?? 14);
        _s.TabSize = (int)(_tab.Value ?? 4);
        _s.WordWrap = _wrap.IsChecked == true;
        _s.ShowWhitespace = _ws.IsChecked == true;
        _s.FormatOnSave = _fmt.IsChecked == true;
        _s.SaveBeforeRun = _saveRun.IsChecked == true;
        _s.AutoCloseBrackets = _brackets.IsChecked == true;
        _s.UseGopls = _gopls.IsChecked == true;
        _s.OrganizeImportsOnSave = _organize.IsChecked == true;
        _s.DlvPath = _dlvPath.Text?.Trim() ?? "";
        _s.GoPath = _goPath.Text?.Trim() ?? "";
        _s.GoplsPath = _goplsPath.Text?.Trim() ?? "";
    }
}
