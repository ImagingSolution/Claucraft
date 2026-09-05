using System;
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

            if (NeedsShell(exe))
            {
                // CreateProcess only ever appends ".exe" when it searches PATH, so an npm-installed
                // CLI - which is a .cmd shim - cannot be started directly. cmd.exe knows how.
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

    /// <summary>Whether this executable has to be launched through cmd.exe to run at all.</summary>
    public static bool NeedsShell(string exe)
    {
        var ext = Path.GetExtension(exe ?? "");
        return ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase);
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
