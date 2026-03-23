# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
dotnet build                # Build (auto-increments build.number)
dotnet run                  # Run debug build (shows console window)

# Publish self-contained single-file executable
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o ./publish-single
```

No test framework is configured. Verify changes by building successfully (`dotnet build`).

## Architecture

Windows MDI terminal app for Claude Code, built with .NET 8.0 / Avalonia 11.3.12. All UI runs on a single STA thread.

### Terminal Pipeline

```
PseudoConsole (ConPTY P/Invoke)
  → OutputReceived event (raw bytes)
    → VtParser.Process() (ANSI/VT state machine, UTF-8 decode)
      → TerminalBuffer (cell grid + scrollback + SGR state)
        → TerminalControl.Render() (Avalonia DrawingContext)
```

- **PseudoConsole**: Windows ConPTY wrapper via P/Invoke. Manages process lifecycle, pipes, resize.
- **VtParser**: State machine (Normal/Escape/Csi/Osc/Dcs). Handles SGR with 256-color and truecolor.
- **TerminalBuffer**: 2D `TerminalCell[,]` grid + `List<TerminalCell[]>` scrollback (10K lines). Tracks wide-char pairs, line-wrap state, alternate buffer, and bracketed paste mode.
- **TerminalControl**: Custom Avalonia `Control`. Handles rendering, text selection, scrollbar, IME input, file drag-and-drop, clipboard image paste.

### MDI Window Management

`MainWindow` manages children via `List<MdiChildInfo>`. Each `MdiChildInfo` record holds the visual container (Border), TerminalControl, strip button (tab), and project folder context. Layout modes: Maximize, Tile, Cascade.

Switching active child triggers project context switching — the toolbar, explorer, session list, and git status all update to reflect that child's project folder.

### Services

- **AppSettings** / **SnippetStore**: JSON persistence to `%APPDATA%\Claucraft/`
- **SessionService**: Reads Claude Code JSONL session files from `~/.claude/projects/` (read-only)
- **UsageTracker**: Reads `~/.claude/stats-cache.json` on a 30-second polling interval (read-only)
- **Localization**: Static dictionary-based EN/JP localization via `Loc.Get("key")`

### Key Conventions

- File-scoped namespaces throughout
- Nullable reference types enabled
- Unsafe blocks allowed (for P/Invoke in PseudoConsole)
- Version scheme: `0.x.y.{auto-increment}` from `build.number` file
- Debug builds output to console (`Exe`), Release builds are windowless (`WinExe`)
- All localized strings go in `Services/Localization.cs` with both EN and JP entries
