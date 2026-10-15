using System.Text.Json;

namespace GoForge.Models;

public sealed class AppSettings
{
    public string Theme { get; set; } = "Dark";
    public string FontFamily { get; set; } = "Cascadia Code, Consolas, Menlo, DejaVu Sans Mono, monospace";
    public double FontSize { get; set; } = 14;
    public int TabSize { get; set; } = 4;
    public bool WordWrap { get; set; }
    public bool ShowWhitespace { get; set; }
    public bool FormatOnSave { get; set; } = true;
    public bool SaveBeforeRun { get; set; } = true;
    public bool AutoCloseBrackets { get; set; } = true;
    public bool UseGopls { get; set; } = true;
    public bool OrganizeImportsOnSave { get; set; } = true;
    public string GoPath { get; set; } = "";
    public string GoplsPath { get; set; } = "";
    public string DlvPath { get; set; } = "";

    public string? LastFolder { get; set; }
    public List<string> RecentFolders { get; set; } = new();
    public List<string> OpenFiles { get; set; } = new();
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;
    public bool Maximized { get; set; }
    /// <summary>Breakpoints per file (1-based line numbers).</summary>
    public Dictionary<string, List<int>> Breakpoints { get; set; } = new();

    public bool IsDark => !string.Equals(Theme, "Light", StringComparison.OrdinalIgnoreCase);
}

public static class SettingsStore
{
    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GoForge");
    static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // corrupt settings -> fall back to defaults
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // best effort
        }
    }
}
