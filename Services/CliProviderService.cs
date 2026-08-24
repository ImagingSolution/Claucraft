using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Claucraft.Services;

/// <summary>
/// Owns the list of AI CLIs Claucraft can drive, which one is active, and all
/// command-line construction. Definitions live in %AppData%\Claucraft\providers.json
/// so argument changes in a CLI can be fixed without rebuilding.
/// </summary>
public class CliProviderService
{
    public const string ClaudeId = "claude";

    public const string LightProfileId = "light";
    public const string StandardProfileId = "standard";
    public const string DeepProfileId = "deep";

    private const int VersionTimeoutMs = 3000;

    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Claucraft");

    private static readonly string ProvidersFile = Path.Combine(SettingsDir, "providers.json");

    private static readonly Regex VersionRegex = new(@"\d+\.\d+[\w.\-]*", RegexOptions.Compiled);

    /// <summary>
    /// CLIs that were replaced by a successor. An existing providers.json is rewritten so the
    /// dead entry disappears instead of lingering as an unusable radio button.
    /// </summary>
    private static readonly Dictionary<string, string> RetiredIds = new()
    {
        // Google folded Gemini CLI into Antigravity CLI in May 2026; the gemini binary is gone.
        ["gemini"] = "antigravity",
    };

    private readonly List<CliProvider> _providers;
    private string _activeId = ClaudeId;

    public CliProviderService()
    {
        _providers = Load();
        ResolveExecutables();
    }

    public IReadOnlyList<CliProvider> Providers => _providers;

    /// <summary>Providers whose executable was found on this machine.</summary>
    public IEnumerable<CliProvider> InstalledProviders => _providers.Where(p => p.IsInstalled);

    public string ActiveId
    {
        get => _activeId;
        set
        {
            var id = RetiredIds.TryGetValue(value ?? "", out var successor) ? successor : value ?? "";
            _activeId = _providers.Any(p => p.Id == id) ? id : ClaudeId;
        }
    }

    public CliProvider Active =>
        _providers.FirstOrDefault(p => p.Id == _activeId)
        ?? _providers.FirstOrDefault(p => p.Id == ClaudeId)
        ?? _providers[0];

    public CliFeatures Features => Active.Features;

    public CliProvider? Find(string id) => _providers.FirstOrDefault(p => p.Id == id);

    /// <summary>Launch profiles the active CLI offers. Empty means the picker stays hidden.</summary>
    public IReadOnlyList<LaunchProfile> ActiveProfiles => Active.Profiles;

    /// <summary>
    /// Resolves a stored profile id against the active CLI. Falls back to the standard
    /// profile, then the first one, so a stale id from another CLI never launches unflagged.
    /// </summary>
    public LaunchProfile? FindProfile(string? id)
    {
        var list = Active.Profiles;
        if (list.Count == 0) return null;
        return list.FirstOrDefault(p => p.Id == id)
            ?? list.FirstOrDefault(p => p.Id == StandardProfileId)
            ?? list[0];
    }

    public static string ConfigFolderPath => SettingsDir;

    /// <summary>Absolute path of the active provider's config directory, e.g. C:\Users\me\.claude.</summary>
    public string ActiveConfigDirPath
    {
        get
        {
            var dir = Active.ConfigDir;
            if (string.IsNullOrWhiteSpace(dir)) return "";
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), dir);
        }
    }

    // ── Command construction ──

    /// <summary>
    /// Command for a brand-new session. NewArgs is only applied when an initial prompt
    /// is set; without one the CLI is launched with just the profile flags.
    /// </summary>
    public string BuildNewCommand(string? initialPrompt, LaunchProfile? profile = null)
    {
        var p = Active;
        var exe = QuoteExe(p.Exe);
        var prompt = SanitizePrompt(initialPrompt);
        var extra = SanitizePrompt(profile?.ExtraArgs);

        if (string.IsNullOrEmpty(prompt))
            return string.IsNullOrEmpty(extra) ? exe : $"{exe} {extra}";

        var args = p.NewArgs;
        args = string.IsNullOrWhiteSpace(args)
            ? QuoteArg(prompt)
            : args.Contains("{prompt}")
                ? args.Replace("{prompt}", QuoteArg(prompt))
                : $"{args} {QuoteArg(prompt)}";

        // Profile flags lead so a prompt beginning with "-" still lands as the trailing
        // positional argument rather than being eaten as a flag value.
        if (!string.IsNullOrEmpty(extra))
            args = $"{extra} {args}";

        return $"{exe} {args}".Trim();
    }

    /// <summary>Command that continues the most recent session.</summary>
    public string BuildContinueCommand()
    {
        var p = Active;
        var exe = QuoteExe(p.Exe);
        return string.IsNullOrWhiteSpace(p.ContinueArgs) ? exe : $"{exe} {p.ContinueArgs.Trim()}";
    }

    /// <summary>Command that resumes a specific session id. Falls back to continue when unsupported.</summary>
    public string BuildResumeCommand(string sessionId)
    {
        var p = Active;
        if (string.IsNullOrWhiteSpace(p.ResumeArgs))
            return BuildContinueCommand();

        var exe = QuoteExe(p.Exe);
        var args = p.ResumeArgs.Replace("{sessionId}", sessionId).Trim();
        return $"{exe} {args}".Trim();
    }

    private static string QuoteExe(string exe)
    {
        exe = (exe ?? "").Trim();
        if (exe.Length == 0) return "";
        if (exe.StartsWith("\"")) return exe;
        return exe.Contains(' ') ? $"\"{exe}\"" : exe;
    }

    /// <summary>
    /// Wraps a value in double quotes so cmd.exe treats &amp;, |, &gt; and friends literally.
    /// Embedded quotes are doubled, which keeps cmd's quote parity intact and is what the
    /// C runtime argument parser expects for a literal quote.
    /// </summary>
    private static string QuoteArg(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private static string SanitizePrompt(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return "";
        // Newlines would terminate the command line mid-way.
        return prompt.Replace("\r", " ").Replace("\n", " ").Trim();
    }

    // ── Executable detection ──

    /// <summary>Re-resolves every provider's executable against the current PATH.</summary>
    public void ResolveExecutables()
    {
        foreach (var p in _providers)
        {
            var resolved = ResolveExecutable(p.Exe);
            if (resolved != p.ResolvedPath)
            {
                p.ResolvedPath = resolved;
                p.Version = "";
            }
        }
    }

    /// <summary>
    /// Reads `--version` from every installed provider in parallel. Runs off the UI thread;
    /// <paramref name="onProviderUpdated"/> fires once per provider as results arrive.
    /// </summary>
    public async Task DetectVersionsAsync(Action<CliProvider>? onProviderUpdated = null)
    {
        var targets = _providers.Where(p => p.IsInstalled && string.IsNullOrEmpty(p.Version)).ToList();
        if (targets.Count == 0) return;

        await Task.WhenAll(targets.Select(async p =>
        {
            var version = await ReadVersionAsync(p.ResolvedPath!);
            p.Version = version;
            onProviderUpdated?.Invoke(p);
        }));
    }

    private static async Task<string> ReadVersionAsync(string resolvedPath)
    {
        try
        {
            // Routed through cmd.exe so .cmd/.bat shims (npm installs) work the same as .exe.
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/s /c \"\"{resolvedPath}\" --version\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null) return "";

            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(VersionTimeoutMs);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
                return "";
            }

            var text = await stdout;
            if (string.IsNullOrWhiteSpace(text))
                text = await stderr;

            return ParseVersion(text);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Pulls the version number out of a --version line.
    /// "2.1.232 (Claude Code)" → "2.1.232", "codex-cli 0.144.5" → "0.144.5".
    /// </summary>
    internal static string ParseVersion(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return "";

        var line = output
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0);
        if (line == null) return "";

        var match = VersionRegex.Match(line);
        // Copilot prints "GitHub Copilot CLI 1.0.80." — drop the sentence-ending punctuation
        return match.Success ? match.Value.TrimEnd('.', '-') : "";
    }

    /// <summary>Mirrors how cmd.exe resolves a bare command name against PATH + PATHEXT.</summary>
    private static string? ResolveExecutable(string exe)
    {
        exe = (exe ?? "").Trim().Trim('"');
        if (exe.Length == 0) return null;

        var extensions = CandidateExtensions(exe);

        try
        {
            if (Path.IsPathRooted(exe) ||
                exe.Contains(Path.DirectorySeparatorChar) ||
                exe.Contains(Path.AltDirectorySeparatorChar))
            {
                var full = Path.GetFullPath(exe);
                foreach (var ext in extensions)
                {
                    if (File.Exists(full + ext)) return full + ext;
                }
                return null;
            }
        }
        catch
        {
            return null;
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var raw in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = raw.Trim().Trim('"');
            if (dir.Length == 0) continue;

            foreach (var ext in extensions)
            {
                try
                {
                    var candidate = Path.Combine(dir, exe + ext);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
        }

        return null;
    }

    private static List<string> CandidateExtensions(string exe)
    {
        var list = new List<string>();

        // An explicit extension is tried as-is first.
        if (Path.HasExtension(exe))
            list.Add("");

        var pathext = Environment.GetEnvironmentVariable("PATHEXT");
        if (string.IsNullOrWhiteSpace(pathext))
            pathext = ".COM;.EXE;.BAT;.CMD";

        foreach (var ext in pathext.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var e = ext.Trim();
            if (e.Length == 0) continue;
            if (!e.StartsWith(".")) e = "." + e;
            list.Add(e);
        }

        return list;
    }

    // ── Persistence ──

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var file = new CliProviderFile { Providers = _providers };
            var json = JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ProvidersFile, json);
        }
        catch { }
    }

    /// <summary>Resets one provider back to its built-in preset. Returns false for unknown ids.</summary>
    public bool RestoreDefaults(string id)
    {
        var preset = BuildPresets().FirstOrDefault(p => p.Id == id);
        var index = _providers.FindIndex(p => p.Id == id);
        if (preset == null || index < 0) return false;

        _providers[index] = preset;
        ResolveExecutables();
        Save();
        return true;
    }

    private static List<CliProvider> Load()
    {
        try
        {
            if (File.Exists(ProvidersFile))
            {
                var json = File.ReadAllText(ProvidersFile);
                var file = JsonSerializer.Deserialize<CliProviderFile>(json);
                if (file?.Providers is { Count: > 0 })
                {
                    var merged = MergeWithPresets(file.Providers, out var changed);
                    // Without writing back, a retired entry would be re-dropped on every launch.
                    if (changed) Write(merged);
                    return merged;
                }
            }
        }
        catch { }

        var presets = BuildPresets();
        Write(presets);
        return presets;
    }

    private static void Write(List<CliProvider> providers)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(new CliProviderFile { Providers = providers },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ProvidersFile, json);
        }
        catch { }
    }

    /// <summary>
    /// Keeps user edits but adds presets introduced by newer app versions, so an existing
    /// providers.json never hides a newly supported CLI.
    /// </summary>
    private static List<CliProvider> MergeWithPresets(List<CliProvider> stored, out bool changed)
    {
        var presets = BuildPresets();
        var result = new List<CliProvider>();
        changed = false;

        foreach (var stale in stored)
        {
            if (string.IsNullOrWhiteSpace(stale.Id))
            {
                changed = true;
                continue;
            }

            // A retired CLI is swapped for its successor in place, so the list keeps its order.
            if (RetiredIds.TryGetValue(stale.Id, out var successorId))
            {
                changed = true;
                var successor = presets.FirstOrDefault(p => p.Id == successorId);
                if (successor != null && !result.Any(p => p.Id == successorId))
                    result.Add(successor);
                continue;
            }

            // Launch profiles arrived after providers.json first shipped. Backfill them so an
            // existing install gets the presets rather than an empty picker; a user who has
            // already customised or removed them keeps their list untouched.
            if (stale.Profiles is not { Count: > 0 })
            {
                var match = presets.FirstOrDefault(p => p.Id == stale.Id);
                if (match != null && match.Profiles.Count > 0)
                {
                    stale.Profiles = match.Profiles.Select(x => x.Clone()).ToList();
                    changed = true;
                }
            }

            result.Add(stale);
        }

        foreach (var preset in presets)
        {
            if (!result.Any(p => p.Id == preset.Id))
            {
                result.Add(preset);
                changed = true;
            }
        }
        return result;
    }

    private static List<CliProvider> BuildPresets() => new()
    {
        new CliProvider
        {
            Id = ClaudeId,
            Name = "Claude Code",
            Exe = "claude",
            NewArgs = "{prompt}",
            ContinueArgs = "-c",
            ResumeArgs = "-r {sessionId}",
            ConfigDir = ".claude",
            InstallHint = "npm i -g @anthropic-ai/claude-code",
            Features = new CliFeatures
            {
                SessionList = true,
                ChatView = true,
                UsageTracker = true,
                PermissionOverlay = true,
                CompactButton = true,
                ModeSwitchButton = true,
                DiagramViewer = true,
                ExitCommand = "/exit\r",
            },
            // Re-reading the conversation prefix (cache_read) is the bulk of a session's cost,
            // so these presets exist to bound context length and trim the fixed prefix.
            // Deliberately not using --bare: it reads auth only from ANTHROPIC_API_KEY or an
            // apiKeyHelper, never OAuth or the keychain, so it breaks Pro/Max sign-in.
            Profiles = new List<LaunchProfile>
            {
                new()
                {
                    Id = LightProfileId,
                    Name = "Light",
                    ExtraArgs = "--model sonnet --effort low --autocompact 100k --strict-mcp-config --disable-slash-commands",
                    Description = "ProfileLightDesc",
                },
                new()
                {
                    Id = StandardProfileId,
                    Name = "Standard",
                    ExtraArgs = "--autocompact 200k --exclude-dynamic-system-prompt-sections",
                    Description = "ProfileStandardDesc",
                },
                new()
                {
                    Id = DeepProfileId,
                    Name = "Deep",
                    ExtraArgs = "--model opus --effort high",
                    Description = "ProfileDeepDesc",
                },
            },
        },
        new CliProvider
        {
            Id = "antigravity",
            Name = "Antigravity CLI",
            Exe = "agy",
            // Successor to Gemini CLI. -i runs the prompt then stays interactive (-p would
            // print once and exit); -c picks up the most recent conversation.
            NewArgs = "-i {prompt}",
            ContinueArgs = "-c",
            ResumeArgs = "--conversation {sessionId}",
            ConfigDir = @".gemini\antigravity-cli",
            InstallHint = "irm https://antigravity.google/cli/install.ps1 | iex",
            Features = new CliFeatures(),
        },
        new CliProvider
        {
            Id = "codex",
            Name = "Codex CLI",
            Exe = "codex",
            NewArgs = "{prompt}",
            ContinueArgs = "resume --last",
            ResumeArgs = "resume {sessionId}",
            ConfigDir = ".codex",
            InstallHint = "npm i -g @openai/codex",
            Features = new CliFeatures(),
        },
        new CliProvider
        {
            Id = "copilot",
            Name = "GitHub Copilot CLI",
            Exe = "copilot",
            NewArgs = "-i {prompt}",
            ContinueArgs = "--continue",
            ResumeArgs = "--resume={sessionId}",
            ConfigDir = ".copilot",
            InstallHint = "npm i -g @github/copilot",
            Features = new CliFeatures(),
        },
    };
}
