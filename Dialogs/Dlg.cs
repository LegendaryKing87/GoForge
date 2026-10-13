using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace GoForge.Dialogs;

public static class Dlg
{
    /// <summary>Shows a message with buttons. Returns the clicked button index, or -1 if dismissed.</summary>
    public static async Task<int> ChoiceAsync(Window owner, string title, string message, params string[] buttons)
    {
        var win = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 380,
            MaxWidth = 640,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 580 };
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 18, 0, 0)
        };

        for (int i = 0; i < buttons.Length; i++)
        {
            int idx = i;
            var b = new Button { Content = buttons[i], MinWidth = 84, HorizontalContentAlignment = HorizontalAlignment.Center };
            if (i == 0) { b.IsDefault = true; b.Classes.Add("accent"); }
            b.Click += (_, _) => win.Close(idx);
            row.Children.Add(b);
        }

        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(text);
        root.Children.Add(row);
        win.Content = root;
        win.KeyDown += (_, e) => { if (e.Key == Key.Escape) win.Close(-1); };

        var result = await win.ShowDialog<int?>(owner);
        return result ?? -1;
    }

    public static Task InfoAsync(Window owner, string title, string message)
        => ChoiceAsync(owner, title, message, "OK");

    /// <summary>Single-line text prompt. Returns null if cancelled.</summary>
    public static async Task<string?> PromptAsync(Window owner, string title, string label, string initial = "", string? hint = null)
    {
        var win = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var box = new TextBox { Text = initial, Margin = new Thickness(0, 6, 0, 0) };
        var ok = new Button { Content = "OK", MinWidth = 84, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Classes.Add("accent");
        var cancel = new Button { Content = "Cancel", MinWidth = 84, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) => win.Close(box.Text ?? "");
        cancel.Click += (_, _) => win.Close(null);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 18, 0, 0)
        };
        row.Children.Add(ok);
        row.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        root.Children.Add(box);
        if (!string.IsNullOrEmpty(hint))
            root.Children.Add(new TextBlock { Text = hint, Opacity = 0.65, FontSize = 12, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap });
        root.Children.Add(row);
        win.Content = root;

        win.KeyDown += (_, e) => { if (e.Key == Key.Escape) win.Close(null); };
        win.Opened += (_, _) => { box.Focus(); box.SelectAll(); };

        return await win.ShowDialog<string?>(owner);
    }
}
