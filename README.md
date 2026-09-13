# Claucraft

A Windows MDI (Multiple Document Interface) terminal application for AI coding CLIs, built with Avalonia UI.

[Claude Code](https://docs.anthropic.com/en/docs/claude-code) is the default, and Codex CLI, GitHub Copilot CLI, Antigravity CLI and Grok CLI can be picked per session. Run several sessions side by side in one window - or dragged out into windows of their own - with a project explorer, a git/GitHub panel, snippets, cost and usage readouts, and a dark/light theme.

![MDI Windows](https://github.com/user-attachments/assets/ed655337-ba75-4975-95da-0d31454db6bf)

![ReeView](https://github.com/user-attachments/assets/3bd58b27-8314-4d3b-9503-a9d3b6b37845)

![Snippets](https://github.com/user-attachments/assets/c4e2b938-7648-42b8-8a89-10873857d682)

## Features

### Windows and layout

- **Welcome Page** - VS Code-style startup page with new project, previous project, and recent projects list. Previous/recent projects automatically resume with `-c`
- **MDI Terminal Windows** - Open multiple sessions in child windows arranged as Tile / Tile Horizontally / Tile Vertically / Full View
- **Docking Layout** - Window positions are a dock tree, not coordinates: panes split horizontally or vertically and the splitters between them are draggable
- **Tab Drag** - Drag a window by its tab to reorder it along the strip, dock it into another pane, move it across to another window, or drop it clear of everything to give it an OS window of its own. The session keeps running through the move
- **Windows Panel** - Side panel listing every open window with a status dot and the conversation summary (preferred over the terminal title). Terminal output preview on hover. Click to switch, × to close
- **Agent View** - Background sessions (`claude agents`) are listed under the window they were dispatched from, with their state, what they are waiting on, and whether a process is still alive. Read straight off `~/.claude/jobs` rather than by running the CLI, so the 700 ms panel refresh stays free
- **Subagent Monitor** - The same panel shows a session's in-flight subagents (label, type, model, depth) while they run
- **Tab Management** - Right-click context menu (Close / Close Others / Close to Right / Duplicate / Export Output). Double-click to rename. Auto-names from the first user input or the session summary
- **Named Workspaces** - Save and restore any number of layouts. Restoring reopens the same transcripts with `-r`, so the conversations come back instead of blank sessions

### Sessions and AI CLIs

- **CLI Provider Support** - Claude Code, Codex CLI, GitHub Copilot CLI, Antigravity CLI and Grok CLI, each with its own executable (`claude`, `codex`, `copilot`, `agy`, `grok`), command-line shape, one-shot invocation, config folder and launch profiles. Switchable per session from the toolbar; the executable and the new/continue/resume argument templates are editable in Settings
- **Host Shell** - Launch the CLI under `cmd.exe` or PowerShell. Argument quoting follows whichever shell is chosen, and a PowerShell that is not installed falls back to `cmd.exe` instead of leaving a dead tab
- **Session Management** - Resume previous sessions with AI-generated titles (from `sessions-index.json`) and timestamps. The picker is searchable, and a session can be thrown away from it
- **Session Index Auto-Creation** - Creates and updates `sessions-index.json` per project so the CLI can fill in AI-generated conversation summaries
- **Running Session Guard** - A session only runs in one place at a time, so launching one another process already holds is blocked before it can die at the prompt
- **Project Context Switching** - Switching MDI windows switches the project folder, explorer, session list and git status with it
- **Launch Profiles** - Light / Standard / Deep from the toolbar, applied on every launch path - new, `-c` continue and `-r` resume alike - so a resumed session cannot silently balloon back to an unbounded context. Light ships its own `--settings` file that trims MCP servers, tool descriptions and auto-memory out of the prompt prefix; Deep switches to `--model opus --effort high`
- **Git Worktree Isolation** - Give a session its own `git worktree` checkout (under a Claucraft-managed folder, on `claucraft/`-prefixed branches) so several windows on one repo can work in parallel without clobbering each other
- **Session Hand-off** - When context runs low (an absolute token threshold, so it still fires on 1M-context models), hand off to a fresh session with a brief extracted from the transcript locally. It costs no tokens, unlike `/compact`, and the brief lands in the new session's input box to be edited before anything is sent
- **Resume Cost & Token Badge** - The session picker shows each session's last known context size as a badge, with a tooltip estimate of what resuming it will cost. Right-click for "Start fresh from brief" to hand off without reopening the old session

### Terminal

- **VT100/ANSI Terminal** - Custom parser over Windows ConPTY: SGR with 256-colour and truecolour, alternate buffer, scroll regions, wide characters, Unicode code points and zero-width combining marks, and a 10,000-line scrollback
- **Caret and Line Editing** - A blinking insert-bar caret, click to move it, Shift+arrows to select, and undo/redo of the CLI's input line
- **Terminal Search (Ctrl+F)** - Full-text search across output and scrollback with match highlighting, navigation, regex mode and a case-sensitive toggle
- **Prompt Navigation (Ctrl+↑/↓)** - Move between user questions with a position counter (Q 2/5). Tracks input positions live and scans buffer separators for past conversations
- **Font Zoom (Ctrl+Scroll)** - Change font size with the wheel; Ctrl+0 resets
- **Expanded Input Panel** - Multi-line input in a drag-resizable panel. Enter for newline, Ctrl+Enter to send, Escape to collapse
- **@-Mention Completion** - Typing `@` completes file paths from a project file index, and files can be picked from the input row
- **Shift+Enter Line Break** - Insert a newline without submitting
- **File Drag & Drop** - Drop files onto the terminal to insert their paths, or drag them in from the Explorer panel
- **Clipboard Image Paste** - Ctrl+V pastes clipboard images as temp file paths
- **Bracketed Paste Mode** - Wraps pasted text in bracket sequences for modern shells
- **Terminal Output Export** - Save output as a text file from the tab context menu

### Project and files

- **Project Explorer** - File tree with syntax-aware icons and colour-coded file types (40+ extensions), Windows shell icons, auto-refresh on file system changes, and file preview on selection
- **In-App Text Editor** - Edit text files straight from the explorer (Ctrl+S to save), preserving the original encoding, line endings and BOM. Binary, oversized or non-UTF8 files stay read-only instead of getting corrupted
- **Open With** - Right-click a file to hand it to the Windows "Open with" picker
- **Safe Delete** - Deleting from the explorer sends to the Windows Recycle Bin rather than removing permanently

### Source control

- **Source Control Panel (Ctrl+Shift+G)** - Unified git + GitHub sidebar: Fetch / Pull (rebase) / Push with a quiet 5-minute background auto-fetch, branch switch/create/delete/merge, staging per file or all, and banners for conflicts or an interrupted rebase/merge with an "Ask the AI" button that drafts a JA/EN prompt naming the affected files. Every operation is non-destructive by design - no force-push, `reset --hard` or `branch -D`
- **AI Commit Messages** - Draft a commit message in English or Japanese from the staged diff, generated locally through the active CLI's one-shot mode without joining the live session
- **Secret Scan** - Before a commit, the staged diff is scanned file by file for credentials and keys, and flagged files are called out rather than quietly committed
- **Risky File Warnings** - Staging policy flags files that were probably not meant to be committed - credential files, build output - and a per-clone ignore list keeps the ones you have decided about out of the way
- **GitHub Pull Requests** - List open PRs, approve, create and open in the browser via `gh`, right inside the panel. Hides itself if `gh` isn't installed/authenticated or the remote isn't GitHub
- **Commit Graph & Diff Viewer** - Full commit history as a lane graph (as an MDI child or its own window, with Fetch / Pull / Push of its own) plus a virtualized diff viewer. Select a diff range or right-click a changed file to send `@path:12-20 <comment>` straight to the terminal

### Knowing what the AI is doing

- **Mode Badge** - Shows the session's mode (manual / auto-accept / plan / bypass) in the status bar, painted the colour the CLI paints it. Click to cycle. If a CLI update renames the modes, it falls back to "Switch Mode" and still sends Shift+Tab
- **Model & Effort** - The active model and reasoning effort are shown in the status bar and switchable from it, and a model switch made inside the CLI is followed
- **Activity Indicator** - Says what the AI is doing while it is quiet - reading a file, running a command, searching - with elapsed time, plus a sweep bar under the input row while a turn is in flight
- **Context Meter** - Reads the context left out of the CLI's own output and shows it as a meter, with one-click `/compact` when it runs low
- **Marginal Cost Readout** - What the last turn cost, and what the next one costs just to re-read the conversation, so a session that has grown expensive says so instead of being discovered on the invoice
- **Usage in Plain Words** - "about 840 left, resets in 5h" instead of a bare message count, measured against the plan (Pro / Max 5x / Max 20x) chosen in Settings. Aggregated from the session transcripts themselves
- **Rate Limit Readout** - Utilization and reset countdown for both the 5-hour and 7-day plan windows
- **Permission Prompts, Explained** - The approval overlay says what the command actually does and rates it read-only / changes files / deletes or reaches the network
- **Error Diagnosis Banner** - Known failures (signed out, rate limited, usage limit, network down, outdated CLI) surface as a banner with the fix one click away, and advisory banners hide themselves again
- **Stop Button** - A stop control on the input row of whichever window is working, for anyone who does not know Escape interrupts the AI
- **Turn-End Frame Blink** - The active window's blue frame blinks twice when the AI hands the turn back, so a finished session is visible at a glance across tiled windows
- **Task Completion Notification** - A tray toast, a sound and a taskbar flash when a session finishes in the background, including when a turn finishes generating. All can be turned off in Settings
- **Status Bar** - Repository name, branch, changed file count, terminal status, mode badge, model, current activity, context left, rate limits, and daily usage with a colour-gradient progress bar

### Panels, commands and tools

- **Command Palette (Ctrl+Shift+P)** - VS Code-style searchable action menu covering every command
- **Slash Commands Panel (Ctrl+/)** - Every slash command the CLI understands, listed with a description. Custom commands from `.claude/commands/*.md` are picked up automatically
- **Extensions Panel** - Configured MCP servers, skills and plugins, with enable/disable toggles where the CLI supports it. Double-click a skill to run it
- **Chat View** - A side panel rendering a session's full JSONL transcript as auto-scrolling, formatted Markdown - a readable document view instead of raw scrollback
- **Diagram Viewer** - Renders Excalidraw diagrams drawn via MCP tool calls, and mermaid/chart code blocks from the output, in a zoomable, pannable window with PNG export/copy. Cached per project so they survive a resume or restart
- **Snippets Panel** - Store and send code snippets to the active console (`\r` in text sends Enter). Drag-and-drop reordering, and it starts pre-filled with common prompts in English or Japanese rather than empty
- **Tokens & Cost Dashboard** - A window reading `message.usage` out of session transcripts: 7/30/90-day range, a "this project only" scope toggle, a daily cost bar chart, and top-50 breakdowns by model, project and session, with CSV export
- **Usage Chart** - 14-day chart of messages, tool calls and sessions
- **Automatic Checkpoints** - Snapshots the project before each prompt so the work can be rolled back. Git repos use `git stash create`, which builds a commit object without touching the working tree; other folders get a file snapshot
- **Setup Check** - One-click diagnosis of the CLI, Node.js, Git, the config folder and sign-in state, with a copyable fix command for anything missing. Runs itself on first launch and stays quiet when everything is fine
- **Keyboard Shortcut Sheet (F1)** - Every shortcut on one screen, grouped by what it acts on
- **Settings Panel** - Language, font family and size, host shell, initial prompt, theme, notifications, checkpoints, live status, commit message language, plan, AI provider executable and argument templates, and a shortcut to the `.claude` folder
- **Self-Update** - Checks the project's GitHub releases for a newer version, downloads it next to the running executable and verifies it before committing to anything, then steps the old executable aside by renaming it and restarts into the new one - undoing the rename if the swap fails, so there is no state with the application removed but not put back. Only the published single-file build can replace itself; a development build points at the releases page instead

### Everything else

- **Dark / Light Theme** - Toggle in Settings, carried through every panel, the terminal palette and the MDI chrome
- **Localization** - English and Japanese (日本語) throughout

## Keyboard Shortcuts

| | |
|---|---|
| **Windows and tabs** | |
| Ctrl+N | New session |
| Ctrl+W | Close |
| Ctrl+Tab / Ctrl+Shift+Tab | Next / previous tab |
| **Panels** | |
| Ctrl+Shift+E | Explorer |
| Ctrl+Shift+G | Source control |
| Ctrl+Shift+P | Command palette |
| Ctrl+/ | Slash commands |
| F1 | Keyboard shortcuts |
| **Terminal** | |
| Ctrl+F | Search |
| Ctrl+↑ / Ctrl+↓ | Jump between prompts |
| Ctrl+Scroll | Zoom font |
| Ctrl+0 | Reset font size |
| Shift+Enter | New line without sending |
| Ctrl+Enter | Send from the expanded input |
| Esc | Stop the task |
| Ctrl+C | Copy, or interrupt when nothing is selected |
| Ctrl+V | Paste, images included |
| Shift+Tab | Cycle the AI mode |
| Ctrl+S | Save the file open in the editor |

## Tech Stack

| Component | Technology |
|---|---|
| Framework | .NET 8.0 / C# |
| UI | Avalonia 12.1.1 + Fluent Theme + Inter fonts |
| Terminal | Custom VT100/ANSI parser over PseudoConsole (ConPTY) |
| Serialization | System.Text.Json |
| External tools | The AI CLI itself, `git`, and `gh` for pull requests |

## Project Structure

```
Claucraft/
├── Program.cs                      # Application entry point
├── App.axaml / .cs                 # Application root and theme resources
├── MainWindow.axaml / .cs          # The application window - hosts one AppShell
├── AppShell.axaml / .cs            # The shell: toolbar, activity bar, panels, MDI area, status bar
├── AppShell.Update.cs              # Self-update flow (check, download, stage, restart)
├── MdiLayoutItem.cs                # Interface every MDI child implements
├── FileTreeNode.cs                 # Explorer tree node model
├── UsageChartWindow.axaml / .cs    # 14-day usage chart dialog
├── Terminal/
│   ├── TerminalControl.cs          # Terminal control: render, select, caret, IME, search, drag & drop
│   ├── TerminalBuffer.cs           # Cell grid + scrollback, alternate buffer, bracketed paste
│   ├── TerminalCell.cs             # Cell model (Unicode code point, colours, attributes)
│   ├── VtParser.cs                 # ANSI/VT state machine, SGR 256-colour and truecolour
│   ├── PseudoConsole.cs            # Windows ConPTY P/Invoke wrapper
│   ├── CodeBlockDetector.cs        # Finds mermaid/chart blocks and Excalidraw MCP calls
│   └── DiagramWindow.cs            # Zoomable, pannable diagram viewer
├── Controls/
│   ├── ShellWindow.cs              # A torn-off window holding its own shell
│   ├── DetachedWindow.axaml / .cs  # Window host for a dragged-out pane
│   ├── DockTree.cs                 # Split/leaf tree that says where each MDI child sits
│   ├── DockHost.cs                 # Renders the dock tree with draggable splitters
│   ├── TabDrag.cs                  # Tab drag: reorder, dock, move across windows, tear off
│   ├── SourceControlPanel.cs       # Unified git + GitHub sidebar panel
│   ├── CommitGraphPanel.cs         # Commit graph as an MDI child, with fetch/pull/push
│   ├── CommitGraphView.cs          # Lane-graph drawing control
│   ├── DiffWindow.cs               # Virtualized diff viewer with line-range comments
│   ├── CostDashboardWindow.cs      # Tokens & cost dashboard
│   ├── DocumentViewPanel.cs        # Session transcript rendered as Markdown (Chat View)
│   └── MarqueeBar.cs               # Turn-in-progress sweep bar
├── Services/
│   ├── AppSettings.cs              # Configuration persistence
│   ├── Localization.cs             # EN/JP string localization
│   ├── SnippetStore.cs             # Snippet storage
│   ├── WorkspaceService.cs         # Named workspace save/restore
│   ├── CliProvider.cs              # CLI provider / launch-profile data model
│   ├── CliProviderService.cs       # Provider registry, command building, argument quoting
│   ├── CliOneShotRunner.cs         # One throwaway answer from the CLI, with no session state
│   ├── ShellHost.cs                # Which shell (cmd.exe / PowerShell) a session launches inside
│   ├── ProcessRunner.cs            # Shared process-launch plumbing
│   ├── ProcessTree.cs              # Walks child processes to find a window's CLI process
│   ├── SessionService.cs           # Session list and sessions-index management
│   ├── SessionMessageReader.cs     # JSONL transcript parser
│   ├── SessionCostMonitor.cs       # Live per-session cost and context tracking
│   ├── RunningSessionService.cs    # Detects a session already held by another process
│   ├── SubagentMonitor.cs          # In-flight subagent tracking
│   ├── AgentViewMonitor.cs         # Background agent sessions, read from ~/.claude/jobs
│   ├── HandoffBuilder.cs           # Session hand-off brief builder
│   ├── WorktreeService.cs          # Per-session git worktree isolation
│   ├── CheckpointService.cs        # Pre-prompt snapshots (stash object / file copy)
│   ├── UsageTracker.cs             # Today's usage, aggregated from transcripts
│   ├── RateLimitService.cs         # 5h/7d plan rate-limit windows
│   ├── CostAnalytics.cs            # Cost/token aggregation for the dashboard
│   ├── TerminalInsight.cs          # Mode/activity inference and error diagnosis
│   ├── CommandExplainer.cs         # Plain-language permission-prompt explanations
│   ├── SetupDoctor.cs              # Environment diagnostics (Setup Check)
│   ├── SlashCommandCatalog.cs      # Slash command listing
│   ├── ExtensionCatalog.cs         # MCP server / skill / plugin listing
│   ├── ProjectFileIndex.cs         # @-mention file path index
│   ├── TextFileEditor.cs           # Explorer's in-app text file read/write
│   ├── RecycleBin.cs               # Safe delete via the Windows Recycle Bin
│   ├── ShellIconProvider.cs        # Windows Explorer icons, cached, as Avalonia images
│   ├── MarkdownParser.cs           # Lightweight Markdown renderer
│   ├── DiagramCache.cs             # Persists Excalidraw diagrams per project
│   ├── NotificationService.cs      # Tray notifications and sound
│   ├── UpdateService.cs            # GitHub release check, download and staging
│   ├── CommitMessageService.cs     # AI-drafted commit messages (EN/JP)
│   ├── SecretScanService.cs        # Per-file secret scan of the staged diff
│   ├── StagingPolicy.cs            # Flags files that probably should not be committed
│   ├── GitIgnoreService.cs         # Per-clone ignore list for those warnings
│   ├── GitHubCli.cs                # `gh` wrapper for pull requests
│   └── GitCli.cs / GitWriteService.cs / GitChangeService.cs / GitLogService.cs / GitPath.cs / CommitGraphLayout.cs
│                                   # Git process wrapper, write ops, status/log readers, path decoding, graph layout
├── icon.ico / icon.png             # Application icon
├── app.manifest                    # Application manifest
├── publish.bat                     # One-click self-contained release build
└── build.number                    # Auto-incrementing build number
```

## Requirements

- Windows 10 or later
- Node.js - the supported CLIs are npm packages
- At least one AI CLI: [Claude Code](https://docs.anthropic.com/en/docs/claude-code) (default), Codex CLI, GitHub Copilot CLI, Antigravity CLI or Grok CLI
- `git` (optional) - for the source control panel, commit graph and worktree isolation
- `gh` (optional) - for pull requests

## Build

```bash
# Build (auto-increments build.number)
dotnet build

# Run
dotnet run

# Publish single-file executable (or just run publish.bat)
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o ./publish-single
```

## Data Locations

| Data | Path |
|---|---|
| Settings | `%APPDATA%\Claucraft\appsettings.json` |
| Snippets | `%APPDATA%\Claucraft\snippets.json` |
| Workspaces | `%APPDATA%\Claucraft\workspace.json` |
| CLI providers | `%APPDATA%\Claucraft\providers.json` |
| Light profile settings | `%APPDATA%\Claucraft\light-settings.json` |
| Slash command overrides | `%APPDATA%\Claucraft\slashcommands.json` (optional) |
| Checkpoints | `%APPDATA%\Claucraft\checkpoints\` |
| Session worktrees | `%LOCALAPPDATA%\Claucraft\worktrees\` |
| Session index (read/write) | `~/.claude/projects/*/sessions-index.json` |
| Session transcripts (read-only) | `~/.claude/projects/*/*.jsonl` |
| Cached diagrams | `~/.claude/projects/*/diagrams/` |
| Background agents (read-only) | `~/.claude/jobs/` |
| CLI settings and plugins (read-only) | `~/.claude/settings.json`, `~/.claude.json`, `~/.claude/plugins/installed_plugins.json` |
| Project MCP servers and skills (read-only) | `<project>/.mcp.json`, `<project>/.claude/settings.local.json`, `<project>/.claude/commands/*.md` |

## License

MIT

---

# Claucraft (日本語)

Avalonia UI で構築された、AI コーディング CLI 用の Windows MDI（マルチドキュメントインターフェース）ターミナルアプリケーションです。

既定は [Claude Code](https://docs.anthropic.com/en/docs/claude-code) で、Codex CLI・GitHub Copilot CLI・Antigravity CLI・Grok CLI をセッションごとに選択できます。複数のセッションを1つのウィンドウに並べて（あるいは別ウィンドウに切り離して）実行でき、プロジェクトエクスプローラー、git / GitHub パネル、スニペット、コストと使用量の表示、ダーク/ライトテーマを備えています。

## 機能

### ウィンドウとレイアウト

- **ウェルカムページ** - VS Code 風の起動画面。新規プロジェクト、前回のプロジェクト、最近使用したプロジェクト一覧を表示。前回/最近のプロジェクトは `-c` で自動継続
- **MDI ターミナルウィンドウ** - 複数セッションを子ウィンドウで表示。タイル / 横並べ / 縦並べ / 最大表示に対応
- **ドッキングレイアウト** - ウィンドウ配置は座標ではなくドックツリーで管理。ペインを水平・垂直に分割でき、境界はドラッグでリサイズ可能
- **タブドラッグ** - タブを掴んでストリップ内で並べ替え、別ペインへドッキング、別ウィンドウへ移動、どこにも重ならない場所へ落とせば独立した OS ウィンドウとして切り離し。移動中もセッションは動き続ける
- **ウィンドウパネル** - 開いている全ウィンドウを状態ドットと会話要約（ターミナルタイトルより優先）付きで一覧表示。ホバーでターミナル出力プレビュー。クリックで切替、×で閉じる
- **Agent View** - バックグラウンドセッション（`claude agents`）を、起動元のウィンドウの下に状態・待機理由・プロセスの生死とともに表示。CLI を実行せず `~/.claude/jobs` から直接読むため、700ms ごとのパネル更新でもコストがかからない
- **サブエージェントモニター** - 同じパネルに実行中のサブエージェント（ラベル・種類・モデル・深さ）を表示
- **タブ管理** - 右クリックメニュー（閉じる / 他を閉じる / 右側を閉じる / 複製 / エクスポート）。ダブルクリックで名前変更。最初のユーザー入力またはセッション要約から自動命名
- **名前付きワークスペース** - レイアウトを任意の数だけ保存・復元。復元時は `-r` で同じセッションを再開するため、新規セッションではなく会話が戻る

### セッションと AI CLI

- **CLI プロバイダー対応** - Claude Code / Codex CLI / GitHub Copilot CLI / Antigravity CLI / Grok CLI。それぞれ独自の実行ファイル（`claude` / `codex` / `copilot` / `agy` / `grok`）・コマンドライン形式・ワンショット実行・設定フォルダ・起動プロファイルを持つ。ツールバーからセッションごとに切替でき、実行ファイルと新規/継続/再開の引数テンプレートは設定パネルで編集可能
- **ホストシェル** - CLI を `cmd.exe` と PowerShell のどちらで起動するかを選択。引数のクォートは選んだシェルに追従し、PowerShell が未インストールの場合はタブが死なないよう `cmd.exe` にフォールバック
- **セッション管理** - 過去のセッションを AI 生成タイトル（`sessions-index.json` より）とタイムスタンプ付きで再開。一覧は検索可能で、不要なセッションは削除できる
- **セッションインデックス自動作成** - プロジェクトごとに `sessions-index.json` を自動作成・更新し、CLI が AI 生成の会話要約を追加できるようにする
- **多重起動ガード** - 1つのセッションは同時に1か所でしか動かないため、他プロセスが保持中のセッションの起動をプロンプト表示前にブロック
- **プロジェクトコンテキスト切替** - MDI ウィンドウの切替に合わせて、プロジェクトフォルダ・エクスプローラー・セッション一覧・git 状態を自動切替
- **起動プロファイル** - ツールバーで Light / Standard / Deep を選択。新規・`-c` 継続・`-r` 再開のすべての起動経路に適用されるため、再開時にコンテキスト上限が外れて膨らむことがない。Light は独自の `--settings` ファイルで MCP サーバー・ツール説明・自動メモリをプロンプト冒頭から削減、Deep は `--model opus --effort high` に切替
- **Git Worktree 分離** - セッションごとに専用の `git worktree` チェックアウト（Claucraft 管理フォルダ配下、`claucraft/` 接頭辞のブランチ）を持たせ、同一リポジトリの複数ウィンドウが互いのファイルを壊さず並行作業可能に
- **セッション引き継ぎ** - コンテキストが少なくなったら（割合ではなく絶対トークン数が閾値なので 1M コンテキストのモデルでも発火する）、記録からローカルで抽出したブリーフを持って新規セッションへ引き継ぐ。`/compact` と違いトークン費用はかからず、ブリーフは新規セッションの入力欄に置かれるので送信前に編集できる
- **再開コスト＆トークンバッジ** - セッション選択画面に直近のコンテキストサイズをバッジ表示し、ツールチップで再開コストを見積もり。右クリックの「ブリーフから新規開始」で、元のセッションを開かずに引き継げる

### ターミナル

- **VT100/ANSI ターミナル** - Windows ConPTY 上の自前パーサー。SGR の 256 色・トゥルーカラー、代替バッファ、スクロール領域、全角文字、Unicode コードポイントとゼロ幅結合文字に対応。スクロールバックは 10,000 行
- **キャレットと行編集** - 点滅する挿入バー型キャレット、クリックでの移動、Shift+矢印での選択、CLI 入力行の Undo/Redo
- **ターミナル検索 (Ctrl+F)** - 出力とスクロールバック履歴の全文検索。マッチハイライト、ナビゲーション、正規表現モード、大文字小文字区別トグル
- **プロンプトナビゲーション (Ctrl+↑/↓)** - 質問単位で移動し、現在位置を表示（Q 2/5）。セッション中の入力位置を追跡し、過去の会話はバッファ内のセパレータを検出して移動
- **フォントズーム (Ctrl+スクロール)** - ホイールでフォントサイズを変更。Ctrl+0 でリセット
- **拡張入力パネル** - ドラッグでサイズ変更できる複数行入力。Enter で改行、Ctrl+Enter で送信、Escape で縮小
- **@メンション補完** - `@` を入力するとプロジェクトのファイルインデックスからパスを補完。入力行からファイルを選ぶこともできる
- **Shift+Enter 改行** - 送信せずに改行を挿入
- **ファイルドラッグ＆ドロップ** - ターミナルにファイルをドロップしてパスを入力。エクスプローラーパネルからのドラッグにも対応
- **クリップボード画像貼り付け** - Ctrl+V でクリップボード内の画像を一時ファイルのパスとして貼り付け
- **ブラケットペーストモード** - モダンシェル向けにペーストテキストをブラケットシーケンスでラップ
- **ターミナル出力のエクスポート** - タブの右クリックメニューから出力をテキストファイルに保存

### プロジェクトとファイル

- **プロジェクトエクスプローラー** - 構文対応アイコンと色分け（40種類以上の拡張子）、Windows のシェルアイコン、ファイルシステム変更時の自動リフレッシュ、選択時のプレビュー表示
- **アプリ内テキストエディタ** - エクスプローラーからテキストファイルを直接編集（Ctrl+S で保存）。元のエンコーディング・改行コード・BOM を保持。バイナリ・大きすぎる・非 UTF-8 のファイルは壊さないよう読み取り専用のまま
- **プログラムから開く** - ファイルを右クリックして Windows の「プログラムから開く」に渡す
- **安全な削除** - エクスプローラーからの削除は完全削除ではなく Windows のごみ箱へ

### ソースコントロール

- **ソースコントロールパネル (Ctrl+Shift+G)** - git + GitHub 統合サイドパネル。取得 / 取込（rebase）/ 送信と5分間隔の静かなバックグラウンド自動取得、ブランチの切替・作成・削除・マージ、ファイル単位または一括のステージ、コンフリクトや中断したリベース/マージのバナー表示（対象ファイル名入りの日英プロンプトを作成する「AI に聞く」ボタン付き）。強制プッシュ・`reset --hard`・`branch -D` は設計上存在せず、常に非破壊的
- **AI コミットメッセージ** - ステージ済み差分から日本語または英語のコミットメッセージを生成。現在のセッションに参加せず、アクティブな CLI のワンショットモードでローカルに生成
- **シークレットスキャン** - コミット前にステージ済み差分をファイル単位で走査し、認証情報や鍵を検出。該当ファイルを黙ってコミットせず明示的に警告
- **危険ファイルの警告** - コミットするつもりがなかったと思われるファイル（認証情報ファイル、ビルド生成物など）をステージングポリシーが検出。判断済みのものはクローンごとの無視リストで以後表示されなくなる
- **GitHub プルリクエスト** - `gh` を介して、パネル内でオープンな PR の一覧表示・承認・作成・ブラウザで開くが可能。`gh` が未インストール/未認証、またはリモートが GitHub でない場合は自動的に非表示
- **コミットグラフ＆差分ビューア** - コミット履歴をレーングラフで表示（MDI 子ウィンドウ or 専用ウィンドウ。取得/取込/送信ボタンを内蔵）し、仮想化された差分ビューアを提供。差分範囲を選択、または変更ファイルを右クリックして `@path:12-20 <コメント>` をターミナルへ直接送信

### AI の状態を知る

- **モードバッジ** - セッションのモード（手動 / 自動承認 / プラン / 権限スキップ）を、CLI が使う色そのままでステータスバーに表示。クリックで切替。CLI の仕様変更でモード名を読めなくなった場合は「Switch Mode」表示に戻り、Shift+Tab の送信は継続
- **モデルと effort** - 使用中のモデルと推論 effort をステータスバーに表示し、そこから切替可能。CLI 内部で行われたモデル切替にも追従
- **実行中インジケータ** - AI が黙っている間に何をしているか（ファイル読み込み・コマンド実行・検索）を経過時間付きで表示。ターン実行中は入力行の下にスイープバーを表示
- **コンテキスト残量メーター** - CLI の出力から残量を読み取ってメーター表示。少なくなったらワンクリックで `/compact`
- **限界コスト表示** - 直前のターンにかかった額と、次のターンが会話を読み直すだけでかかる額を表示。高くなったセッションが自分から知らせる
- **使用量の人間語表示** - 単なるメッセージ数ではなく「残り約 840 回・リセットまで 5 時間」と表示。基準は設定で選んだプラン（Pro / Max 5x / Max 20x）で、集計元はセッション記録そのもの
- **レート制限表示** - プランの5時間枠・7日枠それぞれの使用率とリセットまでの時間を表示
- **権限プロンプトの解説** - 承認オーバーレイに、そのコマンドが何をするかの平易な説明と危険度（読み取りのみ / ファイルを変更 / 削除・ネットワーク）を表示
- **エラー診断バナー** - 既知の失敗（サインアウト・レート制限・使用量上限・ネットワーク断・CLI が古い）を検出し、対処をワンクリックで実行できるバナーを表示。お知らせ系のバナーは自動で消える
- **停止ボタン** - 作業中のウィンドウの入力行に停止ボタンを表示。Escape で中断できることを知らなくても止められる
- **ターン終了時の枠点滅** - AI が応答を終えて入力待ちに戻ると、アクティブウィンドウの青い枠が2回点滅。タイル表示でもどのセッションが終わったか一目で分かる
- **タスク完了通知** - バックグラウンドでセッションが終了したとき、およびターンの生成が完了したときに、トースト通知・効果音・タスクバー点滅で知らせる。いずれも設定でオフにできる
- **ステータスバー** - リポジトリ名、ブランチ、変更ファイル数、ターミナル状態、モードバッジ、モデル、実行中の作業、コンテキスト残量、レート制限、日次使用量（緑→黄→赤のプログレスバー）を表示

### パネル・コマンド・ツール

- **コマンドパレット (Ctrl+Shift+P)** - VS Code 風の検索可能なアクションメニュー。全コマンドを網羅
- **スラッシュコマンドパネル (Ctrl+/)** - CLI が解釈するスラッシュコマンドを説明付きで一覧表示。`.claude/commands/*.md` のカスタムコマンドも自動取り込み
- **拡張機能パネル** - 設定済みの MCP サーバー・スキル・プラグインを一覧表示。CLI が対応していれば有効/無効を切替可能。スキルはダブルクリックで実行
- **チャットビュー** - セッションの JSONL 記録全体を、自動スクロールする整形済み Markdown としてサイドパネルに表示。生のスクロールバックの代わりに読みやすい会話ビューを提供
- **ダイアグラムビューア** - MCP ツール呼び出しで描画された Excalidraw 図と、出力中の mermaid / chart コードブロックを、ズーム・パン可能なウィンドウで表示。PNG エクスポート/コピーに対応し、プロジェクトごとにキャッシュされるため再開・再起動後も残る
- **スニペットパネル** - コードスニペットを保存してアクティブなコンソールへ送信（テキスト中の `\r` で Enter を送信）。ドラッグ＆ドロップで並べ替え可能。初期状態でよく使うプロンプト（日本語／英語）が入っている
- **トークン／コストダッシュボード** - セッション記録の `message.usage` を集計する専用ウィンドウ。7/30/90日の期間切替、「このプロジェクトのみ」表示、日別コストの棒グラフ、モデル別・プロジェクト別・セッション別の上位50件内訳、CSV エクスポート
- **使用量チャート** - 過去14日のメッセージ数・ツール呼び出し数・セッション数をグラフ表示
- **自動チェックポイント** - プロンプト送信前にプロジェクトのスナップショットを取り、巻き戻し可能にする。Git リポジトリでは作業ツリーに触れない `git stash create` を使い、Git 管理外のフォルダはファイルコピーで保存
- **セットアップ診断** - CLI・Node.js・Git・設定フォルダ・サインイン状態をワンクリックで診断し、足りないものにはコピーできる対処コマンドを表示。初回起動時に自動実行され、問題がなければ何も出さない
- **ショートカット一覧 (F1)** - 全ショートカットを対象ごとにグループ分けして1画面で表示
- **設定パネル** - 言語、フォントとサイズ、ホストシェル、初期プロンプト、テーマ、通知、チェックポイント、ライブステータス、コミットメッセージ言語、プラン、AI プロバイダーの実行ファイルと引数テンプレート、`.claude` フォルダへのショートカット
- **自動アップデート** - GitHub リリースを確認し、新しいバージョンがあれば実行中の exe の隣にダウンロードして検証したうえで適用。古い exe はリネームで退避してから新しい exe を配置し、失敗した場合はリネームを戻すため「アプリが消えたまま」の状態にはならない。自己置換できるのは発行済みの単一ファイルビルドのみで、開発ビルドはリリースページを案内する

### その他

- **ダーク/ライトテーマ** - 設定パネルから切替。全パネル・ターミナルのカラーパレット・MDI の枠まで対応
- **多言語対応** - 英語・日本語

## キーボードショートカット

| | |
|---|---|
| **ウィンドウとタブ** | |
| Ctrl+N | 新規セッション |
| Ctrl+W | 閉じる |
| Ctrl+Tab / Ctrl+Shift+Tab | 次 / 前のタブ |
| **パネル** | |
| Ctrl+Shift+E | エクスプローラー |
| Ctrl+Shift+G | ソースコントロール |
| Ctrl+Shift+P | コマンドパレット |
| Ctrl+/ | スラッシュコマンド |
| F1 | ショートカット一覧 |
| **ターミナル** | |
| Ctrl+F | 検索 |
| Ctrl+↑ / Ctrl+↓ | プロンプト間を移動 |
| Ctrl+スクロール | フォントズーム |
| Ctrl+0 | フォントサイズをリセット |
| Shift+Enter | 送信せずに改行 |
| Ctrl+Enter | 拡張入力から送信 |
| Esc | タスクを停止 |
| Ctrl+C | コピー（選択がなければ中断） |
| Ctrl+V | 貼り付け（画像も可） |
| Shift+Tab | AI モードを切替 |
| Ctrl+S | エディタで開いているファイルを保存 |

## 技術スタック

| コンポーネント | 技術 |
|---|---|
| フレームワーク | .NET 8.0 / C# |
| UI | Avalonia 12.1.1 + Fluent テーマ + Inter フォント |
| ターミナル | PseudoConsole (ConPTY) 上の自前 VT100/ANSI パーサー |
| シリアライズ | System.Text.Json |
| 外部ツール | AI CLI 本体、`git`、プルリクエスト用の `gh` |

## プロジェクト構成

```
Claucraft/
├── Program.cs                      # アプリケーションのエントリポイント
├── App.axaml / .cs                 # アプリケーションルートとテーマリソース
├── MainWindow.axaml / .cs          # アプリ本体のウィンドウ。中身は AppShell ひとつ
├── AppShell.axaml / .cs            # シェル本体：ツールバー、アクティビティバー、パネル、MDI 領域、ステータスバー
├── AppShell.Update.cs              # 自動アップデートの一連の流れ（確認・DL・配置・再起動）
├── MdiLayoutItem.cs                # MDI 子ウィンドウが実装するインターフェース
├── FileTreeNode.cs                 # エクスプローラーのツリーノードモデル
├── UsageChartWindow.axaml / .cs    # 14日間の使用量チャートダイアログ
├── Terminal/
│   ├── TerminalControl.cs          # ターミナルコントロール：描画・選択・キャレット・IME・検索・D&D
│   ├── TerminalBuffer.cs           # セルグリッドとスクロールバック、代替バッファ、ブラケットペースト
│   ├── TerminalCell.cs             # セルモデル（Unicode コードポイント、色、属性）
│   ├── VtParser.cs                 # ANSI/VT 状態機械。SGR の 256 色・トゥルーカラー対応
│   ├── PseudoConsole.cs            # Windows ConPTY の P/Invoke ラッパー
│   ├── CodeBlockDetector.cs        # mermaid/chart ブロックと Excalidraw MCP 呼び出しの検出
│   └── DiagramWindow.cs            # ズーム・パン可能なダイアグラムビューア
├── Controls/
│   ├── ShellWindow.cs              # 切り離された、独自のシェルを持つウィンドウ
│   ├── DetachedWindow.axaml / .cs  # ドラッグで切り離されたペインのウィンドウホスト
│   ├── DockTree.cs                 # 各 MDI 子ウィンドウの位置を表す分割/葉ノードのツリー
│   ├── DockHost.cs                 # ドックツリーをドラッグ可能なスプリッタ付きで描画
│   ├── TabDrag.cs                  # タブドラッグ：並べ替え・ドッキング・ウィンドウ間移動・切り離し
│   ├── SourceControlPanel.cs       # git + GitHub 統合サイドパネル
│   ├── CommitGraphPanel.cs         # MDI 子ウィンドウとしてのコミットグラフ（取得/取込/送信付き）
│   ├── CommitGraphView.cs          # レーングラフの描画コントロール
│   ├── DiffWindow.cs               # 仮想化された差分ビューア（行範囲コメント対応）
│   ├── CostDashboardWindow.cs      # トークン／コストダッシュボード
│   ├── DocumentViewPanel.cs        # セッション記録を Markdown で表示（チャットビュー）
│   └── MarqueeBar.cs               # ターン実行中のスイープバー
├── Services/
│   ├── AppSettings.cs              # 設定の永続化
│   ├── Localization.cs             # EN/JP 文字列のローカライズ
│   ├── SnippetStore.cs             # スニペットの保存
│   ├── WorkspaceService.cs         # 名前付きワークスペースの保存・復元
│   ├── CliProvider.cs              # CLI プロバイダー／起動プロファイルのデータモデル
│   ├── CliProviderService.cs       # プロバイダー登録、コマンド構築、引数クォート
│   ├── CliOneShotRunner.cs         # セッション状態を持たない CLI 単発実行
│   ├── ShellHost.cs                # セッションを起動するシェル（cmd.exe / PowerShell）の決定
│   ├── ProcessRunner.cs            # プロセス起動の共通処理
│   ├── ProcessTree.cs              # 子プロセスを辿ってウィンドウの CLI プロセスを特定
│   ├── SessionService.cs           # セッション一覧と sessions-index の管理
│   ├── SessionMessageReader.cs     # JSONL 記録のパーサー
│   ├── SessionCostMonitor.cs       # セッションごとのコスト・コンテキストのライブ追跡
│   ├── RunningSessionService.cs    # 他プロセスが保持中のセッションの検出
│   ├── SubagentMonitor.cs          # 実行中サブエージェントの追跡
│   ├── AgentViewMonitor.cs         # ~/.claude/jobs から読むバックグラウンドセッション
│   ├── HandoffBuilder.cs           # セッション引き継ぎブリーフの生成
│   ├── WorktreeService.cs          # セッションごとの git worktree 分離
│   ├── CheckpointService.cs        # プロンプト前スナップショット（stash オブジェクト／ファイルコピー）
│   ├── UsageTracker.cs             # セッション記録から集計する当日の使用量
│   ├── RateLimitService.cs         # プランの5時間枠・7日枠
│   ├── CostAnalytics.cs            # ダッシュボード用のコスト／トークン集計
│   ├── TerminalInsight.cs          # モード・作業内容の推定とエラー診断
│   ├── CommandExplainer.cs         # 権限プロンプトの平易な説明
│   ├── SetupDoctor.cs              # 環境診断（セットアップ診断）
│   ├── SlashCommandCatalog.cs      # スラッシュコマンドの一覧取得
│   ├── ExtensionCatalog.cs         # MCP サーバー／スキル／プラグインの一覧取得
│   ├── ProjectFileIndex.cs         # @メンション用のファイルパスインデックス
│   ├── TextFileEditor.cs           # エクスプローラーのアプリ内テキスト読み書き
│   ├── RecycleBin.cs               # Windows ごみ箱経由の安全な削除
│   ├── ShellIconProvider.cs        # Windows のシェルアイコンをキャッシュして Avalonia 画像に
│   ├── MarkdownParser.cs           # 軽量 Markdown レンダラー
│   ├── DiagramCache.cs             # Excalidraw 図のプロジェクト単位の永続化
│   ├── NotificationService.cs      # トレイ通知と効果音
│   ├── UpdateService.cs            # GitHub リリースの確認・ダウンロード・配置
│   ├── CommitMessageService.cs     # AI によるコミットメッセージ生成（EN/JP）
│   ├── SecretScanService.cs        # ステージ済み差分のファイル単位シークレットスキャン
│   ├── StagingPolicy.cs            # コミットすべきでない可能性が高いファイルの検出
│   ├── GitIgnoreService.cs         # 上記警告のクローン単位の無視リスト
│   ├── GitHubCli.cs                # プルリクエスト用の `gh` ラッパー
│   └── GitCli.cs / GitWriteService.cs / GitChangeService.cs / GitLogService.cs / GitPath.cs / CommitGraphLayout.cs
│                                   # git プロセスラッパー、書き込み操作、status/log 読み取り、パス復号、グラフレイアウト
├── icon.ico / icon.png             # アプリケーションアイコン
├── app.manifest                    # アプリケーションマニフェスト
├── publish.bat                     # 自己完結型リリースビルドのワンクリック発行
└── build.number                    # 自動インクリメントされるビルド番号
```

## 動作要件

- Windows 10 以降
- Node.js - 対応 CLI はいずれも npm パッケージ
- AI CLI をいずれか1つ以上: [Claude Code](https://docs.anthropic.com/en/docs/claude-code)（既定）、Codex CLI、GitHub Copilot CLI、Antigravity CLI、Grok CLI
- `git`（任意）- ソースコントロールパネル、コミットグラフ、worktree 分離に必要
- `gh`（任意）- プルリクエスト機能に必要

## ビルド

```bash
# ビルド（build.number を自動インクリメント）
dotnet build

# 実行
dotnet run

# 単一ファイル実行可能ファイルを発行（publish.bat でも可）
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o ./publish-single
```

## データ保存先

| データ | パス |
|---|---|
| 設定 | `%APPDATA%\Claucraft\appsettings.json` |
| スニペット | `%APPDATA%\Claucraft\snippets.json` |
| ワークスペース | `%APPDATA%\Claucraft\workspace.json` |
| CLI プロバイダー | `%APPDATA%\Claucraft\providers.json` |
| Light プロファイル設定 | `%APPDATA%\Claucraft\light-settings.json` |
| スラッシュコマンド上書き | `%APPDATA%\Claucraft\slashcommands.json`（任意） |
| チェックポイント | `%APPDATA%\Claucraft\checkpoints\` |
| セッション用 worktree | `%LOCALAPPDATA%\Claucraft\worktrees\` |
| セッションインデックス（読み書き） | `~/.claude/projects/*/sessions-index.json` |
| セッション記録（読み取り専用） | `~/.claude/projects/*/*.jsonl` |
| ダイアグラムのキャッシュ | `~/.claude/projects/*/diagrams/` |
| バックグラウンドセッション（読み取り専用） | `~/.claude/jobs/` |
| CLI 設定・プラグイン（読み取り専用） | `~/.claude/settings.json`, `~/.claude.json`, `~/.claude/plugins/installed_plugins.json` |
| プロジェクトの MCP・スキル（読み取り専用） | `<project>/.mcp.json`, `<project>/.claude/settings.local.json`, `<project>/.claude/commands/*.md` |

## ライセンス

MIT
