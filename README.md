# ClaudeCodeMDI

A Windows MDI (Multiple Document Interface) terminal application for [Claude Code](https://docs.anthropic.com/en/docs/claude-code) built with Avalonia UI.

Manage multiple Claude Code sessions side-by-side in an Apple-style dark/light themed interface with project explorer, snippet management, and usage tracking.

## Features

- **MDI Terminal Windows** - Open multiple Claude Code sessions in resizable, draggable child windows with Maximize / Tile / Cascade layouts
- **Session Management** - Resume previous Claude Code sessions with history and timestamps
- **Project Explorer** - Browse project file trees with syntax-aware icons and color-coded file types
- **Snippets Panel** - Store and quickly send code snippets to the active console
- **Usage Tracking** - Monitor daily Claude API usage (messages, tool calls, sessions) with a chart view
- **File Drag & Drop** - Drop files onto the terminal to insert their paths (same as Claude Code CLI)
- **Clipboard Image Paste** - Ctrl+V pastes clipboard images as temp file paths for Claude Code
- **Localization** - English and Japanese (日本語) support
- **Dark / Light Theme** - Apple-style Fluent UI design with dynamic theme switching
- **Git Integration** - Display repository name and branch in the status bar

## Tech Stack

| Component | Technology |
|---|---|
| Framework | .NET 8.0 / C# |
| UI | Avalonia 11.3 + Fluent Theme |
| Terminal | Custom VT100/ANSI parser with PseudoConsole (ConPTY) |
| Serialization | System.Text.Json |

## Project Structure

```
ClaudeCodeMDI/
├── MainWindow.axaml / .cs        # Main MDI window and UI logic
├── App.axaml / .cs               # Application root and theme management
├── Terminal/
│   ├── TerminalControl.cs        # Custom terminal rendering control
│   ├── TerminalBuffer.cs         # Cell grid and scrollback buffer
│   ├── TerminalCell.cs           # Cell data model (character, colors, attributes)
│   ├── VtParser.cs               # ANSI/VT escape sequence parser
│   └── PseudoConsole.cs          # Windows PTY interface
├── Services/
│   ├── Localization.cs           # EN/JP string localization
│   ├── AppSettings.cs            # Configuration persistence
│   ├── SessionService.cs         # Claude session management
│   ├── SnippetStore.cs           # Snippet storage
│   └── UsageTracker.cs           # API usage monitoring
├── SessionListWindow.axaml / .cs # Session selection dialog
├── SettingsWindow.axaml / .cs    # Settings dialog
├── UsageChartWindow.axaml / .cs  # Usage chart dialog
└── FileTreeNode.cs               # File explorer tree node model
```

## Requirements

- Windows 10 or later
- [Claude Code CLI](https://docs.anthropic.com/en/docs/claude-code) installed

## Build

```bash
# Build
dotnet build

# Run
dotnet run

# Publish single-file executable
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o ./publish-single
```

## Data Locations

| Data | Path |
|---|---|
| Settings | `%APPDATA%\ClaudeCodeMDI\appsettings.json` |
| Snippets | `%APPDATA%\ClaudeCodeMDI\snippets.json` |
| Sessions (read-only) | `~/.claude/projects/` |
| Usage stats (read-only) | `~/.claude/stats-cache.json` |

## License

MIT
