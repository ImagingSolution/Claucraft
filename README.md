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
- **Open With** - Right-click any file in the explorer to hand it to the Windows "Open with" picker, so a file the AI just touched opens in whatever app suits it
- **Snippets Panel** - Store and quickly send code snippets to the active console (`\r` in text sends Enter key). Drag-and-drop reordering supported. Sends to expanded input when active
- **Windows Panel** - Side panel showing all open windows with status dots and conversation summary. Prioritizes session summary over terminal title. Terminal output preview on hover. Click to switch, × to close
- **Prompt Navigation (Ctrl+↑/↓)** - Navigate between user questions in the terminal conversation. Displays a navigation bar with position counter (Q 2/5). Tracks input positions during the session and scans buffer separators for past conversations
- **Terminal Search (Ctrl+F)** - Full-text search across terminal output and scrollback history with match highlighting, navigation, regex mode, and case-sensitive toggle
- **Font Zoom (Ctrl+Scroll)** - Adjust font size in real-time with Ctrl+mouse wheel. Ctrl+0 resets to default size
- **Expanded Input Panel** - Multi-line input mode with drag-resizable panel. Enter for newline, Ctrl+Enter to send. Collapse with Escape or button
- **Command Palette (Ctrl+Shift+P)** - VS Code-style searchable action menu for quick access to all commands
- **Setup Check** - One-click diagnosis of the CLI, Node.js, Git, the config folder and sign-in state, with a copyable fix command for anything missing. Runs itself on first launch and stays quiet when everything is fine
- **Slash Commands Panel (Ctrl+/)** - Every slash command the CLI understands, listed with a description in the side panel next to the activity bar instead of memorised. Custom commands from `.claude/commands/*.md` are picked up automatically
- **Automatic Checkpoints** - Snapshots the project before each prompt so the work can be rolled back from the status bar. Git repos use `git stash create`, which builds a commit object without touching the working tree; other folders get a file snapshot
- **Stop Button** - A stop control appears in the status bar while a session is running, for anyone who does not know Escape interrupts the AI
- **Starter Prompt Templates** - The snippets panel starts pre-filled with common prompts in English or Japanese instead of empty
- **Task Completion Notification** - A tray toast and a sound when a session finishes in the background, on top of the taskbar flash. Both can be turned off in Settings
- **Turn-End Frame Blink** - The active window's blue frame blinks twice when the AI hands the turn back, so a finished session is visible at a glance across tiled windows
- **Keyboard Shortcut Sheet (F1)** - Every shortcut on one screen, grouped by what it acts on
- **Named Workspaces** - Save and restore any number of layouts. Restoring reopens the same transcripts with `-r`, so the conversations come back instead of blank sessions
- **Changed Files + Built-in Diff** - A panel listing every file the AI touched, straight from `git status`. Click one for a colour-coded diff without leaving the app
- **Mode Badge** - Shows which mode the session is in (auto-accept / plan / bypass) right in the status bar, so nobody discovers it by accident. Click it to cycle. If a CLI update renames the modes and the badge cannot read one, it falls back to "Switch Mode" and still sends Shift+Tab
- **Activity Indicator** - Says what the AI is doing while it is quiet - reading a file, running a command, searching - with the elapsed time
- **Context Meter** - Reads the context left from the CLI's own output and shows it as a meter, with a one-click `/compact` when it runs low
- **Usage in Plain Words** - "about 840 left, resets in 5h" instead of a bare message count, measured against the plan (Pro / Max 5x / Max 20x) chosen in Settings
- **Permission Prompts, Explained** - The approval overlay now says what the command actually does and rates it read-only / changes files / deletes or reaches the network
- **Error Diagnosis Banner** - Known failures (signed out, rate limited, usage limit, network down, outdated CLI) surface as a banner with the fix one click away
- **Launch Profiles** - Pick Light / Standard / Deep from the toolbar and new sessions start with matching flags (Light is `--model sonnet --effort low --autocompact 100k --strict-mcp-config --disable-slash-commands`). Bounding context length is what actually lowers the bill
- **Marginal Cost Readout** - The status bar shows what the last turn cost and what the next one costs just to re-read the conversation, so a session that has grown expensive says so instead of being discovered on the invoice
- **Session Hand-off** - When context runs low, hand off to a fresh session with a brief extracted from the transcript locally. It costs no tokens, unlike `/compact`, and the brief lands in the new session's input box so it can be edited before anything is sent
- **Tokens & Cost Dashboard** - Reads `message.usage` out of the session transcripts and totals tokens and estimated cost by day, model, project and session, with CSV export
- **Tab Management** - Right-click context menu (Close / Close Others / Close to Right / Duplicate / Export Output). Double-click to rename. Auto-names from first user input or session summary
- **Terminal Output Export** - Save terminal output as a text file via tab context menu
- **Dark / Light Theme** - Toggle in Settings panel. Full theme support across all UI components
- **Usage Tracking** - Monitor daily Claude API usage with a 14-day chart view. Progress bar in status bar with color gradient (green → yellow → red)
- **Status Bar** - Git repository name, branch, changed files count, terminal status (Running/Exited), mode badge, current activity, context left, and daily usage with progress bar
- **Task Completion Notification** - Taskbar flashes when a terminal exits while the window is in the background
- **Workspace Save / Restore** - Save and restore open tab layout via command palette
- **Keyboard Shortcuts** - Ctrl+N (new session), Ctrl+W (close tab), Ctrl+Tab (next tab), Ctrl+Shift+Tab (previous tab), Ctrl+Shift+E (toggle explorer), Ctrl+Shift+P (command palette), Ctrl+F (search), Ctrl+↑/↓ (prompt navigation), Ctrl+0 (reset font), Ctrl+/ (slash commands), F1 (shortcut sheet)
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
| Checkpoints | `%APPDATA%\Claucraft\checkpoints\` |
| Slash command overrides | `%APPDATA%\Claucraft\slashcommands.json` (optional) |
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
- **プログラムから開く** - エクスプローラーでファイルを右クリックすると Windows の「プログラムから開く」ダイアログを表示。AI が触ったファイルを好きなアプリで開ける
- **スニペットパネル** - コードスニペットを保存し、アクティブなコンソールにワンクリックで送信（テキスト中の `\r` で Enter キーを送信）。ドラッグ＆ドロップによる並べ替えに対応。拡張入力有効時はそちらに送信
- **ウィンドウパネル** - サイドパネルに開いているウィンドウの一覧を表示。状態ドットと会話要約を表示。セッション要約をターミナルタイトルより優先表示。ホバーでターミナル出力プレビュー。クリックで切替、×で閉じる
- **プロンプトナビゲーション (Ctrl+↑/↓)** - ターミナル内の会話を質問単位で移動。ナビゲーションバーに現在位置を表示（Q 2/5）。セッション中の入力位置をトラッキングし、過去の会話はバッファ内のセパレータパターンを検出して移動
- **ターミナル検索 (Ctrl+F)** - ターミナル出力とスクロールバック履歴の全文検索。マッチハイライト、ナビゲーション、正規表現モード、大文字小文字区別トグル
- **フォントズーム (Ctrl+スクロール)** - Ctrl+マウスホイールでリアルタイムにフォントサイズを変更。Ctrl+0 でデフォルトサイズにリセット
- **拡張入力パネル** - 複数行入力モード。ドラッグでサイズ調整可能。Enter で改行、Ctrl+Enter で送信。Escape またはボタンで縮小
- **コマンドパレット (Ctrl+Shift+P)** - VS Code 風の検索可能なアクションメニュー。全コマンドに素早くアクセス
- **セットアップ診断** - CLI・Node.js・Git・設定フォルダ・サインイン状態をワンクリックで診断し、足りないものにはコピーできる対処コマンドを表示。初回起動時に自動実行され、問題がなければ何も出さない
- **スラッシュコマンドパネル（Ctrl+/）** - CLI が解釈するスラッシュコマンドを、アクティビティバー右側のサイドパネルに説明付きで一覧表示。`.claude/commands/*.md` のカスタムコマンドも自動で取り込む
- **自動チェックポイント** - プロンプト送信前にプロジェクトのスナップショットを取り、ステータスバーから巻き戻し可能。Git リポジトリでは作業ツリーに触れない `git stash create` を使い、Git 管理外のフォルダはファイルコピーで保存
- **停止ボタン** - セッション実行中はステータスバーに停止ボタンを表示。Escape で中断できることを知らなくても止められる
- **プロンプトテンプレート同梱** - スニペットパネルが空ではなく、よく使うプロンプト（日本語／英語）が入った状態から始まる
- **タスク完了通知** - バックグラウンドでセッションが終了したとき、タスクバー点滅に加えてトースト通知と効果音で知らせる。どちらも設定でオフにできる
- **ターン終了時の枠点滅** - AI が応答を終えて入力待ちに戻ると、アクティブウィンドウの青い枠が2回点滅。タイル表示でもどのセッションが終わったか一目で分かる
- **ショートカット一覧（F1）** - 全ショートカットを対象ごとにグループ分けして1画面で表示
- **名前付きワークスペース** - レイアウトを任意の数だけ保存・復元。復元時は `-r` で同じセッションを再開するため、新規セッションではなく会話が戻ってくる
- **変更ファイルパネル＋内蔵 diff** - AI が触ったファイルを `git status` から一覧表示。クリックすると色分けされた差分をアプリ内で確認できる
- **モードバッジ** - セッションのモード（自動承認 / プラン / 権限スキップ）をステータスバーに常時表示。クリックで切り替え。CLI の仕様変更でモード名を読み取れなくなった場合は「Switch Mode」表示に戻り、Shift+Tab の送信は継続
- **実行中インジケータ** - AI が黙っている間に何をしているか（ファイル読み込み・コマンド実行・検索）を経過時間付きで表示
- **コンテキスト残量メーター** - CLI の出力から残量を読み取ってメーター表示。少なくなったらワンクリックで `/compact` を実行
- **使用量の人間語表示** - 単なるメッセージ数ではなく「残り約 840 回・リセットまで 5 時間」と表示。設定で選んだプラン（Pro / Max 5x / Max 20x）が基準
- **権限プロンプトの解説** - 承認オーバーレイに、そのコマンドが何をするかの平易な説明と危険度（読み取りのみ / ファイルを変更 / 削除・ネットワーク）を表示
- **エラー診断バナー** - 既知の失敗（サインアウト・レート制限・使用量上限・ネットワーク断・CLI が古い）を検出し、対処をワンクリックで実行できるバナーを表示
- **起動プロファイル** - ツールバーで Light / Standard / Deep を選ぶと、新規セッションが対応するフラグ付きで起動する（Light は `--model sonnet --effort low --autocompact 100k --strict-mcp-config --disable-slash-commands`）。コンテキスト長を抑えることがコスト削減に直結する
- **限界コスト表示** - 直前のターンにかかった額と、次のターンが会話を読み直すだけでかかる額をステータスバーに表示。高くなったセッションが自分から知らせる
- **セッション引き継ぎ** - コンテキストが少なくなったら、記録からローカルで抽出したブリーフを持って新規セッションへ引き継ぐ。`/compact` と違いトークン費用はかからず、ブリーフは新規セッションの入力欄に置かれるので送信前に編集できる
- **トークン／コストダッシュボード** - セッション記録の `message.usage` を集計し、日別・モデル別・プロジェクト別・セッション別にトークンと概算コストを表示。CSV エクスポート対応
- **タブ管理** - 右クリックコンテキストメニュー（閉じる / 他を閉じる / 右側を閉じる / 複製 / エクスポート）。ダブルクリックでタブ名変更。最初のユーザー入力またはセッション要約から自動命名
- **ターミナル出力のエクスポート** - タブコンテキストメニューからターミナル出力をテキストファイルに保存
- **ダーク/ライトテーマ** - 設定パネルから切替。全UIコンポーネントのテーマに完全対応
- **使用量トラッキング** - Claude API の日次使用量を14日間のチャートで表示。ステータスバーにプログレスバー表示（緑→黄→赤のグラデーション）
- **ステータスバー** - Git リポジトリ名、ブランチ名、変更ファイル数、ターミナル状態（実行中/終了）、モードバッジ、実行中の作業、コンテキスト残量、使用量プログレスバーを表示
- **タスク完了通知** - バックグラウンドでターミナルが終了したとき、タスクバーが点滅
- **ワークスペース保存・復元** - コマンドパレットから開いているタブのレイアウトを保存・復元
- **キーボードショートカット** - Ctrl+N（新規セッション）、Ctrl+W（タブを閉じる）、Ctrl+Tab（次のタブ）、Ctrl+Shift+Tab（前のタブ）、Ctrl+Shift+E（エクスプローラー切替）、Ctrl+Shift+P（コマンドパレット）、Ctrl+F（検索）、Ctrl+↑/↓（プロンプトナビゲーション）、Ctrl+0（フォントリセット）、Ctrl+/（スラッシュコマンド）、F1（ショートカット一覧）
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
| チェックポイント | `%APPDATA%\Claucraft\checkpoints\` |
| スラッシュコマンド上書き | `%APPDATA%\Claucraft\slashcommands.json`（任意） |
| セッション（読み書き） | `~/.claude/projects/*/sessions-index.json` |
| セッション JSONL（読み取り専用） | `~/.claude/projects/*/*.jsonl` |
| 使用量統計（読み取り専用） | `~/.claude/stats-cache.json` |

## ライセンス

MIT
