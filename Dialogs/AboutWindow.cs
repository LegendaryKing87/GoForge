using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace GoForge.Dialogs;

public sealed class AboutWindow : Window
{
    public const string GitHubUrl = "https://github.com/LegendaryKing87";

    public AboutWindow(string goInfo, string goplsInfo)
    {
        Title = "About GoForge";
        Width = 580;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var logo = new Border
        {
            Width = 76,
            Height = 76,
            CornerRadius = new CornerRadius(18),
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#00ADD8"), 0),
                    new GradientStop(Color.Parse("#007D9C"), 1)
                }
            },
            Child = new TextBlock
            {
                Text = "GF",
                FontSize = 32,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var version = typeof(AboutWindow).Assembly.GetName().Version;
        var versionText = version == null ? "1.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";

        var link = new Button
        {
            Content = GitHubUrl,
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.Parse("#3794FF")),
            Cursor = new Cursor(StandardCursorType.Hand),
            Padding = new Thickness(6, 2)
        };
        link.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(GitHubUrl) { UseShellExecute = true }); }
            catch { /* no browser available */ }
        };

        var close = new Button
        {
            Content = "Close",
            MinWidth = 96,
            IsDefault = true,
            HorizontalAlignment = HorizontalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 18, 0, 0)
        };
        close.Classes.Add("accent");
        close.Click += (_, _) => Close();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        var root = new StackPanel { Margin = new Thickness(28, 24), Spacing = 6 };
        root.Children.Add(logo);
        root.Children.Add(new TextBlock
        {
            Text = "GoForge",
            FontSize = 30,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0)
        });
        root.Children.Add(new TextBlock
        {
            Text = $"Version {versionText}",
            Opacity = 0.7,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        root.Children.Add(new TextBlock
        {
            Text = "A fast, friendly IDE for the Go programming language.",
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 10)
        });

        root.Children.Add(new Separator { Margin = new Thickness(0, 4) });
        root.Children.Add(new TextBlock
        {
            Text = "Created by Joel R. Frank (Github: @LegendaryKing87) - PrinTech Solutions\nLicense: GNU AFFERO GENERAL PUBLIC LICENSE\nVersion 3.0",
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0)
        });
        root.Children.Add(link);
        root.Children.Add(new Separator { Margin = new Thickness(0, 4) });

        root.Children.Add(Info("Go toolchain", goInfo));
        root.Children.Add(Info("Language server", goplsInfo));
        root.Children.Add(Info("Runtime", RuntimeInformation.FrameworkDescription));
        root.Children.Add(Info("Platform", RuntimeInformation.OSDescription));
        root.Children.Add(new TextBlock
        {
            Text = "Built with Avalonia UI and AvaloniaEdit on .NET 8.",
            Opacity = 0.65,
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0)
        });
        root.Children.Add(close);

        Content = root;
    }

    static Control Info(string label, string value)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*"), Margin = new Thickness(0, 1) };
        var l = new TextBlock { Text = label, Opacity = 0.65, FontSize = 12 };
        var v = new TextBlock { Text = value, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(v, 1);
        grid.Children.Add(l);
        grid.Children.Add(v);
        return grid;
    }
}
