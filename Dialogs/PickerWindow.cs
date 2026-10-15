using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace GoForge.Dialogs;

/// <summary>Shows a list of choices. The dialog result is the chosen index (or -1 when cancelled).</summary>
public sealed class PickerWindow : Window
{
    readonly ListBox _list = new();

    public PickerWindow(string title, IList<string> items)
    {
        Title = title;
        Width = 560;
        Height = Math.Clamp(120 + items.Count * 34, 200, 480);
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _list.ItemsSource = items.ToList();
        _list.SelectedIndex = items.Count > 0 ? 0 : -1;
        _list.Margin = new Thickness(12);
        Content = _list;

        _list.DoubleTapped += (_, _) => Accept();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close(-1);
            else if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
        };
        Opened += (_, _) => _list.Focus();
    }

    void Accept()
    {
        if (_list.SelectedIndex >= 0) Close(_list.SelectedIndex);
    }
}
