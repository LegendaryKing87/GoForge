namespace GoForge.Services;

public static class PathUtil
{
    public static readonly StringComparer Cmp =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    public static string Norm(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    public static bool Same(string? a, string? b)
        => a != null && b != null && Cmp.Equals(Norm(a), Norm(b));

    public static bool IsUnder(string path, string folder)
    {
        var p = Norm(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var f = Norm(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Cmp.Equals(p, f)) return true;
        var cmp = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return p.StartsWith(f + Path.DirectorySeparatorChar, cmp);
    }

    public static bool IsGo(string? path)
        => path != null && path.EndsWith(".go", StringComparison.OrdinalIgnoreCase);

    public static string ToUri(string path) => new Uri(Norm(path)).AbsoluteUri;

    public static string FromUri(string uri)
    {
        if (!uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return uri;

        var p = Uri.UnescapeDataString(uri.Substring(7));
        if (OperatingSystem.IsWindows() && p.Length > 2 && p[0] == '/' && p[2] == ':')
            p = p.Substring(1);
        return Norm(p);
    }

    public static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".idea", ".vs", "node_modules"
    };
}
