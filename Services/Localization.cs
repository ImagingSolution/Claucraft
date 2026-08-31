using System.Collections.Generic;

namespace Claucraft.Services;

public static class Loc
{
    public static string Language { get; set; } = "English";

    private static readonly Dictionary<string, Dictionary<string, string>> Strings = new()
    {
        // ── Toolbar ──
        ["Project"] = new() { ["English"] = "Project", ["日本語"] = "プロジェクト" },
        ["SelectProjectFolder"] = new() { ["English"] = "Select project folder...", ["日本語"] = "プロジェクトフォルダを選択..." },
        ["OpenInExplorer"] = new() { ["English"] = "Open in Explorer", ["日本語"] = "エクスプローラーで開く" },
        ["NewSession"] = new() { ["English"] = "New Session", ["日本語"] = "新規セッション" },
        ["Session"] = new() { ["English"] = "Session", ["日本語"] = "セッション" },
        ["SelectSession"] = new() { ["English"] = "Select a session to resume...", ["日本語"] = "再開するセッションを選択..." },
        ["Resume"] = new() { ["English"] = "Resume", ["日本語"] = "再開" },

        // ── Status Bar ──
        ["NoProjectFolder"] = new() { ["English"] = "No project folder selected", ["日本語"] = "プロジェクトフォルダ未選択" },
        ["Usage"] = new() { ["English"] = "Usage", ["日本語"] = "使用量" },
        ["Windows"] = new() { ["English"] = "windows", ["日本語"] = "ウィンドウ" },
        ["Msgs"] = new() { ["English"] = "msgs", ["日本語"] = "メッセージ" },
        ["Sessions"] = new() { ["English"] = "sessions", ["日本語"] = "セッション" },

        // ── Activity Bar Tooltips ──
        ["ExplorerTooltip"] = new() { ["English"] = "Explorer (Ctrl+Shift+E)", ["日本語"] = "エクスプローラー (Ctrl+Shift+E)" },
        ["SnippetsTooltip"] = new() { ["English"] = "Snippets", ["日本語"] = "スニペット" },
        ["CompactTooltip"] = new() { ["English"] = "Compact (/compact)", ["日本語"] = "コンパクト (/compact)" },
        ["SettingsTooltip"] = new() { ["English"] = "Settings", ["日本語"] = "設定" },

        // ── Side Panel Titles ──
        ["EXPLORER"] = new() { ["English"] = "EXPLORER", ["日本語"] = "エクスプローラー" },
        ["SETTINGS"] = new() { ["English"] = "SETTINGS", ["日本語"] = "設定" },
        ["SNIPPETS"] = new() { ["English"] = "SNIPPETS", ["日本語"] = "スニペット" },

        // ── Explorer Context Menu ──
        ["Open"] = new() { ["English"] = "Open", ["日本語"] = "開く" },
        ["OpenWith"] = new() { ["English"] = "Open with...", ["日本語"] = "プログラムから開く..." },
        ["ShowInExplorer"] = new() { ["English"] = "Show in Explorer", ["日本語"] = "エクスプローラーで表示" },
        ["CopyPath"] = new() { ["English"] = "Copy Path", ["日本語"] = "パスをコピー" },
        ["CopyFilename"] = new() { ["English"] = "Copy Filename", ["日本語"] = "ファイル名をコピー" },

        // ── Settings Panel ──
        ["ConsoleSettings"] = new() { ["English"] = "Console Settings", ["日本語"] = "コンソール設定" },
        ["FontFamily"] = new() { ["English"] = "Font Family", ["日本語"] = "フォント" },
        ["FontSize"] = new() { ["English"] = "Font Size", ["日本語"] = "フォントサイズ" },
        ["InitialPrompt"] = new() { ["English"] = "Initial Prompt", ["日本語"] = "初期プロンプト" },
        ["LanguageSetting"] = new() { ["English"] = "Language", ["日本語"] = "言語" },
        ["Apply"] = new() { ["English"] = "Apply", ["日本語"] = "適用" },

        // ── Snippets Panel ──
        ["AddSnippet"] = new() { ["English"] = "Add Snippet", ["日本語"] = "スニペット追加" },
        ["SendToConsole"] = new() { ["English"] = "Send to Console", ["日本語"] = "コンソールに送信" },
        ["Delete"] = new() { ["English"] = "Delete", ["日本語"] = "削除" },
        ["EnterSnippetText"] = new() { ["English"] = "Enter snippet text...", ["日本語"] = "スニペットテキストを入力..." },

        // ── Window Strip Tooltips ──
        ["TileWindows"] = new() { ["English"] = "Tile windows", ["日本語"] = "タイル配置" },
        ["CascadeWindows"] = new() { ["English"] = "Cascade windows", ["日本語"] = "カスケード配置" },
        ["TileHorizontally"] = new() { ["English"] = "Tile horizontally", ["日本語"] = "横に並べる" },
        ["TileVertically"] = new() { ["English"] = "Tile vertically", ["日本語"] = "縦に並べる" },
        ["FullView"] = new() { ["English"] = "Full view", ["日本語"] = "最大表示" },

        // ── Window Title ──
        ["AppTitle"] = new() { ["English"] = "Claucraft", ["日本語"] = "Claucraft" },

        // ── Settings - Claude Folder ──
        ["OpenClaudeFolder"] = new() { ["English"] = "Open .claude Folder", ["日本語"] = ".claude フォルダを開く" },

        // ── Usage Chart ──
        ["ClickToShowUsage"] = new() { ["English"] = "Click to show usage chart", ["日本語"] = "クリックして使用状況チャートを表示" },

        // ── Welcome Page ──
        ["WelcomeTitle"] = new() { ["English"] = "Claucraft", ["日本語"] = "Claucraft" },
        ["Start"] = new() { ["English"] = "Start", ["日本語"] = "開始" },
        ["NewProject"] = new() { ["English"] = "New Project", ["日本語"] = "新しいプロジェクト" },
        ["PreviousProject"] = new() { ["English"] = "Previous Project", ["日本語"] = "前回のプロジェクト" },
        ["Recent"] = new() { ["English"] = "Recent", ["日本語"] = "最近" },
        ["ShowWelcomeOnStartup"] = new() { ["English"] = "Show Welcome Page on Startup", ["日本語"] = "起動時にウェルカムページを表示" },
        ["ShowWelcomePage"] = new() { ["English"] = "Show Welcome Page", ["日本語"] = "ウェルカムページを表示" },

        // ── Tab Context Menu ──
        ["Close"] = new() { ["English"] = "Close", ["日本語"] = "閉じる" },
        ["CloseOthers"] = new() { ["English"] = "Close Others", ["日本語"] = "他を閉じる" },
        ["CloseToRight"] = new() { ["English"] = "Close to the Right", ["日本語"] = "右側を閉じる" },
        ["Duplicate"] = new() { ["English"] = "Duplicate", ["日本語"] = "複製" },
        ["ExportOutput"] = new() { ["English"] = "Export Output...", ["日本語"] = "出力をエクスポート..." },

        // ── Notifications ──
        ["TaskComplete"] = new() { ["English"] = "Task completed", ["日本語"] = "タスク完了" },

        // ── Tab Rename ──
        ["RenameTab"] = new() { ["English"] = "Rename Tab", ["日本語"] = "タブ名を変更" },

        // ── Search ──
        ["Regex"] = new() { ["English"] = "Regex", ["日本語"] = "正規表現" },
        ["MatchCase"] = new() { ["English"] = "Match Case", ["日本語"] = "大文字小文字" },

        // ── Command Palette ──
        ["CommandPalette"] = new() { ["English"] = "Command Palette", ["日本語"] = "コマンドパレット" },
        ["TypeToSearch"] = new() { ["English"] = "Type to search commands...", ["日本語"] = "コマンドを検索..." },

        // ── Theme ──
        ["DarkMode"] = new() { ["English"] = "Dark Mode", ["日本語"] = "ダークモード" },

        // ── Workspace ──
        ["SaveWorkspace"] = new() { ["English"] = "Save Workspace", ["日本語"] = "ワークスペースを保存" },
        ["RestoreWorkspace"] = new() { ["English"] = "Restore Workspace", ["日本語"] = "ワークスペースを復元" },

        // ── Session Management ──
        ["DeleteSession"] = new() { ["English"] = "Delete", ["日本語"] = "削除" },
        ["SearchSessions"] = new() { ["English"] = "Search sessions...", ["日本語"] = "セッション検索..." },

        // ── File Preview ──
        ["Preview"] = new() { ["English"] = "Preview", ["日本語"] = "プレビュー" },

        // ── Windows Panel ──
        ["WindowsTooltip"] = new() { ["English"] = "Windows", ["日本語"] = "ウィンドウ" },
        ["WINDOWS"] = new() { ["English"] = "WINDOWS", ["日本語"] = "ウィンドウ" },

        // ── Chart/Diagram Rendering ──
        ["EnableCharts"] = new() { ["English"] = "Render diagrams in the terminal", ["日本語"] = "ターミナル内の図を描画する" },
        ["ChartPreview"] = new() { ["English"] = "Chart Preview", ["日本語"] = "チャートプレビュー" },
        ["SaveImage"] = new() { ["English"] = "Save Image", ["日本語"] = "画像を保存" },
        ["CopyImage"] = new() { ["English"] = "Copy Image", ["日本語"] = "画像をコピー" },
        ["MermaidDiagram"] = new() { ["English"] = "Mermaid Diagram", ["日本語"] = "Mermaid 図" },
        ["ExcalidrawDiagram"] = new() { ["English"] = "Excalidraw Diagram", ["日本語"] = "Excalidraw 図" },
        ["OpenInWindow"] = new() { ["English"] = "Open in Window", ["日本語"] = "ウィンドウで開く" },
        ["SaveAsArtifact"] = new() { ["English"] = "Save as Artifact", ["日本語"] = "アーティファクトとして保存" },
        ["OpenArtifact"] = new() { ["English"] = "Open File", ["日本語"] = "ファイルを開く" },
        ["DiagramTooltip"] = new() { ["English"] = "Diagram Viewer", ["日本語"] = "ダイアグラムビューア" },
        ["ZoomIn"] = new() { ["English"] = "Zoom In", ["日本語"] = "拡大" },
        ["ZoomOut"] = new() { ["English"] = "Zoom Out", ["日本語"] = "縮小" },
        ["ResetZoom"] = new() { ["English"] = "Reset", ["日本語"] = "等倍に戻す" },
        ["DocViewTooltip"] = new() { ["English"] = "Chat View", ["日本語"] = "チャットビュー" },
        ["DOCVIEW"] = new() { ["English"] = "CHAT VIEW", ["日本語"] = "チャットビュー" },
        ["CopyCode"] = new() { ["English"] = "Copy", ["日本語"] = "コピー" },
        ["Thinking"] = new() { ["English"] = "Thinking...", ["日本語"] = "思考中..." },
        ["NoSession"] = new() { ["English"] = "No session loaded", ["日本語"] = "セッションが読み込まれていません" },
        ["PermissionRequired"] = new() { ["English"] = "Permission Required", ["日本語"] = "許可が必要です" },
        ["AllowAction"] = new() { ["English"] = "Yes, allow", ["日本語"] = "はい、許可" },
        ["AlwaysAllow"] = new() { ["English"] = "Always allow", ["日本語"] = "常に許可" },
        ["DenyAction"] = new() { ["English"] = "No, deny", ["日本語"] = "いいえ、拒否" },
        ["ToggleDocView"] = new() { ["English"] = "Toggle Chat View", ["日本語"] = "チャットビュー切替" },

        // ── AI Provider ──
        ["AiProvider"] = new() { ["English"] = "AI Provider", ["日本語"] = "AI プロバイダ" },
        ["ExecutableFile"] = new() { ["English"] = "Executable", ["日本語"] = "実行ファイル" },
        ["NewArgs"] = new() { ["English"] = "New Session Args", ["日本語"] = "新規起動引数" },
        ["ContinueArgs"] = new() { ["English"] = "Continue Args", ["日本語"] = "継続起動引数" },
        ["ResumeArgs"] = new() { ["English"] = "Resume Args", ["日本語"] = "再開引数" },
        ["RestoreDefaults"] = new() { ["English"] = "Restore Defaults", ["日本語"] = "既定に戻す" },
        ["OpenConfigFolder"] = new() { ["English"] = "Open Config Folder", ["日本語"] = "設定フォルダを開く" },
        ["NotInstalled"] = new() { ["English"] = "not found", ["日本語"] = "未検出" },
        ["CannotSwitchWhileRunning"] = new()
        {
            ["English"] = "Cannot switch AI while sessions are running. Close all windows and try again.",
            ["日本語"] = "稼働中のセッションがあるため AI を切り替えられません。すべてのウィンドウを閉じてから再度お試しください。",
        },
        ["CannotSwitchTitle"] = new() { ["English"] = "Cannot Switch AI", ["日本語"] = "AI を切り替えできません" },
        ["ContinueSession"] = new() { ["English"] = "Continue", ["日本語"] = "継続起動" },
        ["ContinueSessionTooltip"] = new() { ["English"] = "Continue the most recent session", ["日本語"] = "直前のセッションを継続" },
        ["SwitchAiTooltip"] = new() { ["English"] = "Switch AI provider", ["日本語"] = "AI プロバイダを切り替え" },
        ["OK"] = new() { ["English"] = "OK", ["日本語"] = "OK" },
        // {0} = provider name
        ["NewSessionTooltipFmt"] = new() { ["English"] = "Open new {0} session", ["日本語"] = "新しい {0} セッションを開く" },
        // {0} = config directory name, e.g. ".claude"
        ["OpenConfigDirFmt"] = new() { ["English"] = "Open {0} Folder", ["日本語"] = "{0} フォルダを開く" },

        // ── Common ──
        ["Cancel"] = new() { ["English"] = "Cancel", ["日本語"] = "キャンセル" },
        ["Save"] = new() { ["English"] = "Save", ["日本語"] = "保存" },
        ["Copied"] = new() { ["English"] = "Copied", ["日本語"] = "コピーしました" },

        // ── Stop button ──
        ["StopTask"] = new() { ["English"] = "Stop", ["日本語"] = "停止" },

        // ── Shortcut cheat sheet ──
        ["Shortcuts"] = new() { ["English"] = "Keyboard Shortcuts", ["日本語"] = "キーボードショートカット" },
        ["ShortcutsTooltip"] = new() { ["English"] = "Keyboard shortcuts (F1)", ["日本語"] = "キーボードショートカット（F1）" },
        ["ShortcutsWindows"] = new() { ["English"] = "Windows and tabs", ["日本語"] = "ウィンドウ・タブ" },
        ["ShortcutsPanels"] = new() { ["English"] = "Panels", ["日本語"] = "パネル" },
        ["ShortcutsTerminal"] = new() { ["English"] = "Terminal", ["日本語"] = "ターミナル" },

        // ── Setup diagnostics ──
        ["SetupDoctor"] = new() { ["English"] = "Setup Check", ["日本語"] = "セットアップ診断" },
        ["SetupDoctorTooltip"] = new() { ["English"] = "Check that everything is installed and signed in", ["日本語"] = "必要なものが揃っているか確認する" },
        ["Checking"] = new() { ["English"] = "Checking...", ["日本語"] = "診断中..." },
        ["RunAgain"] = new() { ["English"] = "Run again", ["日本語"] = "再診断" },
        ["CopyCommand"] = new() { ["English"] = "Copy command", ["日本語"] = "コマンドをコピー" },
        ["DoctorAllOk"] = new() { ["English"] = "Everything looks good.", ["日本語"] = "問題は見つかりませんでした。" },
        ["DoctorHasIssues"] = new() { ["English"] = "Some things need attention.", ["日本語"] = "確認が必要な項目があります。" },
        ["DoctorCliInstalled"] = new() { ["English"] = "AI CLI", ["日本語"] = "AI CLI" },
        ["DoctorNode"] = new() { ["English"] = "Node.js", ["日本語"] = "Node.js" },
        ["DoctorGit"] = new() { ["English"] = "Git", ["日本語"] = "Git" },
        ["DoctorConfigDir"] = new() { ["English"] = "Config folder", ["日本語"] = "設定フォルダ" },
        ["DoctorAuth"] = new() { ["English"] = "Sign-in", ["日本語"] = "サインイン" },
        ["DoctorProjectGit"] = new() { ["English"] = "Project under Git", ["日本語"] = "プロジェクトの Git 管理" },

        // ── Slash command palette ──
        ["SlashCommands"] = new() { ["English"] = "Slash Commands", ["日本語"] = "スラッシュコマンド" },
        ["SlashCommandsTooltip"] = new() { ["English"] = "Slash commands (Ctrl+/)", ["日本語"] = "スラッシュコマンド（Ctrl+/）" },
        ["SearchSlashCommands"] = new() { ["English"] = "Type to search slash commands...", ["日本語"] = "スラッシュコマンドを検索..." },
        ["ProjectCommands"] = new() { ["English"] = "Project commands", ["日本語"] = "プロジェクトのコマンド" },
        ["NeedsArgument"] = new() { ["English"] = "needs an argument", ["日本語"] = "引数が必要" },
        ["SlashNeedsSession"] = new() { ["English"] = "Open a session first, then pick a command to send to it.", ["日本語"] = "先にセッションを開いてから、送るコマンドを選んでください。" },
        ["SlashPanelHint"] = new() { ["English"] = "Enter or double-click to send.", ["日本語"] = "Enter またはダブルクリックで送信します。" },
        ["SLASH"] = new() { ["English"] = "SLASH COMMANDS", ["日本語"] = "スラッシュコマンド" },

        // ── Checkpoints ──
        ["Checkpoints"] = new() { ["English"] = "Checkpoints", ["日本語"] = "チェックポイント" },
        ["Undo"] = new() { ["English"] = "Undo", ["日本語"] = "元に戻す" },
        ["EnableCheckpoints"] = new() { ["English"] = "Snapshot the project before each prompt", ["日本語"] = "プロンプト送信前にスナップショットを取る" },
        ["NoCheckpoints"] = new() { ["English"] = "No checkpoints yet", ["日本語"] = "チェックポイントはまだありません" },
        ["CheckpointRestored"] = new() { ["English"] = "Restored.", ["日本語"] = "復元しました。" },
        // {0} = error detail
        ["CheckpointFailedFmt"] = new() { ["English"] = "Could not restore: {0}", ["日本語"] = "復元できませんでした: {0}" },
        // {0} = checkpoint label
        ["RestoreCheckpointFmt"] = new() { ["English"] = "Roll the project folder back to this checkpoint?", ["日本語"] = "プロジェクトフォルダをこのチェックポイントの状態に戻しますか？" },

        // ── Notifications ──
        ["Notifications"] = new() { ["English"] = "Notifications", ["日本語"] = "通知" },
        ["NotifyOnComplete"] = new() { ["English"] = "Notify when a task finishes", ["日本語"] = "処理の完了を通知する" },
        ["NotifySound"] = new() { ["English"] = "Play a sound", ["日本語"] = "音を鳴らす" },
        // {0} = tab title
        ["TaskCompleteFmt"] = new() { ["English"] = "{0} has finished", ["日本語"] = "{0} が終了しました" },

        // ── Workspaces ──
        ["Workspaces"] = new() { ["English"] = "Workspaces", ["日本語"] = "ワークスペース" },
        ["SaveWorkspaceAs"] = new() { ["English"] = "Save Workspace As...", ["日本語"] = "ワークスペースを名前を付けて保存..." },
        ["WorkspaceName"] = new() { ["English"] = "Workspace name", ["日本語"] = "ワークスペース名" },
        ["NoWorkspaces"] = new() { ["English"] = "No saved workspaces", ["日本語"] = "保存されたワークスペースはありません" },

        // ── Live status (mode / activity / context) ──
        ["LiveStatus"] = new() { ["English"] = "Live status", ["日本語"] = "ライブ状態表示" },
        ["EnableLiveStatus"] = new() { ["English"] = "Show mode, activity and context left", ["日本語"] = "モード・作業内容・コンテキスト残量を表示する" },
        ["EnableErrorBanner"] = new() { ["English"] = "Explain errors in a banner", ["日本語"] = "エラーをバナーで解説する" },
        ["ModeBadgeTooltip"] = new() { ["English"] = "Current mode - click to cycle (Shift+Tab)", ["日本語"] = "現在のモード - クリックで切替 (Shift+Tab)" },
        ["ActivityThinking"] = new() { ["English"] = "Thinking…", ["日本語"] = "考えています…" },
        ["ActivityReading"] = new() { ["English"] = "Reading files…", ["日本語"] = "ファイルを読んでいます…" },
        ["ActivityWriting"] = new() { ["English"] = "Writing a file…", ["日本語"] = "ファイルを書いています…" },
        ["ActivityEditing"] = new() { ["English"] = "Editing a file…", ["日本語"] = "ファイルを編集しています…" },
        ["ActivityRunning"] = new() { ["English"] = "Running a command…", ["日本語"] = "コマンドを実行しています…" },
        ["ActivitySearching"] = new() { ["English"] = "Searching…", ["日本語"] = "検索しています…" },
        ["ActivityBrowsing"] = new() { ["English"] = "Fetching from the web…", ["日本語"] = "Web を参照しています…" },
        ["ActivityWaiting"] = new() { ["English"] = "Waiting for your answer…", ["日本語"] = "あなたの回答を待っています…" },
        ["ContextMeterTooltip"] = new() { ["English"] = "Context used before auto-compact - click to run /compact", ["日本語"] = "自動コンパクトまでのコンテキスト使用量 - クリックで /compact を実行" },
        ["ContextLabel"] = new() { ["English"] = "Context", ["日本語"] = "コンテキスト" },
        ["ContextLowTitle"] = new() { ["English"] = "Context is running low", ["日本語"] = "コンテキストが残り少なくなっています" },
        ["ContextLowDetail"] = new() { ["English"] = "About {0}% left. Running /compact now summarises the conversation and frees room.", ["日本語"] = "残り約 {0}% です。いま /compact を実行すると会話が要約され、余裕ができます。" },
        ["RunCompact"] = new() { ["English"] = "Run /compact", ["日本語"] = "/compact を実行" },
        ["ModelTooltip"] = new() { ["English"] = "Model answering in this session. Switching also changes the default for new sessions.", ["日本語"] = "このセッションで応答しているモデル。切り替えると新規セッションの既定も変わります。" },
        ["ModelOther"] = new() { ["English"] = "Other...", ["日本語"] = "その他..." },
        ["EffortTooltip"] = new() { ["English"] = "Reasoning effort this session runs at. Switching sends /effort to the terminal.", ["日本語"] = "このセッションの推論 effort。切り替えるとターミナルに /effort を送信します。" },
        ["EffortAuto"] = new() { ["English"] = "Auto", ["日本語"] = "自動" },

        // ── Rate limits (the 5-hour and 7-day windows the plan is metered on) ──
        ["RateLimit5hTooltip"] = new() { ["English"] = "5-hour window: {0}% used, resets in {1}", ["日本語"] = "5時間枠: {0}% 使用・{1} 後にリセット" },
        ["RateLimit7dTooltip"] = new() { ["English"] = "7-day window: {0}% used, resets in {1}", ["日本語"] = "7日枠: {0}% 使用・{1} 後にリセット" },
        ["RateLimitUnknownReset"] = new() { ["English"] = "unknown", ["日本語"] = "不明" },

        // ── Launch profiles ──
        ["LaunchProfile"] = new() { ["English"] = "Profile", ["日本語"] = "プロファイル" },
        ["LaunchProfileTooltip"] = new() { ["English"] = "Flags applied to new sessions. Bounding context length is what lowers the bill.", ["日本語"] = "新規セッションに付与するフラグ。コンテキスト長を抑えることがコスト削減に直結します。" },
        ["ProfileLightDesc"] = new() { ["English"] = "Sonnet, low effort, context capped at 100k, MCP and skills off. For lookups and single fixes.", ["日本語"] = "Sonnet・低 effort・コンテキスト上限 100k・MCP とスキルを無効。調査や単発修正向け。" },
        ["ProfileStandardDesc"] = new() { ["English"] = "Context capped at 200k with better cache reuse. Everyday work.", ["日本語"] = "コンテキスト上限 200k、キャッシュ再利用を改善。通常作業向け。" },
        ["ProfileDeepDesc"] = new() { ["English"] = "Opus at high effort, nothing restricted. Design and review.", ["日本語"] = "Opus・高 effort・制限なし。設計やレビュー向け。" },

        // ── Hand-off ──
        ["HandoffAction"] = new() { ["English"] = "Hand off to a new session", ["日本語"] = "引き継いで新規セッション" },
        ["HandoffTitle"] = new() { ["English"] = "This session is getting expensive to continue", ["日本語"] = "このセッションは継続コストが高くなっています" },
        ["HandoffDetailFormat"] = new() { ["English"] = "About {0}% context left. Each further turn costs roughly ${1} just to re-read the conversation. Handing off starts fresh from a brief built locally, at no token cost.", ["日本語"] = "コンテキスト残り約 {0}%。以降は会話を読み直すだけで1ターンあたり約 ${1} かかります。引き継ぎならローカル生成したブリーフでやり直せて、トークン費用はかかりません。" },
        ["HandoffDialogTitle"] = new() { ["English"] = "Hand off to a new session", ["日本語"] = "新規セッションへ引き継ぐ" },
        ["HandoffDialogHint"] = new() { ["English"] = "Extracted from this session's transcript. Edit freely - it is placed in the new session's input box, and nothing is sent until you press Enter.", ["日本語"] = "このセッションの記録から抽出したものです。自由に編集できます。新規セッションの入力欄に差し込まれるだけで、Enter を押すまで送信されません。" },
        ["HandoffStart"] = new() { ["English"] = "Start new session", ["日本語"] = "新規セッションを開始" },
        ["HandoffCancel"] = new() { ["English"] = "Cancel", ["日本語"] = "キャンセル" },
        ["HandoffEmpty"] = new() { ["English"] = "There is nothing to hand off yet - this session has no recorded requests or edits.", ["日本語"] = "引き継ぐ内容がまだありません。このセッションには記録された依頼や編集がありません。" },
        ["HandoffNoSession"] = new() { ["English"] = "No transcript found for the active session yet. Wait for the first reply, then try again.", ["日本語"] = "アクティブなセッションの記録がまだ見つかりません。最初の応答を待ってから再試行してください。" },
        ["Dismiss"] = new() { ["English"] = "Dismiss", ["日本語"] = "閉じる" },

        // ── Error diagnosis ──
        ["DiagAuthExpiredTitle"] = new() { ["English"] = "Signed out", ["日本語"] = "サインアウトされています" },
        ["DiagAuthExpiredDetail"] = new() { ["English"] = "The CLI is no longer signed in. Run /login in the terminal to sign back in.", ["日本語"] = "CLI のサインインが切れています。ターミナルで /login を実行してサインインし直してください。" },
        ["DiagAuthExpiredAction"] = new() { ["English"] = "Run /login", ["日本語"] = "/login を実行" },
        ["DiagRateLimitedTitle"] = new() { ["English"] = "Rate limited", ["日本語"] = "レート制限中です" },
        ["DiagRateLimitedDetail"] = new() { ["English"] = "Too many requests in a short time. Wait a moment and send the prompt again.", ["日本語"] = "短時間にリクエストが集中しました。少し待ってから送信し直してください。" },
        ["DiagUsageLimitTitle"] = new() { ["English"] = "Usage limit reached", ["日本語"] = "使用量の上限に達しました" },
        ["DiagUsageLimitDetail"] = new() { ["English"] = "This plan has no capacity left for now. It becomes available again after the reset.", ["日本語"] = "現在のプランの残量がありません。リセット後に再び利用できます。" },
        ["DiagNetworkDownTitle"] = new() { ["English"] = "Cannot reach the network", ["日本語"] = "ネットワークに接続できません" },
        ["DiagNetworkDownDetail"] = new() { ["English"] = "The CLI could not connect. Check the connection, VPN or proxy, then try again.", ["日本語"] = "接続できませんでした。ネットワーク・VPN・プロキシを確認してから再試行してください。" },
        ["DiagOutdatedCliTitle"] = new() { ["English"] = "A newer CLI is available", ["日本語"] = "新しいバージョンの CLI があります" },
        ["DiagOutdatedCliDetail"] = new() { ["English"] = "Updating usually fixes odd behaviour. The update command is on the clipboard.", ["日本語"] = "更新すると不具合が直ることがあります。更新コマンドをクリップボードにコピーします。" },
        ["DiagOutdatedCliAction"] = new() { ["English"] = "Copy update command", ["日本語"] = "更新コマンドをコピー" },
        ["DiagDiskOrPermissionTitle"] = new() { ["English"] = "Blocked by the file system", ["日本語"] = "ファイルシステムに阻まれました" },
        ["DiagDiskOrPermissionDetail"] = new() { ["English"] = "Access was denied or the disk is full. Check the folder permissions and free space.", ["日本語"] = "アクセスが拒否されたか、ディスクが一杯です。フォルダの権限と空き容量を確認してください。" },
        ["DiagUnknownErrorTitle"] = new() { ["English"] = "The CLI reported an error", ["日本語"] = "CLI がエラーを報告しました" },
        ["DiagUnknownErrorDetail"] = new() { ["English"] = "The last command failed. The message is in the terminal above.", ["日本語"] = "直前のコマンドが失敗しました。詳細は上のターミナルに表示されています。" },

        // ── Permission prompt explanation ──
        ["RiskReadOnly"] = new() { ["English"] = "Reads only", ["日本語"] = "読み取りのみ" },
        ["RiskFileChange"] = new() { ["English"] = "Changes files", ["日本語"] = "ファイルを変更します" },
        ["RiskDangerous"] = new() { ["English"] = "Deletes or reaches the network", ["日本語"] = "削除・ネットワーク通信" },

        // ── Changed files ──
        ["CHANGES"] = new() { ["English"] = "CHANGES", ["日本語"] = "変更ファイル" },
        ["ChangesTooltip"] = new() { ["English"] = "Changed files", ["日本語"] = "変更ファイル" },
        ["Refresh"] = new() { ["English"] = "Refresh", ["日本語"] = "更新" },
        ["NoChanges"] = new() { ["English"] = "No changes", ["日本語"] = "変更はありません" },
        ["NotAGitRepo"] = new() { ["English"] = "Not a git repository", ["日本語"] = "git リポジトリではありません" },
        ["LoadingChanges"] = new() { ["English"] = "Loading…", ["日本語"] = "読み込み中…" },
        ["ChangedFilesCount"] = new() { ["English"] = "{0} changed", ["日本語"] = "{0} 件の変更" },
        ["Diff"] = new() { ["English"] = "Diff", ["日本語"] = "差分" },
        ["DiffEmpty"] = new() { ["English"] = "Nothing to show for this file.", ["日本語"] = "このファイルに表示できる差分はありません。" },
        ["Copy"] = new() { ["English"] = "Copy", ["日本語"] = "コピー" },

        // ── Extensions Panel ──
        ["ExtensionsTooltip"] = new() { ["English"] = "MCP, skills and plugins", ["日本語"] = "MCP・スキル・プラグイン" },
        ["EXTENSIONS"] = new() { ["English"] = "EXTENSIONS", ["日本語"] = "拡張機能" },
        ["ExtensionsSearch"] = new() { ["English"] = "Search extensions...", ["日本語"] = "拡張機能を検索..." },
        ["ExtensionsLoading"] = new() { ["English"] = "Reading configuration...", ["日本語"] = "設定を読み込み中..." },
        ["ExtensionsApplyHint"] = new() { ["English"] = "Changes apply to sessions started from now on.", ["日本語"] = "変更はこれから開始するセッションに反映されます。" },
        ["ExtensionsChangedFmt"] = new() { ["English"] = "Updated {0}. Applies to new sessions; the previous file was kept as .claucraft-backup.", ["日本語"] = "{0} 件を更新しました。新規セッションから反映されます（変更前のファイルは .claucraft-backup に残しています）。" },
        ["ExtensionsWriteFailedFmt"] = new() { ["English"] = "Could not save: {0}", ["日本語"] = "保存できませんでした: {0}" },
        ["ExtensionsMoreFmt"] = new() { ["English"] = "{0} more - search to narrow", ["日本語"] = "他 {0} 件 - 検索で絞り込めます" },
        ["McpServers"] = new() { ["English"] = "MCP SERVERS", ["日本語"] = "MCP サーバー" },
        ["Skills"] = new() { ["English"] = "SKILLS", ["日本語"] = "スキル" },
        ["Plugins"] = new() { ["English"] = "PLUGINS", ["日本語"] = "プラグイン" },
        ["DisableAll"] = new() { ["English"] = "Disable all", ["日本語"] = "すべて無効化" },
        ["DisableAllFmt"] = new() { ["English"] = "Turn off all {0} enabled entries", ["日本語"] = "有効な {0} 件をすべて無効にします" },
        ["DisableAllConfirmFmt"] = new() { ["English"] = "Turn off {0} entries? You can switch them back on here.", ["日本語"] = "{0} 件を無効にしますか？ここで戻せます。" },
        ["McpUserScoped"] = new() { ["English"] = "Registered with 'claude mcp' in ~/.claude.json. Claucraft does not edit that file.", ["日本語"] = "~/.claude.json に 'claude mcp' で登録されています。Claucraft はこのファイルを書き換えません。" },
        ["McpOwnedByFmt"] = new() { ["English"] = "Comes with the {0} plugin - switch the plugin off to remove it.", ["日本語"] = "{0} プラグインに含まれています。外すにはプラグインを無効にしてください。" },
        ["FromMarketplaceFmt"] = new() { ["English"] = "From the {0} marketplace", ["日本語"] = "マーケットプレイス: {0}" },
        ["SkillInvokeFmt"] = new() { ["English"] = "Double-click to run {0}. Claude also picks it up from the description above.", ["日本語"] = "ダブルクリックで {0} を実行します。上の説明に合致すれば Claude が自動で選ぶこともあります。" },
        ["DoubleClickOpens"] = new() { ["English"] = "Double-click to open it.", ["日本語"] = "ダブルクリックで開きます。" },
        ["SubagentDepthFmt"] = new() { ["English"] = "Spawned by another agent (depth {0})", ["日本語"] = "別のエージェントが起動（深さ {0}）" },
        ["NSkillsFmt"] = new() { ["English"] = "{0} skills", ["日本語"] = "スキル {0}" },
        ["NCommandsFmt"] = new() { ["English"] = "{0} commands", ["日本語"] = "コマンド {0}" },
        ["NAgentsFmt"] = new() { ["English"] = "{0} agents", ["日本語"] = "エージェント {0}" },
        ["NMcpFmt"] = new() { ["English"] = "{0} MCP", ["日本語"] = "MCP {0}" },
        ["NoMatches"] = new() { ["English"] = "No matches", ["日本語"] = "一致するものがありません" },
        ["DiffComment"] = new() { ["English"] = "Comment", ["日本語"] = "コメント" },
        ["DiffCommentHint"] = new() { ["English"] = "What should change in the selected lines?", ["日本語"] = "選択した行への指示を入力" },
        ["DiffCommentSelectLines"] = new() { ["English"] = "Select lines in the diff first", ["日本語"] = "先に差分の行を選択してください" },
        ["CommentOnFile"] = new() { ["English"] = "Comment on this file…", ["日本語"] = "このファイルにコメント…" },
        ["CommentOnFileHint"] = new() { ["English"] = "What should change in this file?", ["日本語"] = "このファイルへの指示を入力" },
        ["GitStatusModified"] = new() { ["English"] = "Modified", ["日本語"] = "変更" },
        ["GitStatusAdded"] = new() { ["English"] = "Added", ["日本語"] = "追加" },
        ["GitStatusDeleted"] = new() { ["English"] = "Deleted", ["日本語"] = "削除" },
        ["GitStatusRenamed"] = new() { ["English"] = "Renamed", ["日本語"] = "名前変更" },
        ["GitStatusCopied"] = new() { ["English"] = "Copied", ["日本語"] = "コピー" },
        ["GitStatusConflict"] = new() { ["English"] = "Conflict", ["日本語"] = "競合" },
        ["GitStatusTypeChanged"] = new() { ["English"] = "Type Changed", ["日本語"] = "種別変更" },
        ["GitStatusIgnored"] = new() { ["English"] = "Ignored", ["日本語"] = "無視" },
        ["GitStatusUntracked"] = new() { ["English"] = "Untracked", ["日本語"] = "未追跡" },
        ["GitDiffBinaryFile"] = new() { ["English"] = "Binary file (diff not shown)", ["日本語"] = "バイナリファイル（差分は表示されません）" },
        ["GitDiffFileNotFound"] = new() { ["English"] = "File not found", ["日本語"] = "ファイルが見つかりません" },
        ["GitDiffStagedHeader"] = new() { ["English"] = "--- staged ---", ["日本語"] = "--- ステージ済み ---" },
        ["GitDiffUnstagedHeader"] = new() { ["English"] = "--- unstaged ---", ["日本語"] = "--- 未ステージ ---" },
        ["GitDiffTruncatedFmt"] = new() { ["English"] = "... truncated ({0} more lines) ...", ["日本語"] = "... 省略（残り {0} 行）..." },

        // ── Commit graph ──
        ["CommitGraphTitle"] = new() { ["English"] = "Commit Graph", ["日本語"] = "コミットグラフ" },
        ["CommitGraphTooltip"] = new() { ["English"] = "Current branch - double-click for the commit graph", ["日本語"] = "現在のブランチ - ダブルクリックでコミットグラフ" },
        ["GraphRefresh"] = new() { ["English"] = "Refresh", ["日本語"] = "更新" },
        ["GraphLoadMore"] = new() { ["English"] = "Load more", ["日本語"] = "さらに読み込む" },
        ["GraphLoading"] = new() { ["English"] = "Loading...", ["日本語"] = "読み込み中..." },
        ["GraphCommitCountFmt"] = new() { ["English"] = "{0} commits", ["日本語"] = "{0} 件のコミット" },
        ["GraphNoCommits"] = new() { ["English"] = "No commits to show", ["日本語"] = "表示できるコミットがありません" },
        ["GraphUncommitted"] = new() { ["English"] = "Uncommitted Changes", ["日本語"] = "コミットされていない変更" },
        ["GraphColumnDescription"] = new() { ["English"] = "Description", ["日本語"] = "内容" },
        ["GraphColumnAuthor"] = new() { ["English"] = "Author", ["日本語"] = "作成者" },
        ["GraphColumnDate"] = new() { ["English"] = "Date", ["日本語"] = "日時" },
        ["GraphChangedFiles"] = new() { ["English"] = "Changed files", ["日本語"] = "変更されたファイル" },
        ["GraphChangedFilesFmt"] = new() { ["English"] = "Changed files ({0})", ["日本語"] = "変更されたファイル ({0})" },
        ["GraphNoFiles"] = new() { ["English"] = "No files changed", ["日本語"] = "変更されたファイルはありません" },
        ["GraphNoTextualDiff"] = new() { ["English"] = "No textual changes (binary, mode, or rename only)", ["日本語"] = "テキストの変更はありません（バイナリ・モード・リネームのみ）" },
        ["GraphAuthoredFmt"] = new() { ["English"] = "(authored {0})", ["日本語"] = "（作成 {0}）" },

        // ── Usage and cost ──
        ["PlanTier"] = new() { ["English"] = "Plan", ["日本語"] = "プラン" },
        ["PlanPro"] = new() { ["English"] = "Pro", ["日本語"] = "Pro" },
        ["PlanMax5x"] = new() { ["English"] = "Max 5x", ["日本語"] = "Max 5x" },
        ["PlanMax20x"] = new() { ["English"] = "Max 20x", ["日本語"] = "Max 20x" },
        ["CostTooltip"] = new() { ["English"] = "Tokens & cost", ["日本語"] = "トークンとコスト" },
        ["CostDashboard"] = new() { ["English"] = "Tokens & cost", ["日本語"] = "トークンとコスト" },
        ["CostDashboardTitle"] = new() { ["English"] = "Token / Cost Dashboard", ["日本語"] = "トークン/コスト ダッシュボード" },
        ["CostDaysSuffix"] = new() { ["English"] = "d", ["日本語"] = "日" },
        ["CostScopeProjectOnly"] = new() { ["English"] = "This project only", ["日本語"] = "このプロジェクトのみ" },
        ["CostExportCsv"] = new() { ["English"] = "Export CSV", ["日本語"] = "CSV をエクスポート" },
        ["CostLoading"] = new() { ["English"] = "Loading…", ["日本語"] = "読み込み中…" },
        ["CostLoadError"] = new() { ["English"] = "Failed to load usage data", ["日本語"] = "使用状況データの読み込みに失敗しました" },
        ["CostInput"] = new() { ["English"] = "Input", ["日本語"] = "入力" },
        ["CostOutput"] = new() { ["English"] = "Output", ["日本語"] = "出力" },
        ["CostCacheRead"] = new() { ["English"] = "Cache Read", ["日本語"] = "キャッシュ読み取り" },
        ["CostCacheCreation"] = new() { ["English"] = "Cache Creation", ["日本語"] = "キャッシュ作成" },
        ["CostTotalTokens"] = new() { ["English"] = "Total", ["日本語"] = "合計" },
        ["CostByModel"] = new() { ["English"] = "By Model", ["日本語"] = "モデル別" },
        ["CostByProject"] = new() { ["English"] = "By Project", ["日本語"] = "プロジェクト別" },
        ["CostBySession"] = new() { ["English"] = "By Session (top 50)", ["日本語"] = "セッション別（上位 50 件）" },
        ["CostColLabel"] = new() { ["English"] = "Label", ["日本語"] = "ラベル" },
        ["CostColTokens"] = new() { ["English"] = "Tokens", ["日本語"] = "トークン" },
        ["CostColCost"] = new() { ["English"] = "Cost", ["日本語"] = "コスト" },
        ["CostNoData"] = new() { ["English"] = "No data", ["日本語"] = "データなし" },

        // ── Mode labels ──
        ["ModeNormalLabel"] = new() { ["English"] = "Manual mode - every step is approved", ["日本語"] = "手動モード - 毎回承認する" },
        ["ModeAcceptEditsLabel"] = new() { ["English"] = "Auto-accept edits", ["日本語"] = "編集自動承認" },
        ["ModePlanLabel"] = new() { ["English"] = "Plan mode", ["日本語"] = "プランモード" },
        ["ModeBypassPermissionsLabel"] = new() { ["English"] = "Bypass permissions", ["日本語"] = "権限確認スキップ" },
        ["ModeUnknownLabel"] = new() { ["English"] = "Unknown mode", ["日本語"] = "モード不明" },
        ["ModeNormalShort"] = new() { ["English"] = "manual", ["日本語"] = "手動" },
        ["ModeAcceptEditsShort"] = new() { ["English"] = "auto-accept", ["日本語"] = "自動承認" },
        ["ModePlanShort"] = new() { ["English"] = "plan", ["日本語"] = "プラン" },
        ["ModeBypassPermissionsShort"] = new() { ["English"] = "bypass", ["日本語"] = "スキップ" },
        ["ModeUnknownShort"] = new() { ["English"] = "?", ["日本語"] = "?" },
        ["ModeSwitchFallback"] = new() { ["English"] = "Switch Mode", ["日本語"] = "モード切替" },
        ["ModeSwitchTooltip"] = new() { ["English"] = "Switch mode (Shift+Tab) - the mode name could not be read", ["日本語"] = "モード切替 (Shift+Tab) - モード名を読み取れません" },

        // {0} = reset time reported by the CLI
        ["DiagUsageLimitResetSuffix"] = new() { ["English"] = "It resets at {0}.", ["日本語"] = "{0} にリセットされます。" },

        // ── Permission prompt: what the command does ──
        ["CmdExplainRunTitle"] = new() { ["English"] = "Run command: {0}", ["日本語"] = "コマンドを実行します: {0}" },
        ["CmdExplainEditFileTitle"] = new() { ["English"] = "Edit file: {0}", ["日本語"] = "ファイルを編集します: {0}" },
        ["CmdExplainEditFileDetail"] = new() { ["English"] = "Claude wants to modify the contents of this file.", ["日本語"] = "このファイルの内容を変更しようとしています。" },
        ["CmdExplainWriteFileTitle"] = new() { ["English"] = "Write file: {0}", ["日本語"] = "ファイルを書き込みます: {0}" },
        ["CmdExplainWriteFileDetail"] = new() { ["English"] = "Claude wants to overwrite this file with new contents.", ["日本語"] = "このファイルを新しい内容で上書きしようとしています。" },
        ["CmdExplainCreateFileTitle"] = new() { ["English"] = "Create file: {0}", ["日本語"] = "ファイルを作成します: {0}" },
        ["CmdExplainCreateFileDetail"] = new() { ["English"] = "Claude wants to create a new file.", ["日本語"] = "新しいファイルを作成しようとしています。" },
        ["CmdExplainReadFileTitle"] = new() { ["English"] = "Read file: {0}", ["日本語"] = "ファイルを読み込みます: {0}" },
        ["CmdExplainReadFileDetail"] = new() { ["English"] = "Claude wants to read the contents of this file.", ["日本語"] = "このファイルの内容を読み込もうとしています。" },
        ["CmdExplainGenericDetail"] = new() { ["English"] = "This command isn't recognized. Review it carefully before allowing it to run.", ["日本語"] = "このコマンドは認識されていません。実行を許可する前によく確認してください。" },
        ["CmdExplainGitDetail"] = new() { ["English"] = "Git is used for version control - depending on the subcommand, this can read repository history or change tracked files.", ["日本語"] = "Git はバージョン管理のコマンドです。サブコマンドによって履歴を読むだけの場合と、管理対象ファイルを変更する場合があります。" },
        ["CmdExplainNpmDetail"] = new() { ["English"] = "npm manages Node.js packages and scripts; it can install packages or run project scripts.", ["日本語"] = "npm は Node.js のパッケージとスクリプトを管理します。パッケージのインストールやスクリプト実行を行います。" },
        ["CmdExplainDotnetDetail"] = new() { ["English"] = "The .NET CLI can build, run, restore, or publish this project.", ["日本語"] = ".NET CLI はこのプロジェクトのビルド・実行・リストア・発行を行います。" },
        ["CmdExplainPythonDetail"] = new() { ["English"] = "Runs a Python script, which can do anything the script is written to do.", ["日本語"] = "Python スクリプトを実行します。スクリプトの内容次第で何でも行われる可能性があります。" },
        ["CmdExplainPipDetail"] = new() { ["English"] = "pip installs or manages Python packages.", ["日本語"] = "pip は Python パッケージのインストール・管理を行います。" },
        ["CmdExplainNodeDetail"] = new() { ["English"] = "Runs a Node.js script or program.", ["日本語"] = "Node.js のスクリプトやプログラムを実行します。" },
        ["CmdExplainLsDetail"] = new() { ["English"] = "Lists files in a folder. It does not change anything.", ["日本語"] = "フォルダ内のファイル一覧を表示します。何も変更しません。" },
        ["CmdExplainDirDetail"] = new() { ["English"] = "Lists files in a folder. It does not change anything.", ["日本語"] = "フォルダ内のファイル一覧を表示します。何も変更しません。" },
        ["CmdExplainCatDetail"] = new() { ["English"] = "Prints a file's contents. It does not change anything.", ["日本語"] = "ファイルの内容を表示します。何も変更しません。" },
        ["CmdExplainTypeDetail"] = new() { ["English"] = "Prints a file's contents. It does not change anything.", ["日本語"] = "ファイルの内容を表示します。何も変更しません。" },
        ["CmdExplainCdDetail"] = new() { ["English"] = "Changes the current directory for the session.", ["日本語"] = "このセッションのカレントディレクトリを変更します。" },
        ["CmdExplainMkdirDetail"] = new() { ["English"] = "Creates a new folder.", ["日本語"] = "新しいフォルダを作成します。" },
        ["CmdExplainRmDetail"] = new() { ["English"] = "Deletes files or folders. This cannot be undone.", ["日本語"] = "ファイルやフォルダを削除します。元に戻せません。" },
        ["CmdExplainDelDetail"] = new() { ["English"] = "Deletes files. This cannot be undone.", ["日本語"] = "ファイルを削除します。元に戻せません。" },
        ["CmdExplainRmdirDetail"] = new() { ["English"] = "Deletes a folder and, depending on flags, everything inside it.", ["日本語"] = "フォルダを削除します。オプションによっては中身もすべて削除されます。" },
        ["CmdExplainMvDetail"] = new() { ["English"] = "Moves or renames a file or folder.", ["日本語"] = "ファイルやフォルダを移動・改名します。" },
        ["CmdExplainMoveDetail"] = new() { ["English"] = "Moves or renames a file or folder.", ["日本語"] = "ファイルやフォルダを移動・改名します。" },
        ["CmdExplainCpDetail"] = new() { ["English"] = "Copies a file or folder.", ["日本語"] = "ファイルやフォルダをコピーします。" },
        ["CmdExplainCopyDetail"] = new() { ["English"] = "Copies a file or folder.", ["日本語"] = "ファイルやフォルダをコピーします。" },
        ["CmdExplainCurlDetail"] = new() { ["English"] = "Fetches data from a URL. It only writes a file if given an output flag.", ["日本語"] = "URL からデータを取得します。出力先を指定した場合のみファイルに書き込みます。" },
        ["CmdExplainWgetDetail"] = new() { ["English"] = "Downloads a file from a URL and saves it to disk.", ["日本語"] = "URL からファイルをダウンロードしてディスクに保存します。" },
        ["CmdExplainSshDetail"] = new() { ["English"] = "Opens a connection to a remote machine and can run commands there.", ["日本語"] = "リモートマシンへ接続し、そこでコマンドを実行できます。" },
        ["CmdExplainScpDetail"] = new() { ["English"] = "Copies files to or from a remote machine.", ["日本語"] = "リモートマシンとの間でファイルをコピーします。" },
        ["CmdExplainDockerDetail"] = new() { ["English"] = "Manages Docker containers or images.", ["日本語"] = "Docker コンテナやイメージを管理します。" },
        ["CmdExplainGhDetail"] = new() { ["English"] = "Interacts with GitHub (issues, pull requests, releases, etc.).", ["日本語"] = "GitHub を操作します（Issue、Pull Request、リリースなど）。" },
        ["CmdExplainWingetDetail"] = new() { ["English"] = "Installs or manages software packages on Windows.", ["日本語"] = "Windows 上のソフトウェアパッケージをインストール・管理します。" },
        ["CmdExplainChocoDetail"] = new() { ["English"] = "Installs or manages software packages via Chocolatey.", ["日本語"] = "Chocolatey 経由でソフトウェアパッケージをインストール・管理します。" },
        ["CmdExplainPowershellDetail"] = new() { ["English"] = "Runs a PowerShell script or command, which can do anything the script is written to do.", ["日本語"] = "PowerShell スクリプトやコマンドを実行します。内容次第で何でも行われる可能性があります。" },
        ["CmdExplainTaskkillDetail"] = new() { ["English"] = "Forcibly ends a running process.", ["日本語"] = "実行中のプロセスを強制終了します。" },
        ["CmdExplainNetstatDetail"] = new() { ["English"] = "Shows network connection information. It does not change anything.", ["日本語"] = "ネットワーク接続情報を表示します。何も変更しません。" },
        ["CmdExplainFindstrDetail"] = new() { ["English"] = "Searches for text inside files. It does not change anything.", ["日本語"] = "ファイル内のテキストを検索します。何も変更しません。" },
        ["CmdExplainGrepDetail"] = new() { ["English"] = "Searches for text inside files. It does not change anything.", ["日本語"] = "ファイル内のテキストを検索します。何も変更しません。" },
        ["CmdExplainSedDetail"] = new() { ["English"] = "Processes text. With an in-place flag (-i) it can rewrite files.", ["日本語"] = "テキストを処理します。-i オプションを付けるとファイルを直接書き換えます。" },
        ["CmdExplainAwkDetail"] = new() { ["English"] = "Processes text, typically printing results rather than changing files.", ["日本語"] = "テキストを処理します。通常はファイルを変更せず結果を出力します。" },
        ["CmdExplainTarDetail"] = new() { ["English"] = "Creates or extracts an archive of files.", ["日本語"] = "ファイルのアーカイブを作成・展開します。" },
        ["CmdExplainZipDetail"] = new() { ["English"] = "Creates or extracts a compressed archive of files.", ["日本語"] = "ファイルの圧縮アーカイブを作成・展開します。" },
        ["CmdExplainFindDetail"] = new() { ["English"] = "Searches for files matching a pattern. It does not change anything by default.", ["日本語"] = "パターンに一致するファイルを検索します。既定では何も変更しません。" },
        ["CmdExplainEchoDetail"] = new() { ["English"] = "Prints text to the terminal. It does not change anything unless combined with a redirect.", ["日本語"] = "テキストをターミナルに出力します。リダイレクトと組み合わせない限り何も変更しません。" },

        // -- Command palette: entries with no label of their own elsewhere --
        ["PaletteChangedFiles"] = new() { ["English"] = "Changed Files", ["日本語"] = "変更ファイル" },
        ["PaletteCloseTab"] = new() { ["English"] = "Close Tab", ["日本語"] = "タブを閉じる" },
        ["PaletteNextTab"] = new() { ["English"] = "Next Tab", ["日本語"] = "次のタブ" },
        ["PalettePrevTab"] = new() { ["English"] = "Previous Tab", ["日本語"] = "前のタブ" },
        ["PaletteToggleExplorer"] = new() { ["English"] = "Toggle Explorer", ["日本語"] = "エクスプローラーを開閉" },
        ["PaletteToggleSnippets"] = new() { ["English"] = "Toggle Snippets", ["日本語"] = "スニペットを開閉" },
        ["PaletteToggleSettings"] = new() { ["English"] = "Toggle Settings", ["日本語"] = "設定を開閉" },
        ["PaletteToggleWindows"] = new() { ["English"] = "Toggle Windows Panel", ["日本語"] = "ウィンドウ一覧を開閉" },
        ["PaletteUsageChart"] = new() { ["English"] = "Usage Chart", ["日本語"] = "使用量チャート" },
        ["PaletteSwitchMode"] = new() { ["English"] = "Switch Mode (Shift+Tab)", ["日本語"] = "モード切替 (Shift+Tab)" },
        ["MarginalCostTooltip"] = new() { ["English"] = "Last turn, then what the next turn costs just to re-read the conversation.", ["日本語"] = "直前のターンの費用と、次のターンが会話を読み直すだけでかかる費用。" },
        ["StopTaskTooltip"] = new() { ["English"] = "Stop what the AI is doing (Esc)", ["日本語"] = "AI の実行を中断（Esc）" },

        // -- Git write operations --

        // -- Isolated sessions (git worktree) --
        ["AttachFiles"] = new() { ["English"] = "Attach files", ["日本語"] = "ファイルを添付" },
        ["IsolateSession"] = new() { ["English"] = "Isolate", ["日本語"] = "隔離" },
        ["IsolateTooltip"] = new() { ["English"] = "Open the next session in its own git worktree, so two windows cannot edit the same files", ["日本語"] = "次のセッションを専用の git worktree で開きます。複数ウィンドウが同じファイルを編集しなくなります" },
        ["WorktreeFailedTitle"] = new() { ["English"] = "The isolated checkout could not be created", ["日本語"] = "隔離チェックアウトを作成できませんでした" },
        ["WorktreeDirtyTitle"] = new() { ["English"] = "Close this isolated session?", ["日本語"] = "この隔離セッションを閉じますか？" },
        ["WorktreeDirtyFmt"] = new() { ["English"] = "{0} still has uncommitted changes. Closing removes its checkout and those changes are lost.", ["日本語"] = "{0} に未コミットの変更が残っています。閉じるとチェックアウトごと削除され、その変更は失われます。" },
        ["StageAll"] = new() { ["English"] = "Stage all", ["日本語"] = "すべてステージ" },
        ["StageFile"] = new() { ["English"] = "Click to stage", ["日本語"] = "クリックでステージ" },
        ["UnstageFile"] = new() { ["English"] = "Staged - click to unstage", ["日本語"] = "ステージ済み - クリックで取り消し" },
        ["CommitAction"] = new() { ["English"] = "Commit", ["日本語"] = "コミット" },
        ["CommitMessage"] = new() { ["English"] = "Commit message", ["日本語"] = "コミットメッセージ" },
        ["PushAction"] = new() { ["English"] = "Push", ["日本語"] = "プッシュ" },
        ["NothingStaged"] = new() { ["English"] = "Stage a file before committing.", ["日本語"] = "コミットするには先にファイルをステージしてください。" },
        ["NoCommitMessage"] = new() { ["English"] = "Write a commit message first.", ["日本語"] = "先にコミットメッセージを入力してください。" },
        ["GitFailedTitle"] = new() { ["English"] = "Git could not do that", ["日本語"] = "Git の実行に失敗しました" },
        ["BranchSwitchTooltip"] = new() { ["English"] = "Switch branch", ["日本語"] = "ブランチを切り替え" },
        ["NewBranch"] = new() { ["English"] = "New branch...", ["日本語"] = "新しいブランチ..." },
        ["NewBranchPrompt"] = new() { ["English"] = "Branch name", ["日本語"] = "ブランチ名" },
        ["PushConfirmTitle"] = new() { ["English"] = "Push to the remote?", ["日本語"] = "リモートへプッシュしますか？" },
        ["PushConfirmFmt"] = new() { ["English"] = "{0} will be pushed to {1}.", ["日本語"] = "{0} を {1} へプッシュします。" },
        ["PushConfirmNewUpstream"] = new() { ["English"] = "This branch has no upstream yet. It will be published to origin.", ["日本語"] = "このブランチにはまだ upstream がありません。origin に新規作成します。" },
        ["SwitchBranchConfirmTitle"] = new() { ["English"] = "Switch branch?", ["日本語"] = "ブランチを切り替えますか？" },
        ["SwitchBranchConfirmFmt"] = new() { ["English"] = "Switch the working tree to {0}?", ["日本語"] = "作業ツリーを {0} に切り替えますか？" },
        ["NothingToPush"] = new() { ["English"] = "Nothing to push.", ["日本語"] = "プッシュするコミットがありません。" },
        ["BranchAheadFmt"] = new() { ["English"] = "{0} ahead", ["日本語"] = "{0} 件先行" },
    };

    public static string Get(string key)
    {
        if (Strings.TryGetValue(key, out var translations) &&
            translations.TryGetValue(Language, out var text))
            return text;
        // Fallback: English, then key itself
        if (Strings.TryGetValue(key, out var fallback) &&
            fallback.TryGetValue("English", out var eng))
            return eng;
        return key;
    }

    public static string Get(string key, string defaultValue)
    {
        if (Strings.TryGetValue(key, out var translations) &&
            translations.TryGetValue(Language, out var text))
            return text;
        if (Strings.TryGetValue(key, out var fallback) &&
            fallback.TryGetValue("English", out var eng))
            return eng;
        return defaultValue;
    }
}
