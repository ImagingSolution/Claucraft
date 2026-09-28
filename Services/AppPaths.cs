using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Snipyard.Services;

/// <summary>
/// The folders the app keeps its own files in. The app was called Claucraft until it became
/// Snipyard, and an install that predates the rename still has everything under the old
/// %APPDATA%\Claucraft: the first start after the update copies that folder across once, so
/// settings, snippets, providers, schedules and checkpoints carry over.
///
/// The old folder is copied, not moved. Going back to an older build then still finds its
/// settings where it left them, and a copy that fails half way loses nothing.
/// </summary>
public static class AppPaths
{
    public const string AppName = "Snipyard";

    /// <summary>The name the app had before the rename, still on disk for existing installs.</summary>
    private const string LegacyName = "Claucraft";

    private static readonly string RoamingBase = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string LocalBase = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>%APPDATA%\Snipyard - settings, snippets, providers, schedules, checkpoints.</summary>
    public static string Roaming { get; } = PrepareRoaming();

    /// <summary>
    /// %LOCALAPPDATA%\Snipyard - caches that are cheap to rebuild (WebView2, rate limits), so
    /// nothing is carried over from the old name.
    /// </summary>
    public static string Local { get; } = Path.Combine(LocalBase, AppName);

    /// <summary>
    /// %LOCALAPPDATA%\Claucraft. Worktrees made before the rename are registered in their
    /// repositories at this path and may hold uncommitted work, so they stay where they are.
    /// </summary>
    public static string LegacyLocal { get; } = Path.Combine(LocalBase, LegacyName);

    /// <summary>Scratch files (pasted images, rendered diagrams) under %TEMP%.</summary>
    public static string Temp => Path.Combine(Path.GetTempPath(), AppName);

    private static string PrepareRoaming()
    {
        var target = Path.Combine(RoamingBase, AppName);
        var legacy = Path.Combine(RoamingBase, LegacyName);
        if (string.Equals(target, legacy, StringComparison.OrdinalIgnoreCase)
            || Directory.Exists(target) || !Directory.Exists(legacy))
            return target;

        // Built beside the target and renamed into place in one step, so a crash mid-copy
        // leaves no half-filled folder that the next start would mistake for a finished one.
        // The status line relay runs as its own process and can race the app here; whichever
        // rename loses just throws its copy away.
        var staging = target + ".migrating-" + Environment.ProcessId;
        try
        {
            CopyDirectory(legacy, staging);
            RewriteStoredPaths(staging, legacy, target);
            Directory.Move(staging, target);
            return target;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[AppPaths] migration from {legacy} failed: {ex.Message}");
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
            // Keep running on the old folder rather than on empty defaults; the next start
            // tries the copy again.
            return Directory.Exists(target) ? target : legacy;
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(dir);
            // Help is re-extracted from the exe every time it is opened.
            if (string.Equals(name, "Help", StringComparison.OrdinalIgnoreCase)) continue;
            CopyDirectory(dir, Path.Combine(dest, name));
        }
    }

    /// <summary>
    /// Some stored values are absolute paths into the old folder: the Light profile's
    /// --settings file in providers.json, and snapshot folders in the checkpoint index. Left
    /// alone they would keep reading from, and deleting in, the old folder. Only the files the
    /// app writes itself are touched - a checkpoint snapshot is a copy of someone's project and
    /// is never rewritten.
    /// </summary>
    private static void RewriteStoredPaths(string folder, string oldRoot, string newRoot)
    {
        var files = new List<string>(Directory.GetFiles(folder, "*.json"));
        var index = Path.Combine(folder, "checkpoints", "index.json");
        if (File.Exists(index)) files.Add(index);

        // The same path as System.Text.Json writes it (non-ASCII as \uXXXX) and as a relaxed
        // encoder would; a user name in Japanese differs between the two.
        var pairs = new List<(string Old, string New)>();
        foreach (var encoder in new JavaScriptEncoder?[] { null, JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
        {
            var options = new JsonSerializerOptions { Encoder = encoder };
            var o = JsonSerializer.Serialize(oldRoot + Path.DirectorySeparatorChar, options).Trim('"');
            var n = JsonSerializer.Serialize(newRoot + Path.DirectorySeparatorChar, options).Trim('"');
            if (!pairs.Contains((o, n))) pairs.Add((o, n));
        }

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var updated = text;
            foreach (var (o, n) in pairs)
                updated = updated.Replace(o, n, StringComparison.OrdinalIgnoreCase);
            if (updated != text)
                File.WriteAllText(file, updated);
        }
    }
}
