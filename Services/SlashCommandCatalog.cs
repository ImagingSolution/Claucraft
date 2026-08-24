using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Claucraft.Services;

public record SlashCommand(string Name, string Description, string DescriptionJa, bool NeedsArgument = false);

/// <summary>
/// Lists slash commands for the active AI CLI, so the UI can offer completion/help without
/// the user having to remember them. Built-ins are hard-coded per provider; a user can extend
/// or override them via %AppData%\Claucraft\slashcommands.json without a rebuild.
/// </summary>
public static class SlashCommandCatalog
{
    private static readonly string OverridesFile = Path.Combine(CliProviderService.ConfigFolderPath, "slashcommands.json");

    // Order is deliberate (most-used first) and preserved as-is - the catalog is not re-sorted.
    private static readonly List<SlashCommand> ClaudeBuiltins = new()
    {
        new("/help", "Show available commands and usage help.", "利用可能なコマンドと使い方を表示します。"),
        new("/clear", "Clear the conversation history and start a fresh context.", "会話履歴をクリアして新しいコンテキストを開始します。"),
        new("/compact", "Summarize the conversation so far to free up context space.", "これまでの会話を要約し、コンテキストの空き容量を増やします。"),
        new("/model", "Switch the Claude model used for this session.", "このセッションで使用する Claude モデルを切り替えます。"),
        new("/init", "Generate or update a CLAUDE.md file for this project.", "このプロジェクト用の CLAUDE.md を生成・更新します。"),
        new("/review", "Ask Claude to review code, a pull request, or the current changes.", "コードや Pull Request、現在の変更をレビューさせます。"),
        new("/agents", "Manage the subagents available to this session.", "このセッションで使えるサブエージェントを管理します。"),
        new("/mcp", "View and manage connected MCP servers.", "接続中の MCP サーバーを表示・管理します。"),
        new("/config", "View or change Claude Code settings.", "Claude Code の設定を表示・変更します。"),
        new("/cost", "Show token usage and an estimated cost for this session.", "このセッションのトークン使用量と概算コストを表示します。"),
        new("/status", "Show account, model, and connection status.", "アカウント・モデル・接続状況を表示します。"),
        new("/memory", "Edit the project or user memory files.", "プロジェクト/ユーザーのメモリファイルを編集します。"),
        new("/resume", "Resume a previous conversation.", "過去の会話を再開します。"),
        new("/rewind", "Roll the conversation and/or files back to an earlier point.", "会話やファイルを以前の状態に巻き戻します。"),
        new("/context", "Show what is currently taking up the context window.", "コンテキストウィンドウの使用内訳を表示します。"),
        new("/permissions", "View or edit tool permission rules.", "ツールの許可ルールを表示・編集します。"),
        new("/hooks", "View or manage configured hooks.", "設定済みのフックを表示・管理します。"),
        new("/doctor", "Check the Claude Code installation for problems.", "Claude Code のインストール状態を診断します。"),
        new("/login", "Sign in to your Claude account.", "Claude アカウントにサインインします。"),
        new("/logout", "Sign out of the current account.", "現在のアカウントからサインアウトします。"),
        new("/exit", "Exit Claude Code.", "Claude Code を終了します。"),
        new("/vim", "Toggle Vim key bindings for input.", "入力の Vim キーバインドを切り替えます。"),
        new("/terminal-setup", "Configure terminal integration (e.g. Shift+Enter for newline).", "ターミナル連携を設定します（例: Shift+Enter での改行）。"),
        new("/add-dir", "Add an additional working directory to this session.", "このセッションに作業ディレクトリを追加します。", NeedsArgument: true),
        new("/export", "Export the current conversation to a file.", "現在の会話をファイルへ書き出します。"),
    };

    // Other CLIs' slash-command sets are not documented as reliably as Claude Code's, so only
    // the handful of commands that are safe to assume across providers are listed here.
    private static readonly List<SlashCommand> MinimalBuiltins = new()
    {
        new("/help", "Show available commands and usage help.", "利用可能なコマンドと使い方を表示します。"),
        new("/clear", "Clear the conversation history and start a fresh context.", "会話履歴をクリアして新しいコンテキストを開始します。"),
        new("/exit", "Exit the CLI.", "CLI を終了します。"),
    };

    /// <summary>Built-in commands for the given provider id, plus any user overrides.</summary>
    public static IReadOnlyList<SlashCommand> ForProvider(string providerId)
    {
        var builtins = providerId == CliProviderService.ClaudeId ? ClaudeBuiltins : MinimalBuiltins;

        var overrides = LoadOverrides();
        if (!overrides.TryGetValue(providerId, out var providerOverrides) || providerOverrides.Count == 0)
            return builtins;

        // An override with a name matching a built-in replaces it in place, keeping the
        // curated ordering; anything new is appended after.
        var result = new List<SlashCommand>(builtins);
        foreach (var over in providerOverrides)
        {
            var idx = result.FindIndex(c => string.Equals(c.Name, over.Name, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) result[idx] = over;
            else result.Add(over);
        }
        return result;
    }

    /// <summary>Project-local custom commands read from {projectFolder}\.claude\commands\*.md (Claude Code only).</summary>
    public static IReadOnlyList<SlashCommand> ForProject(string providerId, string? projectFolder)
    {
        if (providerId != CliProviderService.ClaudeId || string.IsNullOrWhiteSpace(projectFolder))
            return Array.Empty<SlashCommand>();

        try
        {
            var commandsDir = Path.Combine(projectFolder, ".claude", "commands");
            if (!Directory.Exists(commandsDir))
                return Array.Empty<SlashCommand>();

            var result = new List<SlashCommand>();
            foreach (var file in Directory.EnumerateFiles(commandsDir, "*.md"))
            {
                try
                {
                    var name = "/" + Path.GetFileNameWithoutExtension(file);
                    var description = ReadDescription(file);
                    result.Add(new SlashCommand(name, description, description));
                }
                catch { }
            }
            return result;
        }
        catch
        {
            return Array.Empty<SlashCommand>();
        }
    }

    /// <summary>Pulls "description:" out of a leading YAML frontmatter block, else the first non-empty line.</summary>
    private static string ReadDescription(string filePath)
    {
        var lines = File.ReadAllLines(filePath);
        var bodyStart = 0;

        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            bodyStart = lines.Length; // no closing "---" found - treat the whole file as frontmatter
            for (var i = 1; i < lines.Length; i++)
            {
                if (lines[i].Trim() == "---")
                {
                    bodyStart = i + 1;
                    break;
                }

                var line = lines[i];
                var colonIdx = line.IndexOf(':');
                if (colonIdx > 0 && line[..colonIdx].Trim().Equals("description", StringComparison.OrdinalIgnoreCase))
                {
                    var value = line[(colonIdx + 1)..].Trim().Trim('"', '\'');
                    if (value.Length > 0) return Truncate(value);
                }
            }
        }

        for (var i = bodyStart; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length > 0) return Truncate(trimmed);
        }

        return "";
    }

    private static string Truncate(string value) => value.Length <= 80 ? value : value[..80];

    private static Dictionary<string, List<SlashCommand>> LoadOverrides()
    {
        try
        {
            if (!File.Exists(OverridesFile))
                return EmptyOverrides;

            var json = File.ReadAllText(OverridesFile);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var shape = JsonSerializer.Deserialize<OverridesFileShape>(json, options);
            return shape?.Providers ?? EmptyOverrides;
        }
        catch
        {
            return EmptyOverrides;
        }
    }

    private static readonly Dictionary<string, List<SlashCommand>> EmptyOverrides = new();

    private class OverridesFileShape
    {
        public Dictionary<string, List<SlashCommand>>? Providers { get; set; }
    }
}
