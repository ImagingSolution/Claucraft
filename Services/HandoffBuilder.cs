using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Claucraft.Services;

/// <summary>
/// Builds a hand-off brief from a session transcript so work can continue in a fresh session.
///
/// Why this exists: every turn re-reads the whole conversation at the cache-read rate, so a
/// long session keeps paying for its own history. /compact fixes the size but pays full price
/// to do it - the model reads the entire context and writes the summary. The transcript is on
/// disk and Claucraft can already read it, so the same material can be extracted locally for
/// nothing and used to start a short session instead.
///
/// This is structural extraction, not summarisation: it reports what was asked, what was
/// touched, and where things stopped. It does not restate intent, and the brief is always
/// shown to the user for editing before it goes anywhere.
/// </summary>
public static class HandoffBuilder
{
    private const int MaxPrompts = 40;
    private const int MaxPromptChars = 500;
    private const int MaxFiles = 60;
    private const int MaxCommands = 40;
    private const int MaxCommandChars = 200;
    private const int MaxTailChars = 2000;
    private const int MaxLineChars = 20_000_000;

    /// <summary>Tools whose file_path argument marks a file as touched by the session.</summary>
    private static readonly HashSet<string> FileTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "Edit", "Write", "MultiEdit", "NotebookEdit",
    };

    /// <summary>
    /// Tools that run a shell command, all of which name the argument "command". PowerShell
    /// belongs here as much as Bash - on Windows it carries most of the work.
    /// </summary>
    private static readonly HashSet<string> CommandTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "Bash", "PowerShell",
    };

    /// <summary>
    /// Files written under the temp directory are scratch - patch scripts, screenshots, throwaway
    /// harnesses. A busy session produces dozens of them, and listing them buries the project
    /// files the next session actually needs to know about. They are counted, never silently lost.
    /// </summary>
    private static readonly string TempRoot = NormalizeDir(Path.GetTempPath());

    public static Task<string> BuildAsync(string jsonlPath) => Task.Run(() => Build(jsonlPath));

    /// <summary>
    /// Reads the transcript and renders a Markdown brief. Returns an empty string when the
    /// file is missing or holds nothing worth carrying over.
    /// </summary>
    public static string Build(string jsonlPath)
    {
        if (string.IsNullOrEmpty(jsonlPath) || !File.Exists(jsonlPath)) return "";

        var prompts = new List<string>();
        var files = new List<string>();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var commands = new List<string>();
        var seenCommands = new HashSet<string>(StringComparer.Ordinal);
        var todos = new List<string>();
        string lastAssistantText = "";

        int promptsDropped = 0, filesDropped = 0, commandsDropped = 0, scratchSkipped = 0;

        try
        {
            using var stream = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.Length > MaxLineChars) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;

                    // Injected context and subagent transcripts are not the user's own thread.
                    if (IsTrue(root, "isMeta") || IsTrue(root, "isSidechain")) continue;

                    var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
                    if (!root.TryGetProperty("message", out var msg)) continue;

                    if (type == "user")
                    {
                        // A user line carrying a tool result is the harness replying, not a request.
                        if (root.TryGetProperty("toolUseResult", out _)) continue;

                        var text = SessionMessageReader.CleanMetadataTags(ExtractText(msg));
                        if (string.IsNullOrWhiteSpace(text)) continue;

                        if (prompts.Count < MaxPrompts) prompts.Add(Truncate(text, MaxPromptChars));
                        else promptsDropped++;
                    }
                    else if (type == "assistant")
                    {
                        if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                            continue;

                        foreach (var block in content.EnumerateArray())
                        {
                            if (block.ValueKind != JsonValueKind.Object) continue;
                            var blockType = block.TryGetProperty("type", out var bt) ? bt.GetString() : null;

                            if (blockType == "text")
                            {
                                var t = block.TryGetProperty("text", out var tp) ? tp.GetString() : null;
                                if (!string.IsNullOrWhiteSpace(t)) lastAssistantText = t!;
                            }
                            else if (blockType == "tool_use")
                            {
                                CollectToolUse(block, files, seenFiles, commands, seenCommands, todos,
                                    ref filesDropped, ref commandsDropped, ref scratchSkipped);
                            }
                        }
                    }
                }
                catch
                {
                    // One malformed line must not abort the brief.
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HandoffBuilder] Failed to read {jsonlPath}: {ex.Message}");
            return "";
        }

        if (prompts.Count == 0 && files.Count == 0 && commands.Count == 0) return "";

        return Render(prompts, files, commands, todos, lastAssistantText,
            promptsDropped, filesDropped, commandsDropped, scratchSkipped);
    }

    private static void CollectToolUse(
        JsonElement block,
        List<string> files, HashSet<string> seenFiles,
        List<string> commands, HashSet<string> seenCommands,
        List<string> todos,
        ref int filesDropped, ref int commandsDropped, ref int scratchSkipped)
    {
        var name = block.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
        if (name.Length == 0) return;
        if (!block.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object) return;

        if (FileTools.Contains(name))
        {
            var path = input.TryGetProperty("file_path", out var fp) && fp.ValueKind == JsonValueKind.String
                ? fp.GetString() : null;
            if (!string.IsNullOrWhiteSpace(path) && seenFiles.Add(path!))
            {
                if (IsScratchPath(path!)) scratchSkipped++;
                else if (files.Count < MaxFiles) files.Add(path!);
                else filesDropped++;
            }
        }
        else if (CommandTools.Contains(name))
        {
            var raw = input.TryGetProperty("command", out var cp) && cp.ValueKind == JsonValueKind.String
                ? cp.GetString() : null;
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var cmd = raw!.Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (cmd.Length > 0 && seenCommands.Add(cmd))
                {
                    if (commands.Count < MaxCommands) commands.Add(Truncate(cmd, MaxCommandChars));
                    else commandsDropped++;
                }
            }
        }
        else if (name.Equals("TodoWrite", StringComparison.OrdinalIgnoreCase))
        {
            // Only the final list matters, so each TodoWrite replaces the previous one.
            if (!input.TryGetProperty("todos", out var list) || list.ValueKind != JsonValueKind.Array) return;
            todos.Clear();
            foreach (var todo in list.EnumerateArray())
            {
                if (todo.ValueKind != JsonValueKind.Object) continue;
                var content = todo.TryGetProperty("content", out var c) ? c.GetString() : null;
                var status = todo.TryGetProperty("status", out var st) ? st.GetString() : null;
                if (string.IsNullOrWhiteSpace(content)) continue;
                var mark = string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) ? "x" : " ";
                todos.Add($"- [{mark}] {content}");
            }
        }
    }

    private static string Render(
        List<string> prompts, List<string> files, List<string> commands, List<string> todos,
        string lastAssistantText, int promptsDropped, int filesDropped, int commandsDropped,
        int scratchSkipped)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Session hand-off");
        sb.AppendLine();
        sb.AppendLine("Continuing work from an earlier session. The sections below were extracted");
        sb.AppendLine("verbatim from that session's transcript - they are a record, not a summary,");
        sb.AppendLine("and none of it was written by you. Re-read any file you intend to change.");
        sb.AppendLine();

        if (prompts.Count > 0)
        {
            sb.AppendLine("## What I asked for");
            sb.AppendLine();
            for (int i = 0; i < prompts.Count; i++)
                sb.AppendLine($"{i + 1}. {prompts[i].Replace("\n", "\n   ")}");
            if (promptsDropped > 0)
                sb.AppendLine($"   _(plus {promptsDropped} earlier request(s) not listed)_");
            sb.AppendLine();
        }

        if (files.Count > 0)
        {
            sb.AppendLine("## Files this session changed");
            sb.AppendLine();
            foreach (var f in files) sb.AppendLine($"- `{f}`");
            if (filesDropped > 0) sb.AppendLine($"- _(plus {filesDropped} more not listed)_");
            if (scratchSkipped > 0)
                sb.AppendLine($"- _({scratchSkipped} scratch file(s) under the temp directory omitted)_");
            sb.AppendLine();
        }

        if (commands.Count > 0)
        {
            sb.AppendLine("## Commands that were run");
            sb.AppendLine();
            foreach (var c in commands) sb.AppendLine($"- `{c}`");
            if (commandsDropped > 0) sb.AppendLine($"- _(plus {commandsDropped} more not listed)_");
            sb.AppendLine();
        }

        if (todos.Count > 0)
        {
            sb.AppendLine("## Task list as it stood");
            sb.AppendLine();
            foreach (var t in todos) sb.AppendLine(t);
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(lastAssistantText))
        {
            sb.AppendLine("## Where the last session stopped");
            sb.AppendLine();
            sb.AppendLine(Tail(lastAssistantText, MaxTailChars));
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string ExtractText(JsonElement message)
    {
        if (message.ValueKind == JsonValueKind.String) return message.GetString() ?? "";
        if (message.ValueKind != JsonValueKind.Object) return "";
        if (!message.TryGetProperty("content", out var content)) return "";
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";

        var parts = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.String)
            {
                parts.Add(block.GetString() ?? "");
                continue;
            }
            if (block.ValueKind != JsonValueKind.Object) continue;
            var type = block.TryGetProperty("type", out var tp) ? tp.GetString() : null;
            if (type != "text") continue;
            var text = block.TryGetProperty("text", out var txt) ? txt.GetString() : null;
            if (!string.IsNullOrEmpty(text)) parts.Add(text!);
        }
        return string.Join("\n", parts);
    }

    private static bool IsTrue(JsonElement root, string name)
        => root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.True;

    private static string NormalizeDir(string path)
    {
        try { path = System.IO.Path.GetFullPath(path); }
        catch { return ""; }

        return path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
               + System.IO.Path.DirectorySeparatorChar;
    }

    private static bool IsScratchPath(string path)
    {
        if (TempRoot.Length <= 1) return false;
        try
        {
            return System.IO.Path.GetFullPath(path)
                .StartsWith(TempRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string Truncate(string s, int max)
    {
        s = s.Trim();
        return s.Length <= max ? s : s[..max].TrimEnd() + " ...";
    }

    private static string Tail(string s, int max)
    {
        s = s.Trim();
        return s.Length <= max ? s : "... " + s[^max..].TrimStart();
    }
}
