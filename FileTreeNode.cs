using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Avalonia.Media;

namespace Claucraft;

public class FileTreeNode : INotifyPropertyChanged
{
    private static readonly Geometry FolderIcon = Geometry.Parse(
        "M2 6C2 4.89 2.89 4 4 4H9L11 6H18C19.1 6 20 6.89 20 8V16C20 17.1 19.1 18 18 18H4C2.89 18 2 17.1 2 16V6Z");

    private static readonly Geometry FileIcon = Geometry.Parse(
        "M14 2H6C4.9 2 4 2.9 4 4V20C4 21.1 4.9 22 6 22H18C19.1 22 20 21.1 20 20V8L14 2ZM18 20H6V4H13V9H18V20Z");

    private static readonly Dictionary<string, string> ExtColorMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".cs", "#9B6BDF" }, { ".csproj", "#9B6BDF" }, { ".sln", "#9B6BDF" },
        { ".json", "#CBCB41" }, { ".jsonc", "#CBCB41" },
        { ".xml", "#E37933" }, { ".axaml", "#E37933" }, { ".xaml", "#E37933" },
        { ".config", "#E37933" },
        { ".md", "#519ABA" }, { ".markdown", "#519ABA" },
        { ".txt", "#8C8C8C" }, { ".log", "#8C8C8C" },
        { ".js", "#CBCB41" }, { ".mjs", "#CBCB41" },
        { ".ts", "#3178C6" }, { ".tsx", "#3178C6" }, { ".jsx", "#CBCB41" },
        { ".html", "#E44D26" }, { ".htm", "#E44D26" },
        { ".css", "#563D7C" }, { ".scss", "#CD6799" }, { ".less", "#1D365D" },
        { ".py", "#3572A5" },
        { ".java", "#B07219" },
        { ".go", "#00ADD8" },
        { ".rs", "#DEA584" },
        { ".cpp", "#F34B7D" }, { ".c", "#555555" }, { ".h", "#555555" },
        { ".rb", "#CC342D" },
        { ".php", "#4F5D95" },
        { ".sh", "#89E051" }, { ".bash", "#89E051" }, { ".ps1", "#012456" },
        { ".yml", "#CB171E" }, { ".yaml", "#CB171E" },
        { ".toml", "#9C4121" },
        { ".gitignore", "#F54D27" }, { ".gitattributes", "#F54D27" },
        { ".dockerfile", "#384D54" },
        { ".sql", "#E38C00" },
        { ".svg", "#FFB13B" }, { ".png", "#A074C4" }, { ".jpg", "#A074C4" },
        { ".gif", "#A074C4" }, { ".ico", "#A074C4" },
        { ".manifest", "#8C8C8C" },
    };

    private static readonly IBrush DefaultFileColor = new SolidColorBrush(Color.Parse("#8C8C8C"));
    private static readonly IBrush FolderColor = new SolidColorBrush(Color.Parse("#DCAD54"));

    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public Geometry IconData => IsDirectory ? FolderIcon : FileIcon;
    public IBrush IconColor { get; set; } = DefaultFileColor;
    public ObservableCollection<FileTreeNode> Children { get; set; } = new();

    private bool _isExpanded;
    private bool _childrenLoaded;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged();
            if (value && IsDirectory && !_childrenLoaded)
                LoadChildren();
        }
    }

    // ── Gitignore support ──

    private static HashSet<string>? _gitignorePatterns;
    private static string? _gitignoreRoot;

    public static bool UseGitignore { get; set; } = false;

    private static void LoadGitignore(string rootPath)
    {
        _gitignoreRoot = rootPath;
        _gitignorePatterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var gitignorePath = Path.Combine(rootPath, ".gitignore");
        if (!File.Exists(gitignorePath)) return;

        try
        {
            foreach (var rawLine in File.ReadAllLines(gitignorePath))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
                    continue;
                _gitignorePatterns.Add(line.TrimEnd('/'));
            }
        }
        catch { }
    }

    private static bool IsGitignored(string fullPath, bool isDirectory)
    {
        if (!UseGitignore || _gitignorePatterns == null || _gitignorePatterns.Count == 0 || _gitignoreRoot == null)
            return false;

        var name = Path.GetFileName(fullPath);
        var relativePath = Path.GetRelativePath(_gitignoreRoot, fullPath).Replace('\\', '/');

        foreach (var pattern in _gitignorePatterns)
        {
            // Exact name match (e.g. "node_modules", "bin", ".vs")
            if (string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase))
                return true;

            // Path-based match (e.g. "dist/", "build/output")
            if (relativePath.Equals(pattern, StringComparison.OrdinalIgnoreCase))
                return true;

            // Simple wildcard match (e.g. "*.suo", "*.user")
            if (pattern.StartsWith('*') && name.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase))
                return true;

            // Directory-only pattern (e.g. "bin/")
            if (isDirectory && pattern.EndsWith('/') &&
                string.Equals(name, pattern.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    // Always-hidden directories (version control, IDE, etc.)
    private static readonly HashSet<string> AlwaysHidden = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", ".vs", ".idea"
    };

    private static bool ShouldHide(string fullPath, bool isDirectory)
    {
        if (!UseGitignore) return false;
        var name = Path.GetFileName(fullPath);
        if (isDirectory && AlwaysHidden.Contains(name))
            return true;
        return IsGitignored(fullPath, isDirectory);
    }

    // ── Node creation ──

    public static FileTreeNode CreateForPath(string path, bool isDirectory)
    {
        var node = new FileTreeNode
        {
            Name = Path.GetFileName(path),
            FullPath = path,
            IsDirectory = isDirectory
        };

        if (isDirectory)
        {
            node.IconColor = FolderColor;
            try
            {
                if (Directory.EnumerateFileSystemEntries(path).Any())
                    node.Children.Add(new FileTreeNode { Name = "Loading..." });
            }
            catch { /* access denied */ }
        }
        else
        {
            var ext = Path.GetExtension(path);
            if (!string.IsNullOrEmpty(ext) && ExtColorMap.TryGetValue(ext, out var hex))
                node.IconColor = new SolidColorBrush(Color.Parse(hex));
        }

        return node;
    }

    public void LoadChildren()
    {
        if (_childrenLoaded) return;
        _childrenLoaded = true;
        Children.Clear();

        try
        {
            var dirs = Directory.GetDirectories(FullPath)
                .Where(d => !ShouldHide(d, true))
                .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase);

            foreach (var dir in dirs)
                Children.Add(CreateForPath(dir, true));

            var files = Directory.GetFiles(FullPath)
                .Where(f => !ShouldHide(f, false))
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
                Children.Add(CreateForPath(file, false));
        }
        catch { /* access denied */ }
    }

    public static ObservableCollection<FileTreeNode> CreateRootNodes(string rootPath)
    {
        LoadGitignore(rootPath);

        var nodes = new ObservableCollection<FileTreeNode>();
        try
        {
            var dirs = Directory.GetDirectories(rootPath)
                .Where(d => !ShouldHide(d, true))
                .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase);

            foreach (var dir in dirs)
                nodes.Add(CreateForPath(dir, true));

            var files = Directory.GetFiles(rootPath)
                .Where(f => !ShouldHide(f, false))
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
                nodes.Add(CreateForPath(file, false));
        }
        catch { /* access denied */ }
        return nodes;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
