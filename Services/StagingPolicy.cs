using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Claucraft.Services;

/// <summary>Why a file is one the user probably did not mean to commit.</summary>
public enum StagingRiskKind
{
    /// <summary>A key, certificate or credential file. Its name alone gives it away.</summary>
    Credential,

    /// <summary>Settings that belong to one machine or one IDE, not to the repository.</summary>
    LocalSetting,

    /// <summary>Something a build produced, or something a package manager downloaded.</summary>
    BuildOutput,

    /// <summary>Big enough that committing it weighs on the repository forever.</summary>
    LargeFile,
}

/// <summary>One flagged file, with the reason already worded for the dialog.</summary>
public sealed record StagingRisk(string Path, StagingRiskKind Kind, string Reason);

/// <summary>
/// Names the files that are usually a mistake to commit - a key, a machine-local setting, a build
/// output, something huge - by looking only at the path and the size.
///
/// This is the counterpart to <see cref="SecretScanService"/>, not a replacement for it: that one
/// reads the diff and needs an AI to judge the content, this one needs neither, because the danger
/// is the file itself. A .pfx is no safer for being unreadable, and a launchSettings.json full of
/// one machine's paths carries nothing an AI would call a secret.
///
/// Everything here is a warning, never a block. Real repositories commit odd things on purpose,
/// so the patterns stay narrow: a name that is merely plausible as source code is left alone, and
/// anything that is not actually on disk is passed over, so deleting a committed secret - the very
/// thing this would want the user to do - never trips a warning of its own.
/// </summary>
public static class StagingPolicy
{
    /// <summary>Beyond this, a file is worth a second thought no matter what it holds.</summary>
    public const long LargeFileBytes = 5L * 1024 * 1024;

    /// <summary>How many files the dialog names before it starts counting instead.</summary>
    private const int MaxListed = 10;

    /// <summary>
    /// How far into one collapsed folder to look before giving up. A folder worth warning about
    /// usually gives itself away in its first handful of files, and the answer is needed while the
    /// user waits for a click, so this stops well short of walking something like a node_modules.
    /// </summary>
    private const int MaxScanned = 500;

    // ── Patterns ───────────────────────────────────────────────────────

    /// <summary>Exact file names that are a credential wherever they appear.</summary>
    private static readonly HashSet<string> CredentialNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env", "credentials.json", ".credentials.json", ".npmrc", ".pypirc",
        "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519",
    };

    private static readonly HashSet<string> CredentialExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pem", ".key", ".pfx", ".p12", ".jks", ".keystore", ".snk", ".ppk",
    };

    /// <summary>Directory names that hold nothing but keys.</summary>
    private static readonly HashSet<string> CredentialDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ssh", ".gnupg",
    };

    private static readonly HashSet<string> LocalSettingNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "launchsettings.json",
    };

    private static readonly HashSet<string> LocalSettingExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".user", ".suo", ".pubxml",
    };

    /// <summary>Directory names that hold one editor's or one machine's own state.</summary>
    private static readonly HashSet<string> LocalSettingDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".vs", ".idea", ".vscode",
    };

    /// <summary>
    /// Directory names that hold build output or downloaded dependencies. Deliberately narrow:
    /// "build" and "packages" are left out because they are just as often hand-written source.
    /// </summary>
    private static readonly HashSet<string> BuildDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "publish", "dist", "out", "node_modules", "__pycache__", ".venv", "target",
    };

    private static readonly HashSet<string> BuildExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".pdb", ".pyc", ".class", ".o", ".obj", ".lib", ".so", ".dylib", ".nupkg",
    };

    // ── Inspection ─────────────────────────────────────────────────────

    /// <summary>
    /// The subset of <paramref name="paths"/> worth warning about, in the order they were given.
    /// An empty result is the normal case and means "say nothing".
    /// </summary>
    public static IReadOnlyList<StagingRisk> Inspect(string repoRoot, IEnumerable<string> paths)
    {
        if (string.IsNullOrEmpty(repoRoot) || paths == null) return Array.Empty<StagingRisk>();

        return paths.Select(p => InspectOne(repoRoot, p))
            .Where(r => r != null)
            .Select(r => r!)
            .ToList();
    }

    /// <summary>
    /// What is wrong with one path, or null when nothing is. A path that is not on disk - a
    /// deletion, or a file that moved on since git status ran - is never flagged.
    ///
    /// The path may name a folder rather than a file: "git status --porcelain" collapses a folder
    /// no commit has ever touched into a single "node_modules/" entry, and staging that one row
    /// stages everything under it. So a folder is inspected too - see
    /// <see cref="InspectDirectory"/>.
    /// </summary>
    internal static StagingRisk? InspectOne(string repoRoot, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var normalized = path.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0) return null;

        string full;
        long size;
        try
        {
            full = Path.Combine(repoRoot, normalized.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(full)) return InspectDirectory(repoRoot, normalized, full);

            var info = new FileInfo(full);
            if (!info.Exists) return null;
            size = info.Length;
        }
        catch
        {
            // A path this process cannot even measure is not one to warn about.
            return null;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var name = segments[^1];
        var extension = Path.GetExtension(name);

        // Only the segments before the last are folders; the last one is the file itself.
        int folders = segments.Length - 1;

        // Worst first, so a key that happens to sit in bin/ reads as a key, not as build output.
        if (HasDirectory(segments, folders, CredentialDirectories) || IsCredential(name, extension))
            return new(normalized, StagingRiskKind.Credential, Loc.Get("RiskyKindCredential"));

        if (HasDirectory(segments, folders, LocalSettingDirectories) || IsLocalSetting(name, extension))
            return new(normalized, StagingRiskKind.LocalSetting, Loc.Get("RiskyKindLocalSetting"));

        if (HasDirectory(segments, folders, BuildDirectories) || BuildExtensions.Contains(extension))
            return new(normalized, StagingRiskKind.BuildOutput, Loc.Get("RiskyKindBuildOutput"));

        if (size > LargeFileBytes)
            return new(normalized, StagingRiskKind.LargeFile,
                string.Format(CultureInfo.CurrentCulture, Loc.Get("RiskyKindLargeFileFmt"), Megabytes(size)));

        return null;
    }

    /// <summary>
    /// What is wrong with a whole folder. When the folder's own name settles it - node_modules,
    /// .ssh, .vs - that is the answer and nothing is read from disk. Otherwise the folder is worth
    /// exactly as much as the worst thing inside it, so the contents are walked and the worst find
    /// is reported by its own path: "Properties/launchSettings.json" tells the user far more than
    /// "Properties/" does.
    /// </summary>
    private static StagingRisk? InspectDirectory(string repoRoot, string normalized, string full)
    {
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Here the last segment is a folder like any other, so all of them count.
        if (HasDirectory(segments, segments.Length, CredentialDirectories))
            return new(normalized + "/", StagingRiskKind.Credential, Loc.Get("RiskyKindCredential"));

        if (HasDirectory(segments, segments.Length, LocalSettingDirectories))
            return new(normalized + "/", StagingRiskKind.LocalSetting, Loc.Get("RiskyKindLocalSetting"));

        if (HasDirectory(segments, segments.Length, BuildDirectories))
            return new(normalized + "/", StagingRiskKind.BuildOutput, Loc.Get("RiskyKindBuildOutput"));

        StagingRisk? worst = null;
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System,
            };

            foreach (var file in Directory.EnumerateFiles(full, "*", options).Take(MaxScanned))
            {
                var relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                var risk = InspectOne(repoRoot, relative);
                if (risk == null) continue;

                // The enum is declared worst-first, so a smaller Kind is the more serious find.
                if (worst == null || risk.Kind < worst.Kind) worst = risk;
                if (worst.Kind == StagingRiskKind.Credential) break;
            }
        }
        catch
        {
            // A folder that cannot be walked is judged on its name alone, which said nothing.
        }

        return worst;
    }

    private static bool IsCredential(string name, string extension) =>
        CredentialNames.Contains(name)
        || CredentialExtensions.Contains(extension)
        || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("secrets.", StringComparison.OrdinalIgnoreCase);

    private static bool IsLocalSetting(string name, string extension) =>
        LocalSettingNames.Contains(name)
        || LocalSettingExtensions.Contains(extension)
        || name.EndsWith(".local.json", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".DotSettings.user", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether any of the first <paramref name="count"/> segments is one of these folders.</summary>
    private static bool HasDirectory(string[] segments, int count, HashSet<string> names)
    {
        for (int i = 0; i < count; i++)
            if (names.Contains(segments[i])) return true;

        return false;
    }

    private static string Megabytes(long bytes) =>
        (bytes / 1024d / 1024d).ToString("0.#", CultureInfo.CurrentCulture) + " MB";

    // ── Output ─────────────────────────────────────────────────────────

    /// <summary>
    /// The flagged files as one block of text for the confirmation dialog, one file per line. The
    /// dialog is a fixed width, so a long list stops after <see cref="MaxListed"/> and says how
    /// many more there are.
    /// </summary>
    internal static string Describe(IReadOnlyList<StagingRisk> risks)
    {
        var sb = new StringBuilder();

        foreach (var risk in risks.Take(MaxListed))
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(risk.Path).Append(" - ").Append(risk.Reason);
        }

        int rest = risks.Count - MaxListed;
        if (rest > 0)
            sb.Append('\n').Append(string.Format(CultureInfo.CurrentCulture,
                Loc.Get("RiskyFilesMoreFmt"), rest));

        return sb.ToString();
    }
}
