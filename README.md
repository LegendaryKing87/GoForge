# GoForge

A Go IDE built with **.NET 8**, **Avalonia 11** and **AvaloniaEdit**.

Created by (Github: @LegendaryKing87) — https://github.com/LegendaryKing87

## Requirements

- .NET SDK 8
- Go (https://go.dev/dl/) on your PATH, or set its location in Settings
- Optional but recommended: `gopls` — install from inside the IDE with **Go ▸ Install / Update gopls**

## Run

```
dotnet restore
dotnet run
```

Open a folder or file directly: `dotnet run -- C:\path\to\project`

## Publish a standalone build

```
dotnet publish -c Release -r win-x64   --self-contained -p:PublishSingleFile=true
dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
dotnet publish -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true
```

## Features

- Multi-tab editor (AvaloniaEdit) with Go syntax highlighting (dark and light palettes), line numbers,
  auto-indent, auto-closing brackets/quotes, find (built-in search panel), Ctrl+wheel zoom
- Explorer tree with create / rename / delete / reveal / copy path, Quick Open (Ctrl+P), Find in Files
- gopls integration over LSP: completion, hover, diagnostics (squiggles + Problems panel),
  go to definition (F12 / Ctrl+click), find references, document outline, format document
- Go tooling: run, run file, build, build executable, install, test (all / package / coverage / race),
  vet, mod init / tidy, get, generate, fmt — output streams live, `file.go:12:3` lines are clickable,
  compiler errors feed the Problems panel, stdin box sends input to the running program
- Refactoring through gopls: organize imports (also on save), quick fixes and code actions (Ctrl+.),
  rename symbol across the project (F2), signature help while typing call arguments
- Debugging with Delve (DAP): breakpoints in the gutter (persisted per file), step over / into / out,
  call stack, expandable variables, value-on-hover while paused, program output in the Output panel
- Git: current branch, ahead/behind and change count in the status bar; modified, added, untracked and
  deleted files are coloured in the Explorer
- Format on save (gopls, falling back to gofmt), save-before-run, session restore, recent folders
- Settings dialog (theme, font, tab width, word wrap, whitespace, tool paths) and an About dialog

## Shortcuts

| Action                                          | Keys                                              |
| ----------------------------------------------- | ------------------------------------------------- |
| New / Open file / Open folder                   | Ctrl+N / Ctrl+O / Ctrl+Shift+O                    |
| Save / Save As / Save All                       | Ctrl+S / Ctrl+Shift+S / Ctrl+Alt+S                |
| Close tab                                       | Ctrl+W                                            |
| Quick Open                                      | Ctrl+P                                            |
| Find / Find in Files / Go to Line               | Ctrl+F / Ctrl+Shift+F / Ctrl+G                    |
| Run / Run file / Stop                           | F5 / Ctrl+F5 / Shift+F5                           |
| Build / Test / Test package                     | Ctrl+Shift+B / Ctrl+T / Ctrl+Shift+T              |
| Go to Definition / References                   | F12 / Shift+F12                                   |
| Completion                                      | Ctrl+Space (also after typing `.`)                |
| Format document / Organize imports | Shift+Alt+F / Shift+Alt+O |
| Toggle comment / duplicate / delete / move line | Ctrl+/ / Ctrl+D / Ctrl+Shift+K / Alt+Up, Alt+Down |
| Toggle Explorer / Output panel                  | Ctrl+B / Ctrl+J                                   |
| Zoom in / out / reset                           | Ctrl+= / Ctrl+- / Ctrl+0                          |
| Quick fix / Rename symbol | Ctrl+. / F2 |
| Start or continue debugging / Stop | F6 / Shift+F6 |
| Step over / into / out | F10 / F11 / Shift+F11 |
| Toggle breakpoint | F9 (or click the gutter) |
| Settings                                        | Ctrl+,                                            |

## Project layout

```
GoForge.csproj      Program.cs   App.cs
Models/             settings + data items
Services/           GoToolchain (go/gofmt discovery, process runner), LspClient (gopls), PathUtil
Editing/            EditorTab, Go highlighting, diagnostics renderer, completion data
Dialogs/            About, Settings, Quick Open, message/prompt helpers
UI/                 MainWindow (layout + menus), .Files, .Go, .Lsp partials
```

The UI is built in code (no XAML), so there is nothing to keep in sync with generated fields.

## Limitations

- Debugging needs Delve (`Go > Install Delve`). Delve's DAP mode has no console input, so programs that read
  from stdin should be run with F5 instead. GUI and server programs debug fine.
- Breakpoints are stored by line number and do not follow the line when you insert or delete text above them.
- Rename and other multi-file edits open the affected files as unsaved tabs, so you can review and undo them.
- Not included: snippet completion, a built-in terminal, and Git operations (commit, diff, branches).

## Notes

If a NuGet version in `GoForge.csproj` is unavailable on your feed, bump `Avalonia*` and
`Avalonia.AvaloniaEdit` to the latest 11.x release.



## License / Warranty

GoForge: A Go IDE built with **.NET 8**, **Avalonia 11** and **AvaloniaEdit**.
Copyright (C) 2026  Joel R. Frank - @LegendaryKing87

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU Affero General Public License as
published by the Free Software Foundation, either version 3 of the
License, or (at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU Affero General Public License for more details.

You should have received a copy of the GNU Affero General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
