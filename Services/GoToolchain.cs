using System.Diagnostics;
using System.Text;

namespace GoForge.Services;

public static class GoToolchain
{
    static string? _goPathEnv;
    static string? _goRootEnv;

    public static string ExeName(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    /// <summary>Finds an executable (go, gopls, gofmt, dlv...). A configured path (file or folder) wins.</summary>
    public static string? FindExecutable(string name, string? configured = null)
    {
        var exe = ExeName(name);

        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured)) return configured;
            if (Directory.Exists(configured))
            {
                var p = Path.Combine(configured, exe);
                if (File.Exists(p)) return p;
                p = Path.Combine(configured, "bin", exe);
                if (File.Exists(p)) return p;
            }
        }

        foreach (var dir in SearchDirs())
        {
            try
            {
                var p = Path.Combine(dir, exe);
                if (File.Exists(p)) return p;
            }
            catch
            {
                // invalid path entry
            }
        }
        return null;
    }

    static IEnumerable<string> SearchDirs()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var d in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return d;

        var goroot = Environment.GetEnvironmentVariable("GOROOT");
        if (!string.IsNullOrWhiteSpace(goroot)) yield return Path.Combine(goroot, "bin");

        var gopath = Environment.GetEnvironmentVariable("GOPATH");
        if (!string.IsNullOrWhiteSpace(gopath))
            foreach (var gp in gopath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                yield return Path.Combine(gp, "bin");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home)) yield return Path.Combine(home, "go", "bin");

        if (_goPathEnv != null) yield return Path.Combine(_goPathEnv, "bin");
        if (_goRootEnv != null) yield return Path.Combine(_goRootEnv, "bin");

        if (OperatingSystem.IsWindows())
        {
            yield return @"C:\Program Files\Go\bin";
            yield return @"C:\Go\bin";
        }
        else
        {
            yield return "/usr/local/go/bin";
            yield return "/usr/lib/go/bin";
            yield return "/opt/homebrew/bin";
            yield return "/usr/local/bin";
            yield return "/snap/bin";
        }
    }

    /// <summary>Asks "go env" for GOPATH/GOROOT so tools installed with "go install" are found. Call off the UI thread.</summary>
    public static void DetectEnv(string? goExe)
    {
        if (goExe == null) return;
        try
        {
            var (code, outp, _) = CaptureAsync(goExe, new[] { "env", "GOPATH", "GOROOT" }, null, null, 8000).GetAwaiter().GetResult();
            if (code != 0) return;
            var lines = outp.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length > 0) _goPathEnv = lines[0];
            if (lines.Length > 1) _goRootEnv = lines[1];
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>Directories to prepend to PATH for child processes.</summary>
    public static string ExtraPath()
    {
        var parts = new List<string>();
        if (_goPathEnv != null) parts.Add(Path.Combine(_goPathEnv, "bin"));
        if (_goRootEnv != null) parts.Add(Path.Combine(_goRootEnv, "bin"));
        return string.Join(Path.PathSeparator, parts.Where(Directory.Exists));
    }

    public static void ApplyPath(ProcessStartInfo psi)
    {
        var extra = ExtraPath();
        if (extra.Length == 0) return;
        var cur = psi.Environment.TryGetValue("PATH", out var v) ? v : "";
        psi.Environment["PATH"] = extra + Path.PathSeparator + cur;
    }

    public static async Task<(int Code, string Out, string Err)> CaptureAsync(
        string exe, IEnumerable<string> args, string? dir = null, string? stdin = null, int timeoutMs = 60000)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = dir ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (stdin != null) psi.StandardInputEncoding = new UTF8Encoding(false);
        foreach (var a in args) psi.ArgumentList.Add(a);
        ApplyPath(psi);

        using var p = Process.Start(psi)!;
        if (stdin != null)
        {
            await p.StandardInput.WriteAsync(stdin);
            p.StandardInput.Close();
        }
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { /* ignore */ }
            return (-1, "", "timed out");
        }
        return (p.ExitCode, await o, await e);
    }

    /// <summary>Walks up from a file/folder looking for go.mod. Returns null if none.</summary>
    public static string? ModuleRoot(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        string? dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "go.mod"))) return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }

    /// <summary>Splits a command line into arguments, honouring double/single quotes.</summary>
    public static List<string> SplitArgs(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var sb = new StringBuilder();
        char quote = '\0';
        bool has = false;
        foreach (var c in text)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else sb.Append(c);
            }
            else if (c == '"' || c == '\'')
            {
                quote = c;
                has = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (has || sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); has = false; }
            }
            else sb.Append(c);
        }
        if (has || sb.Length > 0) result.Add(sb.ToString());
        return result;
    }
}

/// <summary>Runs one child process at a time, streaming its output line by line.</summary>
public sealed class ProcessRunner
{
    Process? _proc;

    public bool IsRunning
    {
        get
        {
            try { return _proc is { HasExited: false }; }
            catch { return false; }
        }
    }

    public async Task<int> RunAsync(string exe, IEnumerable<string> args, string workDir, Action<string, bool> onLine)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        GoToolchain.ApplyPath(psi);

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data, false); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data, true); };

        _proc = p;
        try
        {
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await Task.Run(() => p.WaitForExit()); // parameterless overload also flushes async output
            return p.ExitCode;
        }
        finally
        {
            _proc = null;
            p.Dispose();
        }
    }

    public void WriteStdin(string text)
    {
        try { _proc?.StandardInput.WriteLine(text); }
        catch { /* process may have exited */ }
    }

    public void Kill()
    {
        try { _proc?.Kill(true); }
        catch { /* already gone */ }
    }
}
