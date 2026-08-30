using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Claucraft.Services;

/// <summary>
/// The list of files under a project folder, for offering as @ completions. Paths come back
/// relative to the folder and with '/' separators, which is how they have to be typed at the
/// CLI anyway.
///
/// The walk is done once per folder and held for a short while: someone typing an @ expects the
/// list now, and re-walking a large repository on every keystroke would not deliver that.
/// </summary>
public static class ProjectFileIndex
{
    /// <summary>Long enough that a burst of typing walks once, short enough to notice new files.</summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);

    /// <summary>A cap, so a home directory picked by mistake cannot walk forever.</summary>
    private const int MaxFiles = 20000;

    /// <summary>
    /// Folders never worth offering: version control internals, dependency caches and build
    /// output. They hold most of the files in a typical project and none of the interesting ones.
    /// </summary>
    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", ".vs", ".vscode", ".idea", ".gradle",
        "node_modules", "bower_components", "vendor", "packages",
        "bin", "obj", "dist", "build", "out", "target",
        "__pycache__", ".venv", "venv", ".tox", ".mypy_cache", ".pytest_cache",
        ".next", ".nuxt", ".cache", "coverage",
    };

    private static readonly object Gate = new();
    private static string _cachedRoot = "";
    private static List<string> _cached = new();
    private static DateTime _cachedAt = DateTime.MinValue;

    /// <summary>Files under <paramref name="root"/>, relative and '/'-separated.</summary>
    public static Task<List<string>> ListAsync(string? root)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            return Task.FromResult(new List<string>());

        lock (Gate)
        {
            if (string.Equals(_cachedRoot, root, StringComparison.OrdinalIgnoreCase)
                && DateTime.UtcNow - _cachedAt < CacheLifetime)
            {
                return Task.FromResult(_cached);
            }
        }

        return Task.Run(() =>
        {
            var files = Walk(root);
            lock (Gate)
            {
                _cachedRoot = root;
                _cached = files;
                _cachedAt = DateTime.UtcNow;
            }
            return files;
        });
    }

    /// <summary>Drops the cache, so the next @ sees a folder that has just been switched to.</summary>
    public static void Invalidate()
    {
        lock (Gate) { _cachedAt = DateTime.MinValue; }
    }

    /// <summary>
    /// The best matches for what has been typed after the @, best first. A file whose name
    /// starts with the query beats one that merely contains it, which beats a match anywhere
    /// else in the path - so typing "main" reaches MainWindow.axaml.cs before
    /// Services/DomainThing.cs.
    /// </summary>
    public static List<string> Rank(IReadOnlyList<string> files, string query, int limit)
    {
        if (files.Count == 0) return new List<string>();

        if (string.IsNullOrEmpty(query))
            return files.Take(limit).ToList();

        var scored = new List<(int Score, int Length, string Path)>();
        foreach (var path in files)
        {
            int slash = path.LastIndexOf('/');
            var name = slash >= 0 ? path[(slash + 1)..] : path;

            int score;
            if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) score = 0;
            else if (name.Contains(query, StringComparison.OrdinalIgnoreCase)) score = 1;
            else if (path.Contains(query, StringComparison.OrdinalIgnoreCase)) score = 2;
            else continue;

            scored.Add((score, path.Length, path));
        }

        // Shorter paths first within a tier: the file at the top of the tree is more often the
        // one meant than a namesake buried several folders down.
        scored.Sort((a, b) =>
        {
            int c = a.Score.CompareTo(b.Score);
            if (c != 0) return c;
            c = a.Length.CompareTo(b.Length);
            if (c != 0) return c;
            return string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
        });

        return scored.Take(limit).Select(s => s.Path).ToList();
    }

    private static List<string> Walk(string root)
    {
        var results = new List<string>();
        var pending = new Queue<string>();
        pending.Enqueue(root);

        int rootLength = root.TrimEnd(Path.DirectorySeparatorChar).Length + 1;

        while (pending.Count > 0 && results.Count < MaxFiles)
        {
            var dir = pending.Dequeue();

            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    if (results.Count >= MaxFiles) break;
                    if (file.Length <= rootLength) continue;
                    results.Add(file[rootLength..].Replace('\\', '/'));
                }
            }
            catch
            {
                // A folder that cannot be read is skipped rather than aborting the walk.
            }

            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    if (SkipDirectories.Contains(name)) continue;

                    // Other dot-folders are configuration the user rarely means to reference,
                    // but .claude is the exception - its commands and skills are worth reaching.
                    if (name.StartsWith('.') && !name.Equals(".claude", StringComparison.OrdinalIgnoreCase))
                        continue;

                    pending.Enqueue(sub);
                }
            }
            catch
            {
                // Same: an unreadable folder just contributes nothing.
            }
        }

        return results;
    }
}
