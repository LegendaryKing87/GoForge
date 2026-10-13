using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using GoForge.Dialogs;
using GoForge.Models;
using GoForge.Services;

namespace GoForge;

public partial class MainWindow
{
    enum Kind { Normal, Error, Info, Success }

    static readonly Regex LinkRx = new(
        @"(?<f>(?:[A-Za-z]:)?[^\s:*?""<>|()\[\]]+?\.go):(?<l>\d+)(?::(?<c>\d+))?",
        RegexOptions.Compiled);

    static readonly Regex ProblemRx = new(
        @"^\s*(?<f>(?:[A-Za-z]:)?[^\s:*?""<>|][^:*?""<>|]*?\.go):(?<l>\d+):(?:(?<c>\d+):)?\s+(?<m>.+)$",
        RegexOptions.Compiled);

    readonly List<ProblemItem> _buildProblems = new();
    readonly Dictionary<string, List<ProblemItem>> _lspProblems = new(PathUtil.Cmp);

    /// <summary>Runs an async action from a UI event, reporting failures to the Output panel.</summary>
    async void Fire(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            AppendLine("Error: " + ex.Message, Kind.Error);
        }
    }

    // ------------------------------------------------------------ output panel

    IBrush BrushFor(Kind kind) => kind switch
    {
        Kind.Error => Br(_s.IsDark ? "#F48771" : "#C72E0F"),
        Kind.Info => Br(_s.IsDark ? "#7FB7E8" : "#1A6FB5"),
        Kind.Success => Br(_s.IsDark ? "#89D185" : "#217A2B"),
        _ => Br(_s.IsDark ? "#D4D4D4" : "#1F1F1F")
    };

    void AppendLine(string text, Kind kind, string? workDir = null, string? file = null, int line = 0, int col = 0)
    {
        _output.Add(new OutputLine { Text = text, Brush = BrushFor(kind), FilePath = file, Line = line, Column = col });
        if (_output.Count > 6000)
            for (int i = 0; i < 500; i++) _output.RemoveAt(0);
        _outputList.ScrollIntoView(_output.Count - 1);
    }

    void ClearOutput() => _output.Clear();

    async Task CopyOutputAsync()
        => await CopyTextAsync(string.Join(Environment.NewLine, _output.Select(o => o.Text)));

    void AppendProcessLine(string text, bool isErr, string workDir, bool compile, string cmdName)
    {
        string? file = null;
        int ln = 0, col = 1;

        var m = LinkRx.Match(text);
        if (m.Success)
        {
            var f = m.Groups["f"].Value;
            try
            {
                var full = PathUtil.Norm(Path.IsPathRooted(f) ? f : Path.Combine(workDir, f));
                if (File.Exists(full))
                {
                    file = full;
                    ln = int.Parse(m.Groups["l"].Value);
                    if (m.Groups["c"].Success) col = int.Parse(m.Groups["c"].Value);
                }
            }
            catch
            {
                // not a usable path
            }
        }

        AppendLine(text, isErr ? Kind.Error : Kind.Normal, workDir, file, ln, col);

        if (compile && file != null)
        {
            var pm = ProblemRx.Match(text);
            if (pm.Success)
            {
                _buildProblems.Add(new ProblemItem
                {
                    FilePath = file,
                    Line = ln,
                    Column = col,
                    Severity = 1,
                    Message = pm.Groups["m"].Value.Trim(),
                    Source = "go " + cmdName
                });
            }
        }
    }

    // ------------------------------------------------------------ problems

    static string ProblemKey(ProblemItem p) => $"{PathUtil.Norm(p.FilePath).ToLowerInvariant()}|{p.Line}|{p.Message}";

    void RebuildProblems()
    {
        var all = new List<ProblemItem>();
        foreach (var kv in _lspProblems) all.AddRange(kv.Value);

        var seen = new HashSet<string>(all.Select(ProblemKey));
        foreach (var b in _buildProblems)
            if (seen.Add(ProblemKey(b))) all.Add(b);

        var ordered = all
            .OrderBy(p => p.Severity)
            .ThenBy(p => p.FilePath, PathUtil.Cmp)
            .ThenBy(p => p.Line)
            .ToList();

        _problems.Clear();
        foreach (var p in ordered) _problems.Add(p);

        int errors = ordered.Count(p => p.Severity == 1);
        int warnings = ordered.Count(p => p.Severity == 2);
        _problemsTab.Header = $"Problems ({ordered.Count})";
        _stDiag.Text = $"✖ {errors}  ⚠ {warnings}";
    }

    // ------------------------------------------------------------ running go commands

    string ActiveDir()
    {
        if (Current?.FilePath is { } p && Path.GetDirectoryName(p) is { } d) return d;
        return _root ?? Environment.CurrentDirectory;
    }

    string ModuleDir() => GoToolchain.ModuleRoot(ActiveDir()) ?? _root ?? ActiveDir();

    void SetRunning(bool running)
    {
        _runBtn.IsEnabled = !running;
        _stopBtn.IsEnabled = running;
    }

    void StopRun()
    {
        if (!_runner.IsRunning) return;
        _runner.Kill();
        AppendLine("Stop requested.", Kind.Info);
    }

    static string Quote(string a) => a.Contains(' ') ? "\"" + a + "\"" : a;

    async Task<int> RunGoAsync(string workDir, IList<string> args, bool compile = true)
    {
        if (_runner.IsRunning)
        {
            ShowBottom(_outputTab);
            AppendLine("A process is already running. Stop it first (Shift+F5).", Kind.Error);
            return -1;
        }

        var go = GoToolchain.FindExecutable("go", _s.GoPath);
        if (go == null)
        {
            ShowBottom(_outputTab);
            AppendLine("Go was not found. Install it from https://go.dev/dl/ or set its path in Settings.", Kind.Error);
            return -1;
        }

        if (_s.SaveBeforeRun) await SaveAllAsync(format: false);

        ClearOutput();
        ShowBottom(_outputTab);
        _buildProblems.Clear();
        RebuildProblems();

        var cmdName = args.Count > 0 ? args[0] : "go";
        AppendLine($"$ go {string.Join(' ', args.Select(Quote))}", Kind.Info);
        AppendLine($"  in {workDir}", Kind.Info);

        SetRunning(true);
        var sw = Stopwatch.StartNew();
        int code;
        try
        {
            code = await _runner.RunAsync(go, args, workDir,
                (line, isErr) => Dispatcher.UIThread.Post(() => AppendProcessLine(line, isErr, workDir, compile, cmdName)));
        }
        catch (Exception ex)
        {
            AppendLine("Failed to start: " + ex.Message, Kind.Error);
            code = -1;
        }
        SetRunning(false);

        RebuildProblems();
        AppendLine($"Process finished with exit code {code} ({sw.Elapsed.TotalSeconds:0.00}s)",
            code == 0 ? Kind.Success : Kind.Error);

        RefreshTree();
        return code;
    }

    List<string> WithArgs(params string[] first)
    {
        var list = first.ToList();
        list.AddRange(GoToolchain.SplitArgs(_argsBox.Text));
        return list;
    }

    Task RunPackageAsync() => RunGoAsync(ActiveDir(), WithArgs("run", "."));

    async Task RunFileAsync()
    {
        if (Current?.FilePath is not { } p || !PathUtil.IsGo(p))
        {
            await Dlg.InfoAsync(this, "Run File", "Open and save a .go file first.");
            return;
        }
        await RunGoAsync(Path.GetDirectoryName(p)!, WithArgs("run", Path.GetFileName(p)));
    }

    Task BuildAllAsync() => RunGoAsync(ModuleDir(), new[] { "build", "./..." });

    async Task BuildExecutableAsync()
    {
        var dir = ActiveDir();
        var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(name)) name = "app";
        var output = Path.Combine("bin", GoToolchain.ExeName(name));
        var code = await RunGoAsync(dir, new[] { "build", "-o", output, "." });
        if (code == 0) AppendLine($"Built {Path.Combine(dir, output)}", Kind.Success);
    }

    Task InstallAsync() => RunGoAsync(ActiveDir(), new[] { "install", "." });
    Task TestAllAsync() => RunGoAsync(ModuleDir(), new[] { "test", "./..." });
    Task TestPackageAsync() => RunGoAsync(ActiveDir(), new[] { "test", "-v", "." });
    Task TestCoverAsync() => RunGoAsync(ModuleDir(), new[] { "test", "-cover", "./..." });
    Task TestRaceAsync() => RunGoAsync(ModuleDir(), new[] { "test", "-race", "./..." });
    Task VetAsync() => RunGoAsync(ModuleDir(), new[] { "vet", "./..." });
    Task ModTidyAsync() => RunGoAsync(ModuleDir(), new[] { "mod", "tidy" }, compile: false);
    Task GenerateAsync() => RunGoAsync(ModuleDir(), new[] { "generate", "./..." });
    Task FmtAllAsync() => RunGoAsync(ModuleDir(), new[] { "fmt", "./..." }, compile: false);

    async Task ModInitAsync()
    {
        var dir = _root ?? ActiveDir();
        var def = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).ToLowerInvariant();
        var name = await Dlg.PromptAsync(this, "go mod init", "Module path:", def, "e.g. github.com/you/project");
        if (string.IsNullOrWhiteSpace(name)) return;
        var code = await RunGoAsync(dir, new[] { "mod", "init", name.Trim() }, compile: false);
        if (code == 0) await StartLspAsync();
    }

    async Task GetPackageAsync()
    {
        var pkg = await Dlg.PromptAsync(this, "go get", "Package to add:", "", "e.g. github.com/gin-gonic/gin@latest");
        if (string.IsNullOrWhiteSpace(pkg)) return;
        await RunGoAsync(ModuleDir(), new[] { "get", pkg.Trim() }, compile: false);
    }

    async Task InstallGoplsAsync()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var code = await RunGoAsync(home, new[] { "install", "golang.org/x/tools/gopls@latest" }, compile: false);
        if (code == 0)
        {
            AppendLine("gopls installed. Restarting the language server…", Kind.Info);
            var go = GoToolchain.FindExecutable("go", _s.GoPath);
            await Task.Run(() => GoToolchain.DetectEnv(go));
            await StartLspAsync();
        }
    }

    async Task ShowGoEnvAsync()
    {
        await RefreshGoVersionAsync();
        await RunGoAsync(ActiveDir(), new[] { "env" }, compile: false);
        if (_goVersion != null) AppendLine(_goVersion, Kind.Info);
    }

    // ------------------------------------------------------------ find in files

    static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".go", ".mod", ".sum", ".work", ".md", ".txt", ".json", ".yaml", ".yml", ".toml", ".html", ".css",
        ".js", ".ts", ".sql", ".proto", ".sh", ".bat", ".cfg", ".ini", ".xml", ".tmpl", ".tpl", ".env"
    };

    void ShowResults(List<ResultItem> items, string info)
    {
        _results.Clear();
        foreach (var r in items) _results.Add(r);
        _resultsInfo.Text = info;
        ShowBottom(_searchTab);
    }

    async Task SearchInFilesAsync()
    {
        var query = _searchBox.Text;
        if (string.IsNullOrEmpty(query)) return;
        if (_root == null)
        {
            _resultsInfo.Text = "Open a folder first.";
            return;
        }

        bool matchCase = _searchCase.IsChecked == true;
        Regex? rx = null;
        if (_searchRegex.IsChecked == true)
        {
            try { rx = new Regex(query, matchCase ? RegexOptions.None : RegexOptions.IgnoreCase); }
            catch (ArgumentException ex)
            {
                _resultsInfo.Text = "Invalid regular expression: " + ex.Message;
                return;
            }
        }

        var cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var root = _root;
        _resultsInfo.Text = "Searching…";

        var (items, files, truncated) = await Task.Run(() =>
        {
            var list = new List<ResultItem>();
            int fileCount = 0;
            bool limit = false;

            void Scan(string dir)
            {
                if (limit) return;
                IEnumerable<string> entries;
                try { entries = Directory.EnumerateFileSystemEntries(dir); }
                catch { return; }

                foreach (var entry in entries)
                {
                    if (limit) return;
                    if (Directory.Exists(entry))
                    {
                        if (!PathUtil.IgnoredDirs.Contains(Path.GetFileName(entry))) Scan(entry);
                        continue;
                    }
                    if (!TextExtensions.Contains(Path.GetExtension(entry))) continue;

                    try
                    {
                        if (new FileInfo(entry).Length > 2_000_000) continue;
                        int n = 0;
                        bool any = false;
                        foreach (var line in File.ReadLines(entry))
                        {
                            n++;
                            int idx;
                            if (rx != null)
                            {
                                var m = rx.Match(line);
                                if (!m.Success) continue;
                                idx = m.Index;
                            }
                            else
                            {
                                idx = line.IndexOf(query, cmp);
                                if (idx < 0) continue;
                            }

                            any = true;
                            var shown = line.Trim();
                            if (shown.Length > 200) shown = shown.Substring(0, 200) + "…";
                            list.Add(new ResultItem
                            {
                                FilePath = entry,
                                Line = n,
                                Column = idx + 1,
                                Display = $"{Path.GetRelativePath(root, entry)}:{n}:{idx + 1}    {shown}"
                            });
                            if (list.Count >= 3000) { limit = true; break; }
                        }
                        if (any) fileCount++;
                    }
                    catch
                    {
                        // unreadable file
                    }
                }
            }

            Scan(root);
            return (list, fileCount, limit);
        });

        ShowResults(items, $"{items.Count} result(s) in {files} file(s)" + (truncated ? " — limited to the first 3000" : ""));
    }
}
