using System.Text;
using System.Text.RegularExpressions;

namespace GoForge.Services;

public sealed class GitStatus
{
    public string Branch { get; init; } = "";
    public int Ahead { get; init; }
    public int Behind { get; init; }

    /// <summary>Full path -> state letter: M modified, A added, D deleted, R renamed, ? untracked, U conflict.</summary>
    public Dictionary<string, char> Files { get; } = new(PathUtil.Cmp);
    public HashSet<string> UntrackedDirs { get; } = new(PathUtil.Cmp);
    public HashSet<string> DirtyDirs { get; } = new(PathUtil.Cmp);

    public char? StateOf(string path)
    {
        if (Files.TryGetValue(path, out var c)) return c;
        foreach (var d in UntrackedDirs)
            if (PathUtil.IsUnder(path, d)) return '?';
        return null;
    }

    public bool IsDirtyDir(string path) => DirtyDirs.Contains(path) || UntrackedDirs.Contains(path);

    /// <summary>Changes whenever anything the UI shows changes.</summary>
    public string Signature
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append(Branch).Append('|').Append(Ahead).Append('|').Append(Behind).Append('|');
            foreach (var kv in Files.OrderBy(k => k.Key, StringComparer.Ordinal)) sb.Append(kv.Key).Append(kv.Value).Append(';');
            foreach (var d in UntrackedDirs.OrderBy(k => k, StringComparer.Ordinal)) sb.Append(d).Append('/').Append(';');
            return sb.ToString();
        }
    }
}

public static class GitService
{
    static readonly Regex AheadRx = new(@"ahead (\d+)", RegexOptions.Compiled);
    static readonly Regex BehindRx = new(@"behind (\d+)", RegexOptions.Compiled);

    /// <summary>Returns null when git is missing or the folder is not inside a repository.</summary>
    public static async Task<GitStatus?> GetStatusAsync(string root)
    {
        var git = GoToolchain.FindExecutable("git");
        if (git == null) return null;

        var (c1, top, _) = await GoToolchain.CaptureAsync(git, new[] { "-C", root, "rev-parse", "--show-toplevel" }, null, null, 8000);
        if (c1 != 0) return null;
        var topPath = PathUtil.Norm(top.Trim());

        var (c2, outp, _) = await GoToolchain.CaptureAsync(git, new[] { "-C", root, "status", "--porcelain=v1", "-b" }, null, null, 20000);
        if (c2 != 0) return null;

        string branch = "";
        int ahead = 0, behind = 0;
        var entries = new List<(char State, string Path)>();

        foreach (var raw in outp.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 3) continue;

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                var head = line.Substring(3);
                int dots = head.IndexOf("...", StringComparison.Ordinal);
                int sp = head.IndexOf(' ');
                if (head.StartsWith("No commits yet on ", StringComparison.Ordinal)) branch = head.Substring(18);
                else if (head.StartsWith("HEAD (no branch)", StringComparison.Ordinal)) branch = "detached HEAD";
                else branch = dots >= 0 ? head.Substring(0, dots) : (sp >= 0 ? head.Substring(0, sp) : head);

                var a = AheadRx.Match(head);
                var b = BehindRx.Match(head);
                if (a.Success) ahead = int.Parse(a.Groups[1].Value);
                if (b.Success) behind = int.Parse(b.Groups[1].Value);
                continue;
            }

            char x = line[0], y = line[1];
            var rel = line.Substring(3);
            int arrow = rel.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0) rel = rel.Substring(arrow + 4); // renamed: use the new name
            rel = Unquote(rel);

            char state;
            if ((x == '?' && y == '?')) state = '?';
            else if (x == 'U' || y == 'U' || (x == 'A' && y == 'A') || (x == 'D' && y == 'D')) state = 'U';
            else if (y != ' ') state = y;
            else state = x;

            entries.Add((state, rel));
        }

        var status = new GitStatus { Branch = branch, Ahead = ahead, Behind = behind };
        foreach (var (state, rel) in entries)
        {
            bool isDir = rel.EndsWith('/');
            var full = PathUtil.Norm(Path.Combine(topPath, rel.TrimEnd('/')));
            if (isDir) status.UntrackedDirs.Add(full);
            else status.Files[full] = state;

            // mark every ancestor folder as containing changes
            var dir = Path.GetDirectoryName(full);
            while (!string.IsNullOrEmpty(dir) && PathUtil.IsUnder(dir, topPath))
            {
                if (!status.DirtyDirs.Add(dir)) break;
                if (PathUtil.Same(dir, topPath)) break;
                dir = Path.GetDirectoryName(dir);
            }
        }
        return status;
    }

    static string Unquote(string s)
    {
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
            return s.Substring(1, s.Length - 2).Replace("\\\"", "\"").Replace("\\\\", "\\");
        return s;
    }
}
