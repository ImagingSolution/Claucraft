using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Claucraft.Services;

/// <summary>
/// Runs one console program to completion and reports everything it said. Lifted out of
/// <see cref="GitCli"/> once the source-control panel needed the same care for `gh` and for the
/// AI CLIs: both pipes drained at once so neither side can wedge, a hard timeout that takes the
/// whole process tree with it, and stdin for anything that must not survive command-line quoting.
/// </summary>
public static class ProcessRunner
{
    /// <summary>
    /// Starts <paramref name="exe"/> and waits for it.
    /// <paramref name="stdin"/> is written and the stream closed; null leaves it unredirected.
    /// <paramref name="env"/> adds variables to the child's environment for this call only.
    /// </summary>
    public static GitResult Run(string exe, string workingDirectory, string? stdin,
                                int timeoutMs, IReadOnlyDictionary<string, string>? env,
                                params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                RedirectStandardInput = stdin != null,
            };
            if (stdin != null)
                psi.StandardInputEncoding = new UTF8Encoding(false);

            var batch = IsBatch(exe);
            var shim = batch ? ResolveNpmShim(exe) : null;

            if (shim != null)
            {
                // An npm CLI is a batch file wrapped around "node <script>". Starting node
                // ourselves keeps the call off cmd.exe's command line, where every argument would
                // have to survive being re-parsed by quote counting.
                psi.FileName = shim.Node;
                foreach (var f in shim.Flags)
                    psi.ArgumentList.Add(f);
                psi.ArgumentList.Add(shim.Script);
                foreach (var a in args)
                    psi.ArgumentList.Add(a);
            }
            else if (batch)
            {
                // CreateProcess only ever appends ".exe" when it searches PATH, so a batch file
                // cannot be started directly. cmd.exe knows how.
                var refusal = FirstUnsafeForCmd(exe, args);
                if (refusal != null) return GitResult.Failed(refusal);

                psi.FileName = "cmd.exe";
                psi.Arguments = "/s /c \"" + QuoteForCmd(exe) + JoinForCmd(args) + "\"";
            }
            else
            {
                psi.FileName = exe;
                foreach (var a in args)
                    psi.ArgumentList.Add(a);
            }

            if (env != null)
            {
                foreach (var pair in env)
                {
                    // An empty value deletes the variable instead of blanking it. Some of what we
                    // have to clear - CLAUDECODE above all - is read as "set at all", so leaving
                    // an empty string behind would not help.
                    if (pair.Value.Length == 0) psi.Environment.Remove(pair.Key);
                    else psi.Environment[pair.Key] = pair.Value;
                }
            }

            using var proc = Process.Start(psi);
            if (proc == null) return GitResult.Failed(exe + " could not be started");

            if (stdin != null)
            {
                try
                {
                    proc.StandardInput.Write(stdin);
                    proc.StandardInput.Close();
                }
                catch (IOException)
                {
                    // A CLI that reads no input closes the pipe on us. Whatever it printed is
                    // still worth reading, so this is not a failure on its own.
                }
            }

            // Both pipes have to be drained at once. Reading one to the end while the other
            // fills its buffer wedges the child on its next write and this thread on a read
            // that never returns.
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(timeoutMs) || !Task.WaitAll(new Task[] { stdout, stderr }, timeoutMs))
            {
                try
                {
                    if (!proc.HasExited) proc.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Nothing further can be done about a process that will not die.
                }
                return GitResult.Failed(exe + " timed out");
            }

            return new GitResult(proc.ExitCode, stdout.Result, stderr.Result);
        }
        catch (Exception ex)
        {
            return GitResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Whether this executable has to be launched through cmd.exe to run at all - which is to say,
    /// whether its arguments are about to be handed to a parser that rebuilds them by counting
    /// quotes.
    ///
    /// A batch file cannot be started any other way. An npm shim is a batch file, but only
    /// nominally: what it runs is node, node is a real executable, and starting it directly avoids
    /// cmd.exe entirely. So a shim this can see through is not a shell case.
    /// </summary>
    public static bool NeedsShell(string exe) => IsBatch(exe) && ResolveNpmShim(exe) == null;

    private static bool IsBatch(string? exe)
    {
        var ext = Path.GetExtension(exe ?? "");
        return ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase);
    }

    // ---- npm shims ----

    /// <summary>What an npm shim really launches, once its batch plumbing is read away.</summary>
    private sealed record NpmShim(string Node, string[] Flags, string Script);

    /// <summary>
    /// Parsed shims, by path. A shim is written once at install time and then only ever replaced
    /// wholesale, so re-reading it on every call would buy nothing. A path that was not a shim
    /// when first asked about stays that way for this run, which at worst means falling back to
    /// the cmd.exe route the code already had.
    /// </summary>
    private static readonly ConcurrentDictionary<string, NpmShim?> ShimCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static NpmShim? ResolveNpmShim(string? exe) =>
        ShimCache.GetOrAdd(exe ?? "", ParseNpmShim);

    /// <summary>
    /// Reads a .cmd back to the node invocation it exists to perform, or null when it is some
    /// other batch script.
    ///
    /// Every npm shim ends on one line that forwards the caller's arguments with %*, and on that
    /// line sits the interpreter, whatever flags the package asked node for, and the script. That
    /// is the whole shape being matched: the script has to be a file that is actually there, which
    /// is what keeps an unrelated batch file from being mistaken for a shim.
    /// </summary>
    private static NpmShim? ParseNpmShim(string shimPath)
    {
        string text;
        string dir;
        try
        {
            dir = Path.GetDirectoryName(Path.GetFullPath(shimPath)) ?? "";
            text = File.ReadAllText(shimPath);
        }
        catch
        {
            return null;
        }

        foreach (var line in text.Split('\n'))
        {
            if (!line.Contains("%*", StringComparison.Ordinal)) continue;

            var tokens = SplitBatchTokens(line, dir);

            var script = tokens.FindIndex(IsNodeScript);
            if (script < 0) continue;

            var node = FindNode(dir);
            if (node == null) return null;

            // Flags the package asked node for - --no-warnings and the like - sit between the
            // interpreter and the script, and some packages do not run right without them.
            var flags = new List<string>();
            for (int i = script - 1; i >= 0 && tokens[i].StartsWith("-", StringComparison.Ordinal); i--)
                flags.Insert(0, tokens[i]);

            return new NpmShim(node, flags.ToArray(), Path.GetFullPath(tokens[script]));
        }

        return null;
    }

    private static bool IsNodeScript(string token)
    {
        // A leftover % means a batch variable this does not understand, so the path is not real.
        if (token.Length == 0 || token.Contains('%', StringComparison.Ordinal)) return false;

        var ext = Path.GetExtension(token);
        if (!ext.Equals(".js", StringComparison.OrdinalIgnoreCase)
            && !ext.Equals(".cjs", StringComparison.OrdinalIgnoreCase)
            && !ext.Equals(".mjs", StringComparison.OrdinalIgnoreCase)) return false;

        try { return File.Exists(token); }
        catch { return false; }
    }

    /// <summary>The node the shim itself would have picked: the one beside it, else PATH.</summary>
    private static string? FindNode(string shimDir)
    {
        try
        {
            var local = Path.Combine(shimDir, "node.exe");
            if (File.Exists(local)) return local;
        }
        catch
        {
            // A shim directory that cannot be combined with a name is no reason to skip PATH.
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var raw in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(raw.Trim().Trim('"'), "node.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // One unusable PATH entry says nothing about the rest.
            }
        }

        return null;
    }

    /// <summary>Splits one batch line the way cmd.exe would, expanding the shim's own %dp0%.</summary>
    private static List<string> SplitBatchTokens(string line, string shimDir)
    {
        var dir = shimDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var tokens = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false, any = false;

        foreach (var c in line)
        {
            if (c == '"') { quoted = !quoted; any = true; continue; }
            if (!quoted && (c == ' ' || c == '\t' || c == '\r'))
            {
                if (any) { tokens.Add(Expand(sb.ToString())); sb.Clear(); any = false; }
                continue;
            }
            sb.Append(c);
            any = true;
        }
        if (any) tokens.Add(Expand(sb.ToString()));

        return tokens;

        string Expand(string token) => token
            .Replace("%~dp0", dir, StringComparison.OrdinalIgnoreCase)
            .Replace("%dp0%", dir, StringComparison.OrdinalIgnoreCase);
    }

    private static string JoinForCmd(string[] args)
    {
        if (args == null || args.Length == 0) return "";

        var sb = new StringBuilder();
        foreach (var a in args)
            sb.Append(' ').Append(QuoteForCmd(a));
        return sb.ToString();
    }

    /// <summary>
    /// The first thing cmd.exe could not carry intact, described, or null when it can carry them
    /// all. A newline ends a command line and starts a fresh command, and a NUL truncates it;
    /// neither can be quoted or escaped away, so the only honest answer is to refuse the call
    /// rather than run something other than what was asked for.
    /// </summary>
    private static string? FirstUnsafeForCmd(string exe, string[] args)
    {
        if (Unsafe(exe)) return exe + ": the program path contains a line break";

        foreach (var a in args)
            if (Unsafe(a)) return exe + ": an argument contains a line break";

        return null;

        static bool Unsafe(string? value)
            => value != null && value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0;
    }

    /// <summary>
    /// Quotes one argument for the command line cmd.exe rebuilds.
    ///
    /// cmd.exe has no notion of a backslash escape: it walks the line counting quotes, and what
    /// sits between an opening and a closing one is literal. An argument is therefore safe exactly
    /// as long as that count stays balanced, which is why a literal quote is written as two quotes
    /// rather than the C-style backslash-quote. The pair closes and immediately reopens, leaving no
    /// gap for a pipe or an ampersand to sit outside quotes in; backslash-quote instead leaves
    /// cmd.exe believing the quote was closed, and hands it the rest of the value as commands.
    ///
    /// Everything is quoted, harmless-looking values included: whether an argument needs quoting is
    /// precisely the judgement that goes wrong.
    /// </summary>
    private static string QuoteForCmd(string value)
    {
        value ??= "";

        var sb = new StringBuilder(value.Length + 8);
        sb.Append('"');
        foreach (var c in value)
        {
            if (c == '"') sb.Append('"');
            sb.Append(c);
        }
        sb.Append('"');
        return sb.ToString();
    }
}
