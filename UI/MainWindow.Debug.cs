using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Threading;
using GoForge.Editing;
using GoForge.Models;
using GoForge.Services;

namespace GoForge;

public partial class MainWindow
{
    DapSession? _dap;
    int _dbgThread;
    int _dbgFrame;
    bool _dbgPaused;
    bool _suppressFrameSelect;
    EditorTab? _dbgLineTab;

    readonly ObservableCollection<StackFrameItem> _stack = new();
    readonly ListBox _stackList = new();
    readonly TreeView _varsTree = new();

    Button _dbgContinueBtn = null!;
    Button _dbgOverBtn = null!;
    Button _dbgIntoBtn = null!;
    Button _dbgOutBtn = null!;
    Button _dbgStopBtn = null!;
    Button _debugBtn = null!;
    TabItem _debugTab = null!;

    static string? JStr(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    static int JInt(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;

    // ------------------------------------------------------------ UI

    Control BuildDebugPane()
    {
        _dbgContinueBtn = ToolButton("▶  Continue", () => Fire(() => DebugStepAsync("continue")), "Continue (F6)");
        _dbgOverBtn = ToolButton("Step Over", () => Fire(() => DebugStepAsync("next")), "Step over (F10)");
        _dbgIntoBtn = ToolButton("Step Into", () => Fire(() => DebugStepAsync("stepIn")), "Step into (F11)");
        _dbgOutBtn = ToolButton("Step Out", () => Fire(() => DebugStepAsync("stepOut")), "Step out (Shift+F11)");
        _dbgStopBtn = ToolButton("■  Stop", () => Fire(StopDebugAsync), "Stop debugging (Shift+F6)");

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(6, 4) };
        bar.Children.Add(_dbgContinueBtn);
        bar.Children.Add(_dbgOverBtn);
        bar.Children.Add(_dbgIntoBtn);
        bar.Children.Add(_dbgOutBtn);
        bar.Children.Add(_dbgStopBtn);

        _stackList.ItemsSource = _stack;
        _stackList.ItemTemplate = new FuncDataTemplate<StackFrameItem>((f, _) =>
            new TextBlock { Text = f?.Display ?? "", FontFamily = Mono, FontSize = 12 });
        _stackList.SelectionChanged += (_, _) =>
        {
            if (_suppressFrameSelect) return;
            if (_stackList.SelectedItem is StackFrameItem f) Fire(() => ShowFrameAsync(f));
        };

        var stackPane = new DockPanel();
        var sh = PanelHeader("CALL STACK");
        DockPanel.SetDock(sh, Dock.Top);
        stackPane.Children.Add(sh);
        stackPane.Children.Add(_stackList);

        var varsPane = new DockPanel();
        var vh = PanelHeader("VARIABLES");
        DockPanel.SetDock(vh, Dock.Top);
        varsPane.Children.Add(vh);
        varsPane.Children.Add(_varsTree);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(4, GridUnitType.Pixel));
        grid.ColumnDefinitions.Add(new ColumnDefinition(1.5, GridUnitType.Star));
        Grid.SetColumn(stackPane, 0);
        var split = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background = Avalonia.Media.Brushes.Transparent };
        Grid.SetColumn(split, 1);
        Grid.SetColumn(varsPane, 2);
        grid.Children.Add(stackPane);
        grid.Children.Add(split);
        grid.Children.Add(varsPane);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(grid);
        SetDebugUi();
        return root;
    }

    void SetDebugUi()
    {
        bool active = _dap != null;
        _dbgStopBtn.IsEnabled = active;
        _dbgContinueBtn.IsEnabled = active && _dbgPaused;
        _dbgOverBtn.IsEnabled = active && _dbgPaused;
        _dbgIntoBtn.IsEnabled = active && _dbgPaused;
        _dbgOutBtn.IsEnabled = active && _dbgPaused;

        bool idle = !active && !_runner.IsRunning;
        _runBtn.IsEnabled = idle;
        _debugBtn.IsEnabled = idle;
        _stopBtn.IsEnabled = active || _runner.IsRunning;
    }

    // ------------------------------------------------------------ breakpoints

    IEnumerable<int> BreakpointsFor(string? path)
        => path != null && _s.Breakpoints.TryGetValue(PathUtil.Norm(path), out var list)
            ? list
            : Enumerable.Empty<int>();

    void ToggleBreakpoint(EditorTab tab, int line)
    {
        if (tab.FilePath == null)
        {
            FlashGopls("Save the file before setting breakpoints");
            return;
        }

        var key = PathUtil.Norm(tab.FilePath);
        if (!_s.Breakpoints.TryGetValue(key, out var list)) _s.Breakpoints[key] = list = new List<int>();
        if (!list.Remove(line)) list.Add(line);
        if (list.Count == 0) _s.Breakpoints.Remove(key);

        tab.SetBreakpoints(list);
        SettingsStore.Save(_s);
        if (_dap != null) Fire(() => SendBreakpointsAsync(key));
    }

    void ToggleBreakpointAtCaret()
        => WithEditor(t => ToggleBreakpoint(t, t.Editor.Document.GetLocation(t.Editor.CaretOffset).Line));

    void ClearAllBreakpoints()
    {
        var paths = _s.Breakpoints.Keys.ToList();
        _s.Breakpoints.Clear();
        foreach (var t in _editors) t.SetBreakpoints(Enumerable.Empty<int>());
        SettingsStore.Save(_s);
        if (_dap != null)
            foreach (var p in paths) Fire(() => SendBreakpointsAsync(p));
    }

    async Task SendBreakpointsAsync(string path)
    {
        var dap = _dap;
        if (dap == null) return;

        var bps = new JsonArray();
        foreach (var line in BreakpointsFor(path)) bps.Add(new JsonObject { ["line"] = line });
        await dap.RequestAsync("setBreakpoints", new JsonObject
        {
            ["source"] = new JsonObject { ["path"] = path },
            ["breakpoints"] = bps
        });
    }

    // ------------------------------------------------------------ session lifecycle

    async Task StartDebugAsync()
    {
        if (_dap != null)
        {
            if (_dbgPaused) await DebugStepAsync("continue"); // F6 doubles as Continue
            return;
        }
        if (_runner.IsRunning)
        {
            ShowBottom(_outputTab);
            AppendLine("A process is already running. Stop it first (Shift+F5).", Kind.Error);
            return;
        }

        var dlv = GoToolchain.FindExecutable("dlv", _s.DlvPath);
        if (dlv == null)
        {
            ShowBottom(_outputTab);
            AppendLine("Delve (dlv) was not found. Use Go ▸ Install Delve, or set its path in Settings.", Kind.Error);
            return;
        }

        if (_s.SaveBeforeRun) await SaveAllAsync(format: false);

        var dir = ActiveDir();
        ClearOutput();
        ShowBottom(_outputTab);
        AppendLine("$ dlv dap", Kind.Info);
        AppendLine($"  debugging the package in {dir}", Kind.Info);

        var session = new DapSession();
        session.Log += line => Dispatcher.UIThread.Post(() => AppendLine(line, Kind.Info));
        session.EventReceived += (name, body) => Dispatcher.UIThread.Post(() => HandleDapEvent(session, name, body));
        session.Closed += () => Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_dap, session)) return;
            _dap = null;
            OnDebugEnded();
        });

        var initialized = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.EventReceived += (name, _) =>
        {
            if (name == "initialized") initialized.TrySetResult(true);
        };

        _dap = session;
        _dbgPaused = false;
        SetDebugUi();

        if (!await session.StartAsync(dlv, dir))
        {
            AppendLine("Could not start Delve: " + session.LastError, Kind.Error);
            return; // Closed handler (from Dispose inside StartAsync) resets the UI
        }

        var init = await session.RequestAsync("initialize", new JsonObject
        {
            ["clientID"] = "goforge",
            ["clientName"] = "GoForge",
            ["adapterID"] = "go",
            ["pathFormat"] = "path",
            ["linesStartAt1"] = true,
            ["columnsStartAt1"] = true,
            ["supportsVariableType"] = true,
            ["supportsRunInTerminalRequest"] = false
        });
        if (init == null)
        {
            AppendLine("Debugger handshake failed: " + session.LastError, Kind.Error);
            await StopDebugAsync();
            return;
        }

        var argsArray = new JsonArray();
        foreach (var a in GoToolchain.SplitArgs(_argsBox.Text)) argsArray.Add(a);

        // launch's response only arrives after configurationDone, so don't await it yet
        var launchTask = session.RequestAsync("launch", new JsonObject
        {
            ["request"] = "launch",
            ["mode"] = "debug",
            ["program"] = dir,
            ["cwd"] = dir,
            ["args"] = argsArray,
            ["stopOnEntry"] = false
        }, 180000);

        await Task.WhenAny(initialized.Task, Task.Delay(30000));
        foreach (var path in _s.Breakpoints.Keys.ToList()) await SendBreakpointsAsync(path);
        await session.RequestAsync("configurationDone", new JsonObject());

        var launched = await launchTask;
        if (launched == null)
        {
            AppendLine("Launch failed: " + session.LastError, Kind.Error);
            await StopDebugAsync();
            return;
        }
        AppendLine("Debugger attached. Program is running…", Kind.Success);
        SetDebugUi();
    }

    async Task StopDebugAsync()
    {
        var session = _dap;
        if (session == null) return;
        _dap = null;
        try { await session.RequestAsync("disconnect", new JsonObject { ["terminateDebuggee"] = true }, 4000); }
        catch { /* shutting down anyway */ }
        session.Dispose();
        OnDebugEnded();
    }

    void OnDebugEnded()
    {
        _dbgPaused = false;
        _dbgLineTab?.SetDebugLine(0);
        _dbgLineTab = null;
        _stack.Clear();
        _varsTree.Items.Clear();
        SetDebugUi();
        AppendLine("Debug session ended.", Kind.Info);
    }

    // ------------------------------------------------------------ events and stepping

    void HandleDapEvent(DapSession session, string name, JsonNode? body)
    {
        if (!ReferenceEquals(_dap, session)) return;

        switch (name)
        {
            case "stopped":
                Fire(() => OnStoppedAsync(body));
                break;

            case "continued":
                _dbgPaused = false;
                _dbgLineTab?.SetDebugLine(0);
                SetDebugUi();
                break;

            case "output":
                var category = JStr(body?["category"]);
                var text = JStr(body?["output"]);
                if (text != null && category != "telemetry")
                    foreach (var line in text.Replace("\r", "").TrimEnd('\n').Split('\n'))
                        AppendLine(line, category == "stderr" ? Kind.Error : Kind.Normal);
                break;

            case "exited":
                AppendLine($"Program exited with code {JInt(body?["exitCode"])}", Kind.Info);
                break;

            case "terminated":
                Fire(StopDebugAsync);
                break;
        }
    }

    async Task OnStoppedAsync(JsonNode? body)
    {
        var dap = _dap;
        if (dap == null) return;
        _dbgPaused = true;

        int thread = JInt(body?["threadId"]);
        if (thread == 0)
        {
            var threads = await dap.RequestAsync("threads", null);
            if (threads?["threads"] is JsonArray ta && ta.Count > 0) thread = JInt(ta[0]?["id"]);
        }
        _dbgThread = thread;
        var reason = JStr(body?["reason"]) ?? "paused";

        var st = await dap.RequestAsync("stackTrace", new JsonObject
        {
            ["threadId"] = thread,
            ["startFrame"] = 0,
            ["levels"] = 40
        });

        var frames = new List<StackFrameItem>();
        if (st?["stackFrames"] is JsonArray arr)
        {
            foreach (var f in arr)
            {
                if (f is not JsonObject fo) continue;
                var path = JStr((fo["source"] as JsonObject)?["path"]);
                int line = JInt(fo["line"]);
                var fname = JStr(fo["name"]) ?? "?";
                frames.Add(new StackFrameItem
                {
                    Id = JInt(fo["id"]),
                    Name = fname,
                    FilePath = path != null && File.Exists(path) ? path : null,
                    Line = line,
                    Display = $"{fname}    {(path != null ? Path.GetFileName(path) : "")}:{line}"
                });
            }
        }

        _stack.Clear();
        foreach (var f in frames) _stack.Add(f);

        SetDebugUi();
        ShowBottom(_debugTab);
        Activate();
        AppendLine($"Paused: {reason}", Kind.Info);

        var top = frames.FirstOrDefault(f => f.FilePath != null) ?? frames.FirstOrDefault();
        if (top != null)
        {
            _suppressFrameSelect = true;
            _stackList.SelectedItem = top;
            _suppressFrameSelect = false;
            await ShowFrameAsync(top);
        }
    }

    async Task DebugStepAsync(string command)
    {
        var dap = _dap;
        if (dap == null || !_dbgPaused) return;

        _dbgPaused = false;
        _dbgLineTab?.SetDebugLine(0);
        SetDebugUi();
        await dap.RequestAsync(command, new JsonObject { ["threadId"] = _dbgThread });
    }

    // ------------------------------------------------------------ frames and variables

    async Task ShowFrameAsync(StackFrameItem frame)
    {
        _dbgFrame = frame.Id;
        _dbgLineTab?.SetDebugLine(0);
        _dbgLineTab = null;

        if (frame.FilePath != null)
        {
            var tab = await OpenFileAsync(frame.FilePath);
            if (tab != null)
            {
                _dbgLineTab = tab;
                tab.SetDebugLine(frame.Line);
                Dispatcher.UIThread.Post(() => tab.GoTo(frame.Line, 1), DispatcherPriority.Background);
            }
        }
        await LoadVariablesAsync(frame.Id);
    }

    async Task LoadVariablesAsync(int frameId)
    {
        var dap = _dap;
        _varsTree.Items.Clear();
        if (dap == null) return;

        var scopes = await dap.RequestAsync("scopes", new JsonObject { ["frameId"] = frameId });
        if (scopes?["scopes"] is not JsonArray arr) return;

        bool first = true;
        foreach (var sc in arr)
        {
            if (sc is not JsonObject so) continue;
            var item = MakeVarItem(JStr(so["name"]) ?? "scope", JInt(so["variablesReference"]));
            _varsTree.Items.Add(item);
            if (first) { item.IsExpanded = true; first = false; } // show the first scope (locals) right away
        }
    }

    TreeViewItem MakeVarItem(string text, int variablesReference)
    {
        var item = new TreeViewItem
        {
            Header = new TextBlock { Text = text, FontFamily = Mono, FontSize = 12 },
            Tag = variablesReference
        };

        if (variablesReference > 0)
        {
            item.Items.Add(new TreeViewItem { Header = "…" });
            bool loaded = false;
            item.PropertyChanged += (_, e) =>
            {
                if (e.Property == TreeViewItem.IsExpandedProperty && item.IsExpanded && !loaded)
                {
                    loaded = true;
                    Fire(() => PopulateVariablesAsync(item, variablesReference));
                }
            };
        }
        return item;
    }

    async Task PopulateVariablesAsync(TreeViewItem parent, int variablesReference)
    {
        var dap = _dap;
        if (dap == null) return;

        var body = await dap.RequestAsync("variables", new JsonObject { ["variablesReference"] = variablesReference });
        parent.Items.Clear();
        if (body?["variables"] is not JsonArray arr) return;

        foreach (var v in arr)
        {
            if (v is not JsonObject vo) continue;
            var name = JStr(vo["name"]) ?? "?";
            var value = JStr(vo["value"]) ?? "";
            var type = JStr(vo["type"]);
            var text = type != null ? $"{name} = {value}    ({type})" : $"{name} = {value}";
            parent.Items.Add(MakeVarItem(text, JInt(vo["variablesReference"])));
        }
    }

    /// <summary>Evaluates an expression in the selected frame (used for hover values while paused).</summary>
    async Task<string?> EvaluateAsync(string expression)
    {
        var dap = _dap;
        if (dap == null || !_dbgPaused || string.IsNullOrWhiteSpace(expression)) return null;
        var body = await dap.RequestAsync("evaluate", new JsonObject
        {
            ["expression"] = expression,
            ["frameId"] = _dbgFrame,
            ["context"] = "hover"
        }, 5000);
        return JStr(body?["result"]);
    }

    async Task InstallDelveAsync()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var code = await RunGoAsync(home, new[] { "install", "github.com/go-delve/delve/cmd/dlv@latest" }, compile: false);
        if (code == 0)
        {
            var go = GoToolchain.FindExecutable("go", _s.GoPath);
            await Task.Run(() => GoToolchain.DetectEnv(go));
            AppendLine("Delve installed. You can now debug with F6.", Kind.Success);
        }
    }
}
