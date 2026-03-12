using System.Collections.Generic;

namespace ClaudeCodeMDI.Services;

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

        // ── Settings Panel ──
        ["ConsoleSettings"] = new() { ["English"] = "Console Settings", ["日本語"] = "コンソール設定" },
        ["FontFamily"] = new() { ["English"] = "Font Family", ["日本語"] = "フォント" },
        ["FontSize"] = new() { ["English"] = "Font Size", ["日本語"] = "フォントサイズ" },
        ["Theme"] = new() { ["English"] = "Theme", ["日本語"] = "テーマ" },
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
        ["FullView"] = new() { ["English"] = "Full view", ["日本語"] = "最大表示" },

        // ── Window Title ──
        ["AppTitle"] = new() { ["English"] = "Claude Code MDI", ["日本語"] = "Claude Code MDI" },

        // ── Settings - Claude Folder ──
        ["OpenClaudeFolder"] = new() { ["English"] = "Open .claude Folder", ["日本語"] = ".claude フォルダを開く" },

        // ── Usage Chart ──
        ["ClickToShowUsage"] = new() { ["English"] = "Click to show usage chart", ["日本語"] = "クリックして使用状況チャートを表示" },
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
}
