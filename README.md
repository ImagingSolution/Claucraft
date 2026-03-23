# Claucraft

A Windows MDI (Multiple Document Interface) terminal application for [Claude Code](https://docs.anthropic.com/en/docs/claude-code) built with Avalonia UI.

Manage multiple Claude Code sessions side-by-side with welcome page, project explorer, snippet management, usage tracking, and dark/light theme support.

![MDI Windows](https://github.com/user-attachments/assets/ed655337-ba75-4975-95da-0d31454db6bf)

![ReeView](https://github.com/user-attachments/assets/3bd58b27-8314-4d3b-9503-a9d3b6b37845)

![Snippets](https://github.com/user-attachments/assets/c4e2b938-7648-42b8-8a89-10873857d682)

## Features

- **Welcome Page** - VS Code-style startup page with new project, previous project, and recent projects list. Previous/recent projects automatically resume with `claude -c`
- **MDI Terminal Windows** - Open multiple Claude Code sessions in resizable, draggable child windows with Tile / Tile Horizontally / Tile Vertically / Cascade / Full view layouts
- **Session Management** - Resume previous Claude Code sessions with AI-generated titles (from `sessions-index.json`) and timestamps. Displays conversation summaries like Claude Desktop's recent items
- **Session Index Auto-Creation** - Automatically creates and updates `sessions-index.json` for each project, enabling Claude Code CLI to populate AI-generated conversation summaries
- **Project Context Switching** - Automatically switch project folder, explorer, and sessions when switching between MDI windows
- **Project Explorer** - Browse project file trees with syntax-aware icons and color-coded file types (40+ file extensions). Auto-refreshes on file system changes. File preview on selection
- **Snippets Panel** - Store and quickly send code snippets to the active console (`\r` in text sends Enter key). Drag-and-drop reordering supported. Sends to expanded input when active
- **Windows Panel** - Side panel showing all open windows with status dots and conversation summary. Prioritizes session summary over terminal title. Terminal output preview on hover. Click to switch, × to close
- **Prompt Navigation (Ctrl+↑/↓)** - Navigate between user questions in the terminal conversation. Displays a navigation bar with position counter (Q 2/5). Tracks input positions during the session and scans buffer separators for past conversations
- **Terminal Search (Ctrl+F)** - Full-text search across terminal output and scrollback history with match highlighting, navigation, regex mode, and case-sensitive toggle
- **Font Zoom (Ctrl+Scroll)** - Adjust font size in real-time with Ctrl+mouse wheel. Ctrl+0 resets to default size
- **Expanded Input Panel** - Multi-line input mode with drag-resizable panel. Enter for newline, Ctrl+Enter to send. Collapse with Escape or button
- **Command Palette (Ctrl+Shift+P)** - VS Code-style searchable action menu for quick access to all commands
- **Tab Management** - Right-click context menu (Close / Close Others / Close to Right / Duplicate / Export Output). Double-click to rename. Auto-names from first user input or session summary
- **Terminal Output Export** - Save terminal output as a text file via tab context menu
- **Dark / Light Theme** - Toggle in Settings panel. Full theme support across all UI components
- **Usage Tracking** - Monitor daily Claude API usage with a 14-day chart view. Progress bar in status bar with color gradient (green → yellow → red)
- **Status Bar** - Git repository name, branch, changed files count, terminal status (Running/Exited), and daily usage with progress bar
- **Task Completion Notification** - Taskbar flashes when a terminal exits while the window is in the background
- **Workspace Save / Restore** - Save and restore open tab layout via command palette
- **Keyboard Shortcuts** - Ctrl+N (new session), Ctrl+W (close tab), Ctrl+Tab (next tab), Ctrl+Shift+Tab (previous tab), Ctrl+Shift+E (toggle explorer), Ctrl+Shift+P (command palette), Ctrl+F (search), Ctrl+↑/↓ (prompt navigation), Ctrl+0 (reset font)
- **Mode Switch** - Switch Claude Code mode (Shift+Tab) from the activity bar
- **Compact** - Send /compact command from the activity bar
- **Settings Panel** - Configure font family, font size, language, initial prompt, and dark/light theme from the side panel
- **Initial Prompt** - Configurable initial prompt for new Claude sessions
- **Open .claude Folder** - Quick access to the `.claude` configuration folder from settings
- **Shift+Enter Line Break** - Insert a newline without submitting, enabling multi-line input
- **File Drag & Drop** - Drop files onto the terminal to insert their paths (same as Claude Code CLI)
- **Clipboard Image Paste** - Ctrl+V pastes clipboard images as temp file paths for Claude Code
- **Bracketed Paste Mode** - Properly wraps pasted text in bracket sequences for modern shells
- **Localization** - English and Japanese (日本語) support
- **Git Integration** - Display repository name, branch, and changed files count in the status bar. Double-click repo name to open in browser

## Tech Stack

| Component | Technology |
|---|---|
| Framework | .NET 8.0 / C# |
| UI | Avalonia 11.3.12 + Fluent Theme |
| Terminal | Custom VT100/ANSI parser with PseudoConsole (ConPTY) |
| Serialization | System.Text.Json |

## Project Structure

```
Claucraft/
├── MainWindow.axaml / .cs          # Main MDI window and UI logic
├── App.axaml / .cs                 # Application root and theme management
├── Program.cs                      # Application entry point
├── Terminal/
│   ├── TerminalControl.cs          # Custom terminal rendering control
│   ├── TerminalBuffer.cs           # Cell grid and scrollback buffer
│   ├── TerminalCell.cs             # Cell data model (character, colors, attributes)
│   ├── VtParser.cs                 # ANSI/VT escape sequence parser
│   └── PseudoConsole.cs            # Windows PTY interface
├── Services/
│   ├── Localization.cs             # EN/JP string localization
│   ├── AppSettings.cs              # Configuration persistence
│   ├── SessionService.cs           # Claude session and sessions-index management
│   ├── SnippetStore.cs             # Snippet storage
│   ├── UsageTracker.cs             # API usage monitoring
│   └── WorkspaceService.cs         # Workspace save/restore
├── UsageChartWindow.axaml / .cs    # Usage chart dialog
├── SettingsWindow.axaml / .cs      # Settings dialog window
├── SessionListWindow.axaml / .cs   # Session list window
├── FileTreeNode.cs                 # File explorer tree node model
├── icon.ico / icon.png             # Application icon
├── app.manifest                    # Application manifest
└── build.number                    # Auto-incrementing build number
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
| Settings | `%APPDATA%\Claucraft\appsettings.json` |
| Snippets | `%APPDATA%\Claucraft\snippets.json` |
| Workspace | `%APPDATA%\Claucraft\workspace.json` |
| Sessions (read/write) | `~/.claude/projects/*/sessions-index.json` |
| Session JSONL (read-only) | `~/.claude/projects/*/*.jsonl` |
| Usage stats (read-only) | `~/.claude/stats-cache.json` |

## License

MIT

---

# Claucraft (日本語)

Avalonia UI で構築された、[Claude Code](https://docs.anthropic.com/en/docs/claude-code) 用の Windows MDI（マルチドキュメントインターフェース）ターミナルアプリケーションです。

ダーク/ライトテーマ対応のインターフェースで、複数の Claude Code セッションを並べて管理できます。ウェルカムページ、プロジェクトエクスプローラー、スニペット管理、使用量トラッキングなどの機能を備えています。

## 機能

- **ウェルカムページ** - VS Code 風の起動画面。新規プロジェクト、前回のプロジェクト、最近使用したプロジェクト一覧を表示。前回/最近のプロジェクトは `claude -c` で自動継続
- **MDI ターミナルウィンドウ** - 複数の Claude Code セッションを、リサイズ・ドラッグ可能な子ウィンドウで表示。タイル / 横並べ / 縦並べ / カスケード / 最大表示に対応
- **セッション管理** - 過去の Claude Code セッションを AI 生成タイトル（`sessions-index.json` より）とタイムスタンプ付きで再開。Claude Desktop の最近の項目と同様の会話要約を表示
- **セッションインデックス自動作成** - プロジェクトごとに `sessions-index.json` を自動作成・更新。Claude Code CLI が AI 生成の会話要約を追加可能に
- **プロジェクトコンテキスト切替** - MDI ウィンドウの切り替え時に、プロジェクトフォルダ・エクスプローラー・セッション一覧を自動切替
- **プロジェクトエクスプローラー** - ファイルツリーを構文対応のアイコンと色分けで表示（40種類以上のファイル拡張子対応）。ファイルシステム変更時に自動リフレッシュ。ファイル選択時にプレビュー表示
- **スニペットパネル** - コードスニペットを保存し、アクティブなコンソールにワンクリックで送信（テキスト中の `\r` で Enter キーを送信）。ドラッグ＆ドロップによる並べ替えに対応。拡張入力有効時はそちらに送信
- **ウィンドウパネル** - サイドパネルに開いているウィンドウの一覧を表示。状態ドットと会話要約を表示。セッション要約をターミナルタイトルより優先表示。ホバーでターミナル出力プレビュー。クリックで切替、×で閉じる
- **プロンプトナビゲーション (Ctrl+↑/↓)** - ターミナル内の会話を質問単位で移動。ナビゲーションバーに現在位置を表示（Q 2/5）。セッション中の入力位置をトラッキングし、過去の会話はバッファ内のセパレータパターンを検出して移動
- **ターミナル検索 (Ctrl+F)** - ターミナル出力とスクロールバック履歴の全文検索。マッチハイライト、ナビゲーション、正規表現モード、大文字小文字区別トグル
- **フォントズーム (Ctrl+スクロール)** - Ctrl+マウスホイールでリアルタイムにフォントサイズを変更。Ctrl+0 でデフォルトサイズにリセット
- **拡張入力パネル** - 複数行入力モード。ドラッグでサイズ調整可能。Enter で改行、Ctrl+Enter で送信。Escape またはボタンで縮小
- **コマンドパレット (Ctrl+Shift+P)** - VS Code 風の検索可能なアクションメニュー。全コマンドに素早くアクセス
- **タブ管理** - 右クリックコンテキストメニュー（閉じる / 他を閉じる / 右側を閉じる / 複製 / エクスポート）。ダブルクリックでタブ名変更。最初のユーザー入力またはセッション要約から自動命名
- **ターミナル出力のエクスポート** - タブコンテキストメニューからターミナル出力をテキストファイルに保存
- **ダーク/ライトテーマ** - 設定パネルから切替。全UIコンポーネントのテーマに完全対応
- **使用量トラッキング** - Claude API の日次使用量を14日間のチャートで表示。ステータスバーにプログレスバー表示（緑→黄→赤のグラデーション）
- **ステータスバー** - Git リポジトリ名、ブランチ名、変更ファイル数、ターミナル状態（実行中/終了）、使用量プログレスバーを表示
- **タスク完了通知** - バックグラウンドでターミナルが終了したとき、タスクバーが点滅
- **ワークスペース保存・復元** - コマンドパレットから開いているタブのレイアウトを保存・復元
- **キーボードショートカット** - Ctrl+N（新規セッション）、Ctrl+W（タブを閉じる）、Ctrl+Tab（次のタブ）、Ctrl+Shift+Tab（前のタブ）、Ctrl+Shift+E（エクスプローラー切替）、Ctrl+Shift+P（コマンドパレット）、Ctrl+F（検索）、Ctrl+↑/↓（プロンプトナビゲーション）、Ctrl+0（フォントリセット）
- **モード切替** - アクティビティバーから Claude Code のモードを切替（Shift+Tab）
- **コンパクト** - アクティビティバーから /compact コマンドを送信
- **設定パネル** - サイドパネルからフォント、フォントサイズ、言語、初期プロンプト、ダーク/ライトテーマを設定
- **初期プロンプト** - 新規 Claude セッション起動時のプロンプトを設定可能
- **.claude フォルダを開く** - 設定から `.claude` 設定フォルダへのクイックアクセス
- **Shift+Enter 改行** - 送信せずに改行を挿入し、複数行の入力が可能
- **ファイルドラッグ＆ドロップ** - ターミナルにファイルをドロップしてパスを入力（Claude Code CLI と同じ動作）
- **クリップボード画像貼り付け** - Ctrl+V でクリップボード内の画像を一時ファイルとして貼り付け
- **ブラケットペーストモード** - モダンシェル向けにペーストテキストをブラケットシーケンスでラップ
- **多言語対応** - 英語・日本語に対応
- **Git 連携** - ステータスバーにリポジトリ名、ブランチ名、変更ファイル数を表示。リポジトリ名ダブルクリックでブラウザで開く

## 技術スタック

| コンポーネント | 技術 |
|---|---|
| フレームワーク | .NET 8.0 / C# |
| UI | Avalonia 11.3.12 + Fluent テーマ |
| ターミナル | カスタム VT100/ANSI パーサー + PseudoConsole (ConPTY) |
| シリアライズ | System.Text.Json |

## 動作要件

- Windows 10 以降
- [Claude Code CLI](https://docs.anthropic.com/en/docs/claude-code) がインストール済みであること

## ビルド

```bash
# ビルド
dotnet build

# 実行
dotnet run

# 単一ファイル実行可能ファイルを発行
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o ./publish-single
```

## データ保存先

| データ | パス |
|---|---|
| 設定 | `%APPDATA%\Claucraft\appsettings.json` |
| スニペット | `%APPDATA%\Claucraft\snippets.json` |
| ワークスペース | `%APPDATA%\Claucraft\workspace.json` |
| セッション（読み書き） | `~/.claude/projects/*/sessions-index.json` |
| セッション JSONL（読み取り専用） | `~/.claude/projects/*/*.jsonl` |
| 使用量統計（読み取り専用） | `~/.claude/stats-cache.json` |

## ライセンス

MIT
