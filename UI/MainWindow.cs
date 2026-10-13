using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using GoForge.Dialogs;
using GoForge.Editing;
using GoForge.Models;
using GoForge.Services;

namespace GoForge;

public partial class MainWindow : Window
{
    // ------------------------------------------------------------ state
    readonly AppSettings _s = SettingsStore.Load();
    readonly List<EditorTab> _editors = new();
    readonly List<(KeyGesture Gesture, Action Action)> _shortcuts = new();

    readonly TabControl _tabs = new();
    readonly TreeView _tree = new();
    readonly Grid _mainGrid = new();
    readonly Grid _centerGrid = new();
    readonly Grid _sidebar = new();
    readonly Border _welcome = new();
    readonly StackPanel _welcomeRecent = new() { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
    readonly TabControl _bottomTabs = new();

    readonly ObservableCollection<OutputLine> _output = new();
    readonly ListBox _outputList = new();
    readonly ObservableCollection<ProblemItem> _problems = new();
    readonly ListBox _problemsList = new();
    readonly ObservableCollection<ResultItem> _results = new();
    readonly ListBox _resultsList = new();
    readonly ObservableCollection<SymbolItem> _symbols = new();
    readonly ListBox _symbolsList = new();

    readonly TextBlock _stGopls = new() { Text = "gopls: off" };
    readonly TextBlock _stGo = new() { Text = "Go: …" };
    readonly TextBlock _stDiag = new() { Text = "✖ 0  ⚠ 0" };
    readonly TextBlock _stPos = new() { Text = "" };
    readonly TextBlock _stLang = new() { Text = "" };
    readonly TextBox _argsBox = new() { Watermark = "program arguments", Width = 240 };
    readonly TextBox _stdinBox = new() { Watermark = "stdin: type here and press Enter to send to the running program" };
    readonly TextBox _searchBox = new() { Watermark = "Find in files…", Width = 320 };
    readonly CheckBox _searchCase = new() { Content = "Aa" };
    readonly CheckBox _searchRegex = new() { Content = ".*" };
    readonly TextBlock _resultsInfo = new() { Opacity = 0.7, FontSize = 12, Margin = new Thickness(8, 2) };

    readonly ProcessRunner _runner = new();
    Button _runBtn = null!;
    Button _stopBtn = null!;
    TabItem _outputTab = null!;
    TabItem _problemsTab = null!;
    TabItem _searchTab = null!;
    MenuItem _recentMenu = null!;

    string? _root;
    string? _goVersion;
    bool _sidebarVisible = true;
    bool _bottomVisible = true;
    double _bottomHeight = 230;
    double _sidebarWidth = 270;

    static readonly FontFamily Mono = new("Cascadia Code, Consolas, Menlo, DejaVu Sans Mono, monospace");

    public MainWindow()
    {
        Title = "GoForge";
        Width = Math.Max(900, _s.WindowWidth);
        Height = Math.Max(560, _s.WindowHeight);
        MinWidth = 820;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (_s.Maximized) WindowState = WindowState.Maximized;

        Content = BuildLayout();
        ApplyTheme();
        UpdateWelcome();

        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
        Opened += async (_, _) => await OnOpenedAsync();
        Closing += OnClosing;
    }

    // ------------------------------------------------------------ helpers

    static KeyGesture G(Key key, KeyModifiers mods = KeyModifiers.None) => new(key, mods);
    const KeyModifiers Ctrl = KeyModifiers.Control;
    const KeyModifiers Shift = KeyModifiers.Shift;
    const KeyModifiers Alt = KeyModifiers.Alt;

    static IBrush Br(string hex) => new SolidColorBrush(Color.Parse(hex));

    MenuItem MI(string header, Action action, KeyGesture? gesture = null, bool register = true)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        if (gesture != null)
        {
            item.InputGesture = gesture;
            if (register) _shortcuts.Add((gesture, action));
        }
        return item;
    }

    EditorTab? Current => _editors.FirstOrDefault(t => ReferenceEquals(t.Item, _tabs.SelectedItem));

    void WithEditor(Action<EditorTab> action)
    {
        if (Current is { } t) action(t);
    }

    void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        foreach (var (gesture, action) in _shortcuts)
        {
            if (gesture.Matches(e))
            {
                e.Handled = true;
                action();
                return;
            }
        }
    }

    // ------------------------------------------------------------ layout

    Control BuildLayout()
    {
        var dock = new DockPanel();

        var top = new StackPanel();
        top.Children.Add(BuildMenu());
        top.Children.Add(BuildToolbar());
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);

        var status = BuildStatusBar();
        DockPanel.SetDock(status, Dock.Bottom);
        dock.Children.Add(status);

        dock.Children.Add(BuildMain());
        return dock;
    }

    Control BuildMenu()
    {
        var menu = new Menu();

        // ---- File
        var file = new MenuItem { Header = "_File" };
        file.Items.Add(MI("_New File", NewFile, G(Key.N, Ctrl)));
        file.Items.Add(MI("_Open File…", () => Fire(OpenFileDialogAsync), G(Key.O, Ctrl)));
        file.Items.Add(MI("Open _Folder…", () => Fire(OpenFolderDialogAsync), G(Key.O, Ctrl | Shift)));
        _recentMenu = new MenuItem { Header = "Open _Recent" };
        file.Items.Add(_recentMenu);
        file.Items.Add(new Separator());
        file.Items.Add(MI("_Save", () => Fire(SaveCurrentAsync), G(Key.S, Ctrl)));
        file.Items.Add(MI("Save _As…", () => Fire(SaveCurrentAsAsync), G(Key.S, Ctrl | Shift)));
        file.Items.Add(MI("Save A_ll", () => Fire(() => SaveAllAsync()), G(Key.S, Ctrl | Alt)));
        file.Items.Add(new Separator());
        file.Items.Add(MI("_Close Tab", () => Fire(CloseCurrentAsync), G(Key.W, Ctrl)));
        file.Items.Add(MI("Close All Tabs", () => Fire(CloseAllAsync), G(Key.W, Ctrl | Shift)));
        file.Items.Add(new Separator());
        file.Items.Add(MI("Se_ttings…", () => Fire(ShowSettingsAsync), G(Key.OemComma, Ctrl)));
        file.Items.Add(MI("E_xit", Close, G(Key.F4, Alt), register: false));
        menu.Items.Add(file);

        // ---- Edit
        var edit = new MenuItem { Header = "_Edit" };
        edit.Items.Add(MI("_Undo", () => WithEditor(t => t.Editor.Undo()), G(Key.Z, Ctrl), false));
        edit.Items.Add(MI("_Redo", () => WithEditor(t => t.Editor.Redo()), G(Key.Y, Ctrl), false));
        edit.Items.Add(new Separator());
        edit.Items.Add(MI("Cu_t", () => WithEditor(t => t.Editor.Cut()), G(Key.X, Ctrl), false));
        edit.Items.Add(MI("_Copy", () => WithEditor(t => t.Editor.Copy()), G(Key.C, Ctrl), false));
        edit.Items.Add(MI("_Paste", () => WithEditor(t => t.Editor.Paste()), G(Key.V, Ctrl), false));
        edit.Items.Add(MI("Select _All", () => WithEditor(t => t.Editor.SelectAll()), G(Key.A, Ctrl), false));
        edit.Items.Add(new Separator());
        edit.Items.Add(MI("_Find…", () => WithEditor(t => t.Search.Open()), G(Key.F, Ctrl), false));
        edit.Items.Add(MI("Find _Next", () => WithEditor(t => t.Search.FindNext()), G(Key.F3)));
        edit.Items.Add(MI("Find Pre_vious", () => WithEditor(t => t.Search.FindPrevious()), G(Key.F3, Shift)));
        edit.Items.Add(MI("Find in F_iles…", FocusSearch, G(Key.F, Ctrl | Shift)));
        edit.Items.Add(MI("_Go to Line…", () => Fire(GoToLineAsync), G(Key.G, Ctrl)));
        edit.Items.Add(new Separator());
        edit.Items.Add(MI("Toggle Co_mment", () => WithEditor(t => t.ToggleComment()), G(Key.OemQuestion, Ctrl)));
        edit.Items.Add(MI("_Duplicate Line", () => WithEditor(t => t.DuplicateLine()), G(Key.D, Ctrl)));
        edit.Items.Add(MI("Delete _Line", () => WithEditor(t => t.DeleteLine()), G(Key.K, Ctrl | Shift)));
        edit.Items.Add(MI("Move Line U_p", () => WithEditor(t => t.MoveLines(true)), G(Key.Up, Alt)));
        edit.Items.Add(MI("Move Line Dow_n", () => WithEditor(t => t.MoveLines(false)), G(Key.Down, Alt)));
        edit.Items.Add(new Separator());
        edit.Items.Add(MI("_Format Document", () => Fire(FormatCurrentAsync), G(Key.F, Shift | Alt)));
        edit.Items.Add(MI("Trigger Co_mpletion", () => Fire(() => ShowCompletionAsync(Current, true)), G(Key.Space, Ctrl)));
        menu.Items.Add(edit);

        // ---- Navigate
        var nav = new MenuItem { Header = "_Navigate" };
        nav.Items.Add(MI("Quick _Open File…", () => Fire(QuickOpenAsync), G(Key.P, Ctrl)));
        nav.Items.Add(MI("Go to _Definition", () => Fire(GoToDefinitionAtCaretAsync), G(Key.F12)));
        nav.Items.Add(MI("Find _References", () => Fire(FindReferencesAtCaretAsync), G(Key.F12, Shift)));
        menu.Items.Add(nav);

        // ---- Go
        var go = new MenuItem { Header = "_Go" };
        go.Items.Add(MI("_Run Package", () => Fire(RunPackageAsync), G(Key.F5)));
        go.Items.Add(MI("Run Current _File", () => Fire(RunFileAsync), G(Key.F5, Ctrl)));
        go.Items.Add(MI("_Stop", StopRun, G(Key.F5, Shift)));
        go.Items.Add(new Separator());
        go.Items.Add(MI("_Build All (go build ./...)", () => Fire(BuildAllAsync), G(Key.B, Ctrl | Shift)));
        go.Items.Add(MI("Build _Executable", () => Fire(BuildExecutableAsync)));
        go.Items.Add(MI("_Install (go install)", () => Fire(InstallAsync)));
        go.Items.Add(new Separator());
        go.Items.Add(MI("_Test All (go test ./...)", () => Fire(TestAllAsync), G(Key.T, Ctrl)));
        go.Items.Add(MI("Test _Package (verbose)", () => Fire(TestPackageAsync), G(Key.T, Ctrl | Shift)));
        go.Items.Add(MI("Test with _Coverage", () => Fire(TestCoverAsync)));
        go.Items.Add(MI("Test with _Race Detector", () => Fire(TestRaceAsync)));
        go.Items.Add(MI("_Vet (go vet ./...)", () => Fire(VetAsync)));
        go.Items.Add(new Separator());
        go.Items.Add(MI("Mod _Init…", () => Fire(ModInitAsync)));
        go.Items.Add(MI("Mod T_idy", () => Fire(ModTidyAsync)));
        go.Items.Add(MI("Get _Package…", () => Fire(GetPackageAsync)));
        go.Items.Add(MI("Generate (go generate)", () => Fire(GenerateAsync)));
        go.Items.Add(MI("Format All (go fmt ./...)", () => Fire(FmtAllAsync)));
        go.Items.Add(new Separator());
        go.Items.Add(MI("Install / Update gop_ls", () => Fire(InstallGoplsAsync)));
        go.Items.Add(MI("Restart gopls", () => Fire(StartLspAsync)));
        go.Items.Add(MI("Show Go _Version and Env", () => Fire(ShowGoEnvAsync)));
        menu.Items.Add(go);

        // ---- View
        var view = new MenuItem { Header = "_View" };
        view.Items.Add(MI("Toggle _Explorer", ToggleSidebar, G(Key.B, Ctrl)));
        view.Items.Add(MI("Toggle _Output Panel", ToggleBottom, G(Key.J, Ctrl)));
        view.Items.Add(MI("_Clear Output", ClearOutput));
        view.Items.Add(new Separator());
        view.Items.Add(MI("Zoom _In", () => Zoom(1), G(Key.OemPlus, Ctrl)));
        view.Items.Add(MI("Zoom O_ut", () => Zoom(-1), G(Key.OemMinus, Ctrl)));
        view.Items.Add(MI("_Reset Zoom", () => Zoom(0), G(Key.D0, Ctrl)));
        view.Items.Add(new Separator());
        view.Items.Add(MI("Toggle _Word Wrap", ToggleWrap, G(Key.Z, Alt)));
        view.Items.Add(MI("Toggle _Theme (Dark / Light)", ToggleTheme));
        menu.Items.Add(view);

        // ---- Help
        var help = new MenuItem { Header = "_Help" };
        help.Items.Add(MI("Go _Documentation", () => OpenUrl("https://go.dev/doc/")));
        help.Items.Add(MI("Package _Reference (pkg.go.dev)", () => OpenUrl("https://pkg.go.dev/")));
        help.Items.Add(MI("GoForge on _GitHub", () => OpenUrl(AboutWindow.GitHubUrl)));
        help.Items.Add(new Separator());
        help.Items.Add(MI("_About GoForge", ShowAbout));
        menu.Items.Add(help);

        return menu;
    }

    Control BuildToolbar()
    {
        _runBtn = ToolButton("▶  Run", () => Fire(RunPackageAsync), "Run package (F5)");
        _runBtn.Classes.Add("accent");
        _stopBtn = ToolButton("■  Stop", StopRun, "Stop the running process (Shift+F5)");
        _stopBtn.IsEnabled = false;

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(8, 4) };
        bar.Children.Add(_runBtn);
        bar.Children.Add(_stopBtn);
        bar.Children.Add(ToolButton("Build", () => Fire(BuildAllAsync), "go build ./... (Ctrl+Shift+B)"));
        bar.Children.Add(ToolButton("Test", () => Fire(TestAllAsync), "go test ./... (Ctrl+T)"));
        bar.Children.Add(ToolButton("Vet", () => Fire(VetAsync), "go vet ./..."));
        bar.Children.Add(new Separator { Width = 1, Height = 20, Margin = new Thickness(4, 0) });
        bar.Children.Add(ToolButton("Save", () => Fire(SaveCurrentAsync), "Save (Ctrl+S)"));
        bar.Children.Add(ToolButton("Format", () => Fire(FormatCurrentAsync), "Format document (Shift+Alt+F)"));
        bar.Children.Add(new Separator { Width = 1, Height = 20, Margin = new Thickness(4, 0) });
        bar.Children.Add(new TextBlock { Text = "Args:", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 });
        bar.Children.Add(_argsBox);

        return new Border
        {
            Child = bar,
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = Br("#44808080")
        };
    }

    static Button ToolButton(string text, Action action, string tip)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 4) };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => action();
        return b;
    }

    Control BuildStatusBar()
    {
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        left.Children.Add(_stGopls);
        left.Children.Add(_stGo);
        left.Children.Add(_stDiag);

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        right.Children.Add(_stPos);
        right.Children.Add(_stLang);
        right.Children.Add(new TextBlock { Text = "UTF-8" });

        foreach (var tb in new[] { _stGopls, _stGo, _stDiag, _stPos, _stLang })
        {
            tb.Foreground = Brushes.White;
            tb.FontSize = 12;
        }
        foreach (var child in right.Children.OfType<TextBlock>())
        {
            child.Foreground = Brushes.White;
            child.FontSize = 12;
        }

        var dock = new DockPanel { Margin = new Thickness(10, 3) };
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        dock.Children.Add(left);
        return new Border { Background = Br("#007ACC"), Child = dock };
    }

    Control BuildMain()
    {
        _mainGrid.ColumnDefinitions.Add(new ColumnDefinition(_sidebarWidth, GridUnitType.Pixel) { MinWidth = 160 });
        _mainGrid.ColumnDefinitions.Add(new ColumnDefinition(4, GridUnitType.Pixel));
        _mainGrid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

        var side = BuildSidebar();
        Grid.SetColumn(side, 0);
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background = Brushes.Transparent };
        Grid.SetColumn(splitter, 1);
        var center = BuildCenter();
        Grid.SetColumn(center, 2);

        _mainGrid.Children.Add(side);
        _mainGrid.Children.Add(splitter);
        _mainGrid.Children.Add(center);
        return _mainGrid;
    }

    static Control PanelHeader(string title, params Control[] buttons)
    {
        var dock = new DockPanel { Margin = new Thickness(10, 4, 4, 4) };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var b in buttons) row.Children.Add(b);
        DockPanel.SetDock(row, Dock.Right);
        dock.Children.Add(row);
        dock.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center
        });
        return dock;
    }

    static Button IconButton(string glyph, string tip, Action action)
    {
        var b = new Button
        {
            Content = glyph,
            FontSize = 13,
            Padding = new Thickness(6, 2),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => action();
        return b;
    }

    Control BuildSidebar()
    {
        _sidebar.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
        _sidebar.RowDefinitions.Add(new RowDefinition(4, GridUnitType.Pixel));
        _sidebar.RowDefinitions.Add(new RowDefinition(190, GridUnitType.Pixel) { MinHeight = 60 });

        // Explorer
        var explorer = new DockPanel();
        var header = PanelHeader("EXPLORER",
            IconButton("＋", "New file in project root", () => Fire(() => NewFileInAsync(_root))),
            IconButton("📂", "Open folder…", () => Fire(OpenFolderDialogAsync)),
            IconButton("⟳", "Refresh", RefreshTree));
        DockPanel.SetDock(header, Dock.Top);
        explorer.Children.Add(header);
        _tree.DoubleTapped += (_, _) => OpenSelectedTreeFile();
        _tree.KeyDown += (_, e) => { if (e.Key == Key.Enter) OpenSelectedTreeFile(); };
        explorer.Children.Add(_tree);
        Grid.SetRow(explorer, 0);

        var split = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, Background = Brushes.Transparent };
        Grid.SetRow(split, 1);

        // Outline
        var outline = new DockPanel();
        var oh = PanelHeader("OUTLINE", IconButton("⟳", "Refresh outline", () => Fire(RefreshOutlineAsync)));
        DockPanel.SetDock(oh, Dock.Top);
        outline.Children.Add(oh);
        _symbolsList.ItemsSource = _symbols;
        _symbolsList.ItemTemplate = new FuncDataTemplate<SymbolItem>((o, _) =>
            new TextBlock { Text = o?.Display ?? "", FontSize = 12, FontFamily = Mono });
        _symbolsList.DoubleTapped += (_, _) => GoToSelectedSymbol();
        _symbolsList.KeyDown += (_, e) => { if (e.Key == Key.Enter) GoToSelectedSymbol(); };
        outline.Children.Add(_symbolsList);
        Grid.SetRow(outline, 2);

        _sidebar.Children.Add(explorer);
        _sidebar.Children.Add(split);
        _sidebar.Children.Add(outline);
        return _sidebar;
    }

    Control BuildCenter()
    {
        _centerGrid.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
        _centerGrid.RowDefinitions.Add(new RowDefinition(4, GridUnitType.Pixel));
        _centerGrid.RowDefinitions.Add(new RowDefinition(_bottomHeight, GridUnitType.Pixel) { MinHeight = 80 });

        // editor area = tab control with a welcome panel overlaid when no file is open
        var editorArea = new Grid();
        _tabs.SelectionChanged += (_, _) => OnActiveTabChanged();
        editorArea.Children.Add(_tabs);
        _welcome.Child = BuildWelcome();
        editorArea.Children.Add(_welcome);
        Grid.SetRow(editorArea, 0);

        var split = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, Background = Brushes.Transparent };
        Grid.SetRow(split, 1);

        var bottom = BuildBottom();
        Grid.SetRow(bottom, 2);

        _centerGrid.Children.Add(editorArea);
        _centerGrid.Children.Add(split);
        _centerGrid.Children.Add(bottom);
        return _centerGrid;
    }

    Control BuildWelcome()
    {
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = "GoForge",
            FontSize = 40,
            FontWeight = FontWeight.Bold,
            Foreground = Br("#00ADD8"),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Open a folder or file to start coding in Go",
            Opacity = 0.7,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 14)
        });

        var open = new Button { Content = "Open Folder…   Ctrl+Shift+O", HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 260 };
        open.Classes.Add("accent");
        open.Click += (_, _) => Fire(OpenFolderDialogAsync);
        var openFile = new Button { Content = "Open File…   Ctrl+O", HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 260 };
        openFile.Click += (_, _) => Fire(OpenFileDialogAsync);
        var newFile = new Button { Content = "New File   Ctrl+N", HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 260 };
        newFile.Click += (_, _) => NewFile();
        panel.Children.Add(open);
        panel.Children.Add(openFile);
        panel.Children.Add(newFile);

        panel.Children.Add(new TextBlock
        {
            Text = "Recent folders",
            Opacity = 0.7,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 18, 0, 2)
        });
        panel.Children.Add(_welcomeRecent);
        return panel;
    }

    void UpdateWelcome()
    {
        _welcome.IsVisible = _editors.Count == 0;
        _tabs.IsVisible = _editors.Count > 0;

        _welcomeRecent.Children.Clear();
        foreach (var f in _s.RecentFolders.Where(Directory.Exists).Take(6))
        {
            var folder = f;
            var b = new Button
            {
                Content = folder,
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Br("#3794FF")
            };
            b.Click += (_, _) => Fire(() => OpenFolderAsync(folder));
            _welcomeRecent.Children.Add(b);
        }

        _recentMenu.Items.Clear();
        foreach (var f in _s.RecentFolders.Where(Directory.Exists).Take(10))
        {
            var folder = f;
            _recentMenu.Items.Add(MI(folder, () => Fire(() => OpenFolderAsync(folder))));
        }
        _recentMenu.IsEnabled = _recentMenu.Items.Count > 0;
    }

    Control BuildBottom()
    {
        // Output
        var outputDock = new DockPanel();
        _stdinBox.Margin = new Thickness(4);
        _stdinBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            var line = _stdinBox.Text ?? "";
            if (_runner.IsRunning)
            {
                _runner.WriteStdin(line);
                AppendLine("» " + line, Kind.Info);
            }
            _stdinBox.Text = "";
            e.Handled = true;
        };
        DockPanel.SetDock(_stdinBox, Dock.Bottom);
        outputDock.Children.Add(_stdinBox);
        _outputList.ItemsSource = _output;
        _outputList.ItemTemplate = new FuncDataTemplate<OutputLine>((o, _) =>
        {
            var tb = new TextBlock { Text = o?.Text ?? "", FontFamily = Mono, FontSize = 12.5, TextWrapping = TextWrapping.NoWrap };
            if (o?.Brush != null) tb.Foreground = o.Brush;
            return tb;
        });
        _outputList.DoubleTapped += (_, _) =>
        {
            if (_outputList.SelectedItem is OutputLine { FilePath: not null } l)
                Fire(() => OpenLocationAsync(l.FilePath!, l.Line, l.Column));
        };
        var outMenu = new ContextMenu();
        outMenu.Items.Add(MI("Clear", ClearOutput));
        outMenu.Items.Add(MI("Copy All", () => Fire(CopyOutputAsync)));
        _outputList.ContextMenu = outMenu;
        outputDock.Children.Add(_outputList);

        // Problems
        _problemsList.ItemsSource = _problems;
        _problemsList.ItemTemplate = new FuncDataTemplate<ProblemItem>((o, _) =>
        {
            var tb = new TextBlock { Text = o?.Display ?? "", FontSize = 12.5 };
            // never assign a null Foreground: that hides the text instead of inheriting the theme colour
            switch (o?.Severity)
            {
                case 1: tb.Foreground = Br("#F14C4C"); break;
                case 2: tb.Foreground = Br("#CCA700"); break;
                case 3: tb.Foreground = Br("#3794FF"); break;
            }
            return tb;
        });
        _problemsList.DoubleTapped += (_, _) =>
        {
            if (_problemsList.SelectedItem is ProblemItem p)
                Fire(() => OpenLocationAsync(p.FilePath, p.Line, p.Column));
        };

        // Search
        var searchDock = new DockPanel();
        var searchBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(6, 4) };
        var searchBtn = new Button { Content = "Search" };
        searchBtn.Classes.Add("accent");
        searchBtn.Click += (_, _) => Fire(SearchInFilesAsync);
        _searchBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Fire(SearchInFilesAsync); e.Handled = true; } };
        ToolTip.SetTip(_searchCase, "Match case");
        ToolTip.SetTip(_searchRegex, "Regular expression");
        searchBar.Children.Add(_searchBox);
        searchBar.Children.Add(_searchCase);
        searchBar.Children.Add(_searchRegex);
        searchBar.Children.Add(searchBtn);
        var searchTop = new StackPanel();
        searchTop.Children.Add(searchBar);
        searchTop.Children.Add(_resultsInfo);
        DockPanel.SetDock(searchTop, Dock.Top);
        searchDock.Children.Add(searchTop);
        _resultsList.ItemsSource = _results;
        _resultsList.ItemTemplate = new FuncDataTemplate<ResultItem>((o, _) =>
            new TextBlock { Text = o?.Display ?? "", FontFamily = Mono, FontSize = 12.5 });
        _resultsList.DoubleTapped += (_, _) =>
        {
            if (_resultsList.SelectedItem is ResultItem r)
                Fire(() => OpenLocationAsync(r.FilePath, r.Line, r.Column));
        };
        searchDock.Children.Add(_resultsList);

        _outputTab = new TabItem { Header = "Output", Content = outputDock, FontSize = 12 };
        _problemsTab = new TabItem { Header = "Problems", Content = _problemsList, FontSize = 12 };
        _searchTab = new TabItem { Header = "Search / References", Content = searchDock, FontSize = 12 };
        _bottomTabs.Items.Add(_outputTab);
        _bottomTabs.Items.Add(_problemsTab);
        _bottomTabs.Items.Add(_searchTab);
        return _bottomTabs;
    }

    // ------------------------------------------------------------ view toggles / theme

    void ToggleSidebar()
    {
        _sidebarVisible = !_sidebarVisible;
        if (_sidebarVisible)
        {
            _mainGrid.ColumnDefinitions[0].Width = new GridLength(_sidebarWidth);
            _mainGrid.ColumnDefinitions[0].MinWidth = 160;
        }
        else
        {
            _sidebarWidth = Math.Max(160, _mainGrid.ColumnDefinitions[0].ActualWidth);
            _mainGrid.ColumnDefinitions[0].MinWidth = 0;
            _mainGrid.ColumnDefinitions[0].Width = new GridLength(0);
        }
        _sidebar.IsVisible = _sidebarVisible;
    }

    void ToggleBottom()
    {
        _bottomVisible = !_bottomVisible;
        var row = _centerGrid.RowDefinitions[2];
        if (_bottomVisible)
        {
            row.MinHeight = 80;
            row.Height = new GridLength(_bottomHeight);
        }
        else
        {
            _bottomHeight = Math.Max(80, row.ActualHeight);
            row.MinHeight = 0;
            row.Height = new GridLength(0);
        }
        _bottomTabs.IsVisible = _bottomVisible;
    }

    void ShowBottom(TabItem tab)
    {
        if (!_bottomVisible) ToggleBottom();
        _bottomTabs.SelectedItem = tab;
    }

    void ApplyTheme()
    {
        if (Application.Current != null)
            Application.Current.RequestedThemeVariant = _s.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        foreach (var t in _editors) t.ApplyStyle(_s, _s.IsDark);
    }

    void ApplyStyleToAll()
    {
        foreach (var t in _editors) t.ApplyStyle(_s, _s.IsDark);
    }

    void ToggleTheme()
    {
        _s.Theme = _s.IsDark ? "Light" : "Dark";
        ApplyTheme();
        SettingsStore.Save(_s);
    }

    void ToggleWrap()
    {
        _s.WordWrap = !_s.WordWrap;
        ApplyStyleToAll();
        SettingsStore.Save(_s);
    }

    void Zoom(int direction)
    {
        _s.FontSize = direction == 0 ? 14 : Math.Clamp(_s.FontSize + direction, 8, 48);
        ApplyStyleToAll();
        SettingsStore.Save(_s);
    }

    async Task ShowSettingsAsync()
    {
        var win = new SettingsWindow(_s);
        await win.ShowDialog<bool>(this);
        if (!win.Saved) return;

        SettingsStore.Save(_s);
        ApplyTheme();
        if (_s.UseGopls) await StartLspAsync();
        else await StopLspAsync();
    }

    void ShowAbout()
    {
        var go = GoToolchain.FindExecutable("go", _s.GoPath);
        var gopls = GoToolchain.FindExecutable("gopls", _s.GoplsPath);
        var goInfo = _goVersion ?? (go ?? "not found");
        var win = new AboutWindow(goInfo, gopls ?? "not found");
        _ = win.ShowDialog(this);
    }

    static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser */ }
    }

    void FocusSearch()
    {
        ShowBottom(_searchTab);
        _searchBox.Focus();
        _searchBox.SelectAll();
    }

    async Task GoToLineAsync()
    {
        var tab = Current;
        if (tab == null) return;
        var input = await Dlg.PromptAsync(this, "Go to Line", $"Line number (1 - {tab.Editor.Document.LineCount}):");
        if (input != null && int.TryParse(input.Trim(), out var n)) tab.GoTo(n, 1);
    }

    // ------------------------------------------------------------ status

    void OnActiveTabChanged()
    {
        UpdateStatus();
        UpdateTitle();
        Fire(RefreshOutlineAsync);
    }

    void UpdateTitle()
    {
        var t = Current;
        var folder = _root != null ? Path.GetFileName(_root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) : null;
        Title = t == null
            ? (folder != null ? $"{folder} — GoForge" : "GoForge")
            : $"{t.DisplayName}{(t.IsDirty ? " ●" : "")}{(folder != null ? " — " + folder : "")} — GoForge";
    }

    void UpdateStatus()
    {
        var t = Current;
        if (t == null)
        {
            _stPos.Text = "";
            _stLang.Text = "";
            return;
        }
        var loc = t.Editor.Document.GetLocation(t.Editor.CaretOffset);
        _stPos.Text = $"Ln {loc.Line}, Col {loc.Column}";
        _stLang.Text = t.IsGo ? "Go" : Path.GetExtension(t.DisplayName).TrimStart('.').ToUpperInvariant();
    }
}
