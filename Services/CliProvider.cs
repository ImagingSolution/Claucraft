using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Claucraft.Services;

/// <summary>
/// Feature flags describing which Claude Code specific integrations a CLI supports.
/// Anything false is hidden from the UI and disabled at runtime.
/// </summary>
public class CliFeatures
{
    public bool SessionList { get; set; }
    public bool ChatView { get; set; }
    public bool UsageTracker { get; set; }
    public bool PermissionOverlay { get; set; }
    public bool CompactButton { get; set; }
    public bool ModeSwitchButton { get; set; }
    public bool DiagramViewer { get; set; }

    /// <summary>Command sent to the PTY on shutdown. Empty means kill the process directly.</summary>
    public string ExitCommand { get; set; } = "";

    public CliFeatures Clone() => new()
    {
        SessionList = SessionList,
        ChatView = ChatView,
        UsageTracker = UsageTracker,
        PermissionOverlay = PermissionOverlay,
        CompactButton = CompactButton,
        ModeSwitchButton = ModeSwitchButton,
        DiagramViewer = DiagramViewer,
        ExitCommand = ExitCommand,
    };
}

/// <summary>
/// A named set of extra command-line flags applied when starting a new session.
/// Exists to cap how much context a session accumulates: cache_read of the conversation
/// prefix dominates the bill, so the flags that bound context length (--autocompact) and
/// trim the fixed prefix (--strict-mcp-config, --disable-slash-commands) are the levers.
/// Serialized with the provider so users can edit them without a rebuild.
/// </summary>
public class LaunchProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Flags <c>CliProviderService</c> inserts ahead of the provider arguments on every launch path - new, continue and resume alike.</summary>
    public string ExtraArgs { get; set; } = "";

    /// <summary>Localization key describing when to pick this profile, or literal text.</summary>
    public string Description { get; set; } = "";

    public LaunchProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        ExtraArgs = ExtraArgs,
        Description = Description,
    };
}

/// <summary>
/// One AI CLI that Claucraft can drive (Claude Code, Antigravity CLI, Codex CLI, ...).
/// Serialized to %AppData%\Claucraft\providers.json so users can adjust arguments
/// without a rebuild when a CLI changes its flags.
/// </summary>
public class CliProvider
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Executable name resolved through PATH, or an absolute path.</summary>
    public string Exe { get; set; } = "";

    /// <summary>Args used only when an initial prompt is set. Supports the {prompt} placeholder.</summary>
    public string NewArgs { get; set; } = "";

    /// <summary>Args that continue the most recent session.</summary>
    public string ContinueArgs { get; set; } = "";

    /// <summary>Args that resume a specific session. Supports the {sessionId} placeholder.</summary>
    public string ResumeArgs { get; set; } = "";

    /// <summary>
    /// Args that run one prompt without a session and print the answer, used for the commit
    /// message draft. Without {prompt} the prompt is written to stdin, which is what a .cmd
    /// shim needs - cmd.exe rebuilds the command line and would mangle a long prompt on it.
    /// Empty means the CLI has no one-shot mode here and the draft button stays off.
    /// </summary>
    public string OneShotArgs { get; set; } = "";

    /// <summary>Config directory name under the user profile, e.g. ".claude".</summary>
    public string ConfigDir { get; set; } = "";

    public string InstallHint { get; set; } = "";

    public CliFeatures Features { get; set; } = new();

    /// <summary>
    /// Launch presets offered next to the New Session button. Empty means the CLI has none
    /// and the picker stays hidden.
    /// </summary>
    public List<LaunchProfile> Profiles { get; set; } = new();

    // ── Runtime state (not persisted) ──

    /// <summary>Full path the executable resolved to, or null when not installed.</summary>
    [JsonIgnore]
    public string? ResolvedPath { get; set; }

    /// <summary>Version string parsed from `{exe} --version`, empty until detected.</summary>
    [JsonIgnore]
    public string Version { get; set; } = "";

    [JsonIgnore]
    public bool IsInstalled => !string.IsNullOrEmpty(ResolvedPath);

    /// <summary>Name plus version when known, e.g. "Claude Code 2.1.232".</summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrEmpty(Version) ? Name : $"{Name} {Version}";

    public CliProvider Clone() => new()
    {
        Id = Id,
        Name = Name,
        Exe = Exe,
        NewArgs = NewArgs,
        ContinueArgs = ContinueArgs,
        ResumeArgs = ResumeArgs,
        OneShotArgs = OneShotArgs,
        ConfigDir = ConfigDir,
        InstallHint = InstallHint,
        Features = Features.Clone(),
        Profiles = Profiles.Select(p => p.Clone()).ToList(),
    };
}

/// <summary>Root object persisted to providers.json.</summary>
public class CliProviderFile
{
    public List<CliProvider> Providers { get; set; } = new();
}
