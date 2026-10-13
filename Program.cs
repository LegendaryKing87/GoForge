using Avalonia;

namespace GoForge;

internal static class Program
{
    public static string[] StartupArgs { get; private set; } = Array.Empty<string>();

    [STAThread]
    public static void Main(string[] args)
    {
        StartupArgs = args;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GoForge");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "crash.log"), $"[{DateTime.Now:u}] {e.ExceptionObject}{Environment.NewLine}");
            }
            catch
            {
                // ignore - nothing more we can do
            }
        };

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect();
}
