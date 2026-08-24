using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Claucraft.Services;

/// <summary>
/// One work-tree snapshot taken right before a prompt is sent, so the user can undo whatever
/// the AI is about to do. IsGit tells restore/cleanup which kind of Payload it is holding:
/// a dangling commit SHA for a git repo, or a snapshot folder path otherwise.
/// </summary>
public record Checkpoint(
    string Id,
    string ProjectFolder,
    DateTime CreatedAt,
    string Label,
    bool IsGit,
    string Payload
);

/// <summary>
/// Takes a snapshot of a project's working tree before each prompt and restores it on demand.
/// Git repos use "stash create", which builds a commit object without touching the working
/// tree or the stash list, so capture never interferes with what the user is doing. Non-git
/// folders fall back to a plain recursive file copy under %AppData%\Claucraft\checkpoints.
/// </summary>
public class CheckpointService
{
    public int MaxPerProject { get; set; } = 20;

    private readonly List<Checkpoint> _checkpoints = new();

    private static readonly string RootDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Claucraft", "checkpoints");

    private static readonly string IndexFile = Path.Combine(RootDir, "index.json");

    private const long MaxSnapshotBytes = 50L * 1024 * 1024;
    private const int MaxSnapshotFiles = 2000;
    private const int GitTimeoutMs = 10000;

    private static readonly HashSet<string> ExcludedDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".venv", "__pycache__", "dist", "target",
    };

    public CheckpointService()
    {
        Load();
    }

    // ── Public API ──

    /// <summary>Takes a snapshot of the working tree. Returns null when nothing was captured.</summary>
    public async Task<Checkpoint?> CreateAsync(string projectFolder, string promptLabel)
    {
        if (string.IsNullOrWhiteSpace(projectFolder) || !Directory.Exists(projectFolder))
            return null;

        return await Task.Run(() => CreateCore(projectFolder, promptLabel));
    }

    /// <summary>Restores the working tree to the given checkpoint. Returns an error message, or null on success.</summary>
    public async Task<string?> RestoreAsync(Checkpoint checkpoint)
    {
        return await Task.Run(() => RestoreCore(checkpoint));
    }

    public IReadOnlyList<Checkpoint> ForProject(string projectFolder) =>
        _checkpoints
            .Where(c => string.Equals(c.ProjectFolder, projectFolder, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.CreatedAt)
            .ToList();

    public Checkpoint? LatestFor(string projectFolder) => ForProject(projectFolder).FirstOrDefault();

    public void Load()
    {
        _checkpoints.Clear();
        try
        {
            if (File.Exists(IndexFile))
            {
                var json = File.ReadAllText(IndexFile);
                var loaded = JsonSerializer.Deserialize<List<Checkpoint>>(json);
                if (loaded != null) _checkpoints.AddRange(loaded);
            }
        }
        catch { }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(RootDir);
            var json = JsonSerializer.Serialize(_checkpoints, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(IndexFile, json);
        }
        catch { }
    }

    // ── Create ──

    private Checkpoint? CreateCore(string projectFolder, string promptLabel)
    {
        try
        {
            var label = BuildLabel(promptLabel);
            var gitRoot = FindGitRoot(projectFolder);

            var checkpoint = gitRoot != null
                ? CreateGitCheckpoint(projectFolder, gitRoot, label)
                : CreateFileCheckpoint(projectFolder, label);

            if (checkpoint == null) return null;

            // Prompts that change nothing produce an identical snapshot (git returns the same
            // SHA). Keeping only the first stops a run of y/n answers from evicting the
            // checkpoints that actually matter.
            var duplicate = _checkpoints.FirstOrDefault(c =>
                c.IsGit == checkpoint.IsGit
                && c.Payload == checkpoint.Payload
                && string.Equals(c.ProjectFolder, projectFolder, StringComparison.OrdinalIgnoreCase));
            if (duplicate != null)
            {
                DeleteCheckpointData(checkpoint);
                return null;
            }

            _checkpoints.Add(checkpoint);
            TrimOldCheckpoints(projectFolder);
            Save();
            return checkpoint;
        }
        catch
        {
            return null;
        }
    }

    private static Checkpoint? CreateGitCheckpoint(string projectFolder, string gitRoot, string label)
    {
        // "stash create" builds the commit object only - unlike "stash push" it never touches
        // the index or the working tree, so this is safe to run silently before every prompt.
        var (exitCode, stdout, _) = RunGit(gitRoot, "stash create");
        var sha = stdout.Trim();
        if (exitCode != 0 || string.IsNullOrEmpty(sha))
            return null; // nothing to snapshot (working tree is clean)

        var id = Guid.NewGuid().ToString();

        // The commit "stash create" makes is dangling (no ref points to it), so without this
        // it would be swept away by the next "git gc".
        RunGit(gitRoot, $"update-ref refs/claucraft/checkpoints/{id} {sha}");

        return new Checkpoint(id, projectFolder, DateTime.Now, label, true, sha);
    }

    private static Checkpoint? CreateFileCheckpoint(string projectFolder, string label)
    {
        var id = Guid.NewGuid().ToString();
        var snapshotDir = Path.Combine(RootDir, HashFolder(projectFolder), id);

        try
        {
            Directory.CreateDirectory(snapshotDir);

            long totalBytes = 0;
            int totalFiles = 0;
            var completed = CopySnapshot(projectFolder, snapshotDir, ref totalBytes, ref totalFiles);

            if (!completed || totalFiles == 0)
            {
                // Either the folder is too large to snapshot or there was nothing to copy;
                // either way a half-written snapshot must not linger on disk.
                TryDeleteDirectory(snapshotDir);
                return null;
            }

            return new Checkpoint(id, projectFolder, DateTime.Now, label, false, snapshotDir);
        }
        catch
        {
            TryDeleteDirectory(snapshotDir);
            return null;
        }
    }

    /// <summary>Recursively copies sourceDir into destDir, skipping excluded folders. Returns false once the size/count cap is hit.</summary>
    private static bool CopySnapshot(string sourceDir, string destDir, ref long totalBytes, ref int totalFiles)
    {
        Directory.CreateDirectory(destDir);

        foreach (var filePath in Directory.EnumerateFiles(sourceDir))
        {
            long length;
            try { length = new FileInfo(filePath).Length; }
            catch { continue; }

            totalBytes += length;
            totalFiles++;
            if (totalBytes > MaxSnapshotBytes || totalFiles > MaxSnapshotFiles)
                return false;

            try { File.Copy(filePath, Path.Combine(destDir, Path.GetFileName(filePath)), overwrite: true); }
            catch { } // unreadable file (lock, permissions, ...) - skip it, don't abort the whole snapshot
        }

        foreach (var subDir in Directory.EnumerateDirectories(sourceDir))
        {
            var name = Path.GetFileName(subDir);
            if (ExcludedDirNames.Contains(name)) continue;

            if (!CopySnapshot(subDir, Path.Combine(destDir, name), ref totalBytes, ref totalFiles))
                return false;
        }

        return true;
    }

    // ── Restore ──

    private static string? RestoreCore(Checkpoint checkpoint)
    {
        return checkpoint.IsGit ? RestoreGitCheckpoint(checkpoint) : RestoreFileCheckpoint(checkpoint);
    }

    private static string? RestoreGitCheckpoint(Checkpoint checkpoint)
    {
        var gitRoot = FindGitRoot(checkpoint.ProjectFolder) ?? checkpoint.ProjectFolder;

        var (applyExit, _, applyErr) = RunGit(gitRoot, $"stash apply {checkpoint.Payload}");
        if (applyExit == 0) return null;

        // "stash apply" fails on conflicts against the current working tree; fall back to a
        // hard overwrite from the checkpoint commit, which always succeeds if the SHA exists.
        var (checkoutExit, _, checkoutErr) = RunGit(gitRoot, $"checkout {checkpoint.Payload} -- .");
        if (checkoutExit == 0) return null;

        var detail = string.IsNullOrWhiteSpace(checkoutErr) ? applyErr : checkoutErr;
        return $"Restore failed: {detail.Trim()}";
    }

    private static string? RestoreFileCheckpoint(Checkpoint checkpoint)
    {
        if (!Directory.Exists(checkpoint.Payload))
            return "Checkpoint snapshot folder no longer exists.";

        try
        {
            // Overwrite-only: files present in the project but absent from the snapshot are
            // left alone, so restoring never deletes work the snapshot never knew about.
            CopyOverwrite(checkpoint.Payload, checkpoint.ProjectFolder);
            return null;
        }
        catch (Exception ex)
        {
            return $"Restore failed: {ex.Message}";
        }
    }

    private static void CopyOverwrite(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var filePath in Directory.EnumerateFiles(sourceDir))
        {
            try { File.Copy(filePath, Path.Combine(destDir, Path.GetFileName(filePath)), overwrite: true); }
            catch { }
        }

        foreach (var subDir in Directory.EnumerateDirectories(sourceDir))
            CopyOverwrite(subDir, Path.Combine(destDir, Path.GetFileName(subDir)));
    }

    // ── Housekeeping ──

    private void TrimOldCheckpoints(string projectFolder)
    {
        var forProject = ForProject(projectFolder);
        if (forProject.Count <= MaxPerProject) return;

        foreach (var stale in forProject.Skip(MaxPerProject))
        {
            DeleteCheckpointData(stale);
            _checkpoints.Remove(stale);
        }
    }

    private static void DeleteCheckpointData(Checkpoint checkpoint)
    {
        if (checkpoint.IsGit)
        {
            var gitRoot = FindGitRoot(checkpoint.ProjectFolder) ?? checkpoint.ProjectFolder;
            RunGit(gitRoot, $"update-ref -d refs/claucraft/checkpoints/{checkpoint.Id}");
        }
        else
        {
            TryDeleteDirectory(checkpoint.Payload);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch { }
    }

    // ── Helpers ──

    private static string BuildLabel(string promptLabel)
    {
        if (string.IsNullOrWhiteSpace(promptLabel)) return "(no prompt)";
        var trimmed = promptLabel.Trim();
        return trimmed.Length <= 60 ? trimmed : trimmed[..60];
    }

    /// <summary>Walks up from folder looking for a .git directory (or worktree .git file).</summary>
    private static string? FindGitRoot(string folder)
    {
        try
        {
            var dir = new DirectoryInfo(folder);
            while (dir != null)
            {
                var gitPath = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(gitPath) || File.Exists(gitPath))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }
        catch { }
        return null;
    }

    private static string HashFolder(string projectFolder)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(projectFolder.ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..16];
    }

    private static (int ExitCode, string StdOut, string StdErr) RunGit(string workingDirectory, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null) return (-1, "", "");

            // Read both streams concurrently before blocking on exit, otherwise a chatty
            // stderr/stdout can deadlock the process once its pipe buffer fills up.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(GitTimeoutMs))
            {
                try { proc.Kill(true); } catch { }
                return (-1, "", "git command timed out");
            }

            return (proc.ExitCode, stdoutTask.Result, stderrTask.Result);
        }
        catch
        {
            return (-1, "", "");
        }
    }
}
