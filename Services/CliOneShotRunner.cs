using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Claucraft.Services;

/// <summary>
/// Runs one prompt through whichever AI CLI is selected and hands back its answer, with none of
/// the session state a normal launch would carry. Shared by every feature that needs a single
/// throwaway answer from the CLI rather than an interactive session - the commit message draft
/// and the staged-changes secret scan alike.
/// </summary>
public static class CliOneShotRunner
{
    /// <summary>
    /// What a Claude Code session exports to everything it starts. Claucraft may itself have been
    /// launched from inside one, and a CLI that inherits these joins that session instead of
    /// running once on its own - so they are cleared for this call.
    /// </summary>
    private static readonly string[] SessionVars =
    {
        "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_EXECPATH", "CLAUDE_CODE_SESSION_ID",
        "CLAUDE_CODE_CHILD_SESSION", "CLAUDE_CODE_BRIDGE_SESSION_ID", "CLAUDE_CODE_MESSAGING_SOCKET",
        "CLAUDE_CODE_MESSAGING_TOKEN", "CLAUDE_PID", "CLAUDE_EFFORT",
    };

    private static Dictionary<string, string> CleanEnvironment()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // ProcessRunner reads an empty value as "remove this variable".
        foreach (var name in SessionVars) env[name] = "";

        // Claude Code's knob for extended thinking, ignored by every other CLI. A one-shot answer
        // does not need it, and it was over half the bill: measured on the commit draft, turning
        // it off took a draft from 5,900 output tokens to 138.
        env["MAX_THINKING_TOKENS"] = "0";
        return env;
    }

    /// <summary>
    /// Sends <paramref name="prompt"/> to <paramref name="provider"/>'s one-shot mode and returns
    /// its cleaned answer, or null when the provider has no one-shot preset, the call failed, or
    /// it produced nothing usable. Never throws for a CLI failure - only for cancellation.
    /// </summary>
    public static async Task<string?> RunAsync(string workingDirectory, CliProvider provider,
        string prompt, int timeoutMs, CancellationToken ct = default)
    {
        if (provider == null || string.IsNullOrWhiteSpace(provider.OneShotArgs)) return null;

        var args = SplitArgs(provider.OneShotArgs);
        bool inline = Array.Exists(args, a => a.Contains("{prompt}", StringComparison.Ordinal));
        var exe = string.IsNullOrEmpty(provider.ResolvedPath) ? provider.Exe : provider.ResolvedPath!;

        // The prompt carries content an outsider may have written (a diff, a staged change), so
        // it is the one value here that is not ours. Anything ProcessRunner has to start through
        // cmd.exe gets its command line rebuilt by quote counting, which is no place for that, so
        // such a preset gets no answer at all rather than a command line assembled out of
        // untrusted text. Most CLIs are not that case: an .exe, and an npm shim ProcessRunner can
        // read back to node, both take their arguments as arguments.
        if (inline && ProcessRunner.NeedsShell(exe)) return null;

        string? stdin = inline ? null : prompt;
        if (inline)
        {
            for (int i = 0; i < args.Length; i++)
                args[i] = args[i].Replace("{prompt}", prompt, StringComparison.Ordinal);
        }

        var result = await Task.Run(
            () => ProcessRunner.Run(exe, workingDirectory, stdin, timeoutMs, CleanEnvironment(), args), ct);

        ct.ThrowIfCancellationRequested();
        if (!result.Ok) return null;

        var text = Clean(result.StdOut);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    // ── Output ─────────────────────────────────────────────────────────

    /// <summary>
    /// Strips what a CLI adds around an answer: a fenced block, and the blank lines either side.
    /// An answer that legitimately contains a fenced block keeps it - only an outer fence that
    /// wraps the whole reply is removed.
    /// </summary>
    internal static string Clean(string output)
    {
        var text = (output ?? "").Replace("\r\n", "\n").Trim();
        if (text.Length == 0) return "";

        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            int firstBreak = text.IndexOf('\n');
            int lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstBreak > 0 && lastFence > firstBreak)
                text = text[(firstBreak + 1)..lastFence].Trim();
        }

        return text.Trim();
    }

    // ── Argument parsing ───────────────────────────────────────────────

    /// <summary>
    /// Splits a stored argument string into arguments, honouring double quotes so a preset can
    /// hold a flag value with a space in it.
    /// </summary>
    internal static string[] SplitArgs(string text)
    {
        var args = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return args.ToArray();

        var current = new StringBuilder();
        bool quoted = false;
        bool any = false;

        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
                continue;
            }
            if (!quoted && char.IsWhiteSpace(c))
            {
                if (any) { args.Add(current.ToString()); current.Clear(); any = false; }
                continue;
            }
            current.Append(c);
            any = true;
        }

        if (any) args.Add(current.ToString());
        return args.ToArray();
    }
}
