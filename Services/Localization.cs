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
        ["NewClaude"] = new() { ["English"] = "New Claude", ["日本語"] = "新規 Claude" },
        ["Session"] = new() { ["English"] = "Session", ["日本語"] = "セッション" },
        ["SelectSession"] = new() { ["English"] = "Select a session to resume...", ["日本語"] = "再開するセッションを選択..." },
        ["Resume"] = new() { ["English"] = "Resume", ["日本語"] = "再開" },

        // ── Status Bar ──
        ["NoProjectFolder"] = new() { ["English"] = "No project folder selected", ["日本語"] = "プロジェクトフォルダ未選択" },
        ["Usage"] = new() { ["English"] = "Usage", ["日本語"] = "使用量" },
        ["Windows"] = new() { ["English"] = "windows", ["日本語"] = "ウィンドウ" },
        ["Ready"] = new() { ["English"] = "Ready", ["日本語"] = "準備完了" },
        ["Running"] = new() { ["English"] = "Running", ["日本語"] = "実行中" },
        ["Exited"] = new() { ["English"] = "Exited", ["日本語"] = "終了" },
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
        ["EnableCharts"] = new() { ["English"] = "Enable Chart/Diagram Rendering", ["日本語"] = "チャート/図の描画を有効にする" },
        ["ChartPreview"] = new() { ["English"] = "Chart Preview", ["日本語"] = "チャートプレビュー" },
        ["SaveImage"] = new() { ["English"] = "Save Image", ["日本語"] = "画像を保存" },
        ["CopyImage"] = new() { ["English"] = "Copy Image", ["日本語"] = "画像をコピー" },
        ["MermaidDiagram"] = new() { ["English"] = "Mermaid Diagram", ["日本語"] = "Mermaid 図" },
        ["ExcalidrawDiagram"] = new() { ["English"] = "Excalidraw Diagram", ["日本語"] = "Excalidraw 図" },
        ["BarChart"] = new() { ["English"] = "Bar Chart", ["日本語"] = "棒グラフ" },
        ["LineChart"] = new() { ["English"] = "Line Chart", ["日本語"] = "折れ線グラフ" },
        ["PieChart"] = new() { ["English"] = "Pie Chart", ["日本語"] = "円グラフ" },
        ["ClickToRender"] = new() { ["English"] = "Click to render", ["日本語"] = "クリックして描画" },
        ["OpenInWindow"] = new() { ["English"] = "Open in Window", ["日本語"] = "ウィンドウで開く" },
        ["SaveAsArtifact"] = new() { ["English"] = "Save as Artifact", ["日本語"] = "アーティファクトとして保存" },
        ["OpenArtifact"] = new() { ["English"] = "Open File", ["日本語"] = "ファイルを開く" },
        ["DiagramTooltip"] = new() { ["English"] = "Diagram Viewer", ["日本語"] = "ダイアグラムビューア" },
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
