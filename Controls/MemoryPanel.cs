using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Claucraft.Services;

namespace Claucraft.Controls;

/// <summary>
/// What the memory panel needs from the window around it. Deliberately small: this panel reads,
/// it does not write, so there is nothing here that changes a file.
/// </summary>
public sealed record MemoryHost(
    Action<string> OpenInEditor,
    Action<string> RevealInExplorer,
    Action<string, string> ShowMessage,
    Action<MemoryNoteView> OpenNoteWindow);

/// <summary>
/// What Claude has chosen to remember, made visible.
///
/// Claude Code already keeps a per-project set of Markdown notes under
/// <c>~/.claude/projects/&lt;project&gt;/memory/</c>, cross-referenced with <c>[[wikilinks]]</c> and
/// indexed by a <c>MEMORY.md</c> it loads every session. The AI reads them perfectly well on its
/// own - it has grep. The person the notes are about cannot see them at all without opening a
/// shell. This panel closes that gap and nothing more: it lists the notes, finds text across the
/// set, and opens one for reading in its own window. Editing is the file editor's job, and
/// writing a note is Claude's.
/// </summary>
public sealed class MemoryPanel : UserControl
{
    /// <summary>
    /// One project the combo can switch to. <see cref="FullPath"/> is the readable path recovered
    /// for the tooltip; <see cref="Display"/> is just its leaf folder name, since two different
    /// projects can share that name and the full path is what disambiguates them.
    /// </summary>
    private sealed record ProjectEntry(string Dir, string FullPath)
    {
        public string Display => LeafName(FullPath);
        public override string ToString() => Display;

        private static string LeafName(string fullPath)
        {
            var trimmed = (fullPath ?? "").TrimEnd('\\', '/');
            var cut = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
            return cut >= 0 && cut < trimmed.Length - 1 ? trimmed[(cut + 1)..] : trimmed;
        }
    }

    private readonly bool _isDark;
    private readonly Typeface _mono;
    private readonly MemoryHost _host;

    /// <summary>The project folder the active window is on, as the window gave it.</summary>
    private string _folder = "";

    private MemoryStore _store = MemoryStore.Empty;
    private MemoryNote? _selected;
    private readonly List<ProjectEntry> _projects = new();

    /// <summary>Set while the combo is being refilled, so doing so does not count as a choice.</summary>
    private bool _suppressProjectChanged;

    private readonly ComboBox _projectCombo;
    private readonly TextBox _search;
    private readonly TextBlock _count;
    private readonly StackPanel _list;

    /// <summary>The rows currently drawn, so selection can be repainted without a rebuild.</summary>
    private readonly Dictionary<string, Border> _rows = new(StringComparer.OrdinalIgnoreCase);

    public MemoryPanel(bool isDark, Typeface mono, MemoryHost host)
    {
        _isDark = isDark;
        _mono = mono;
        _host = host;

        _projectCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = 11.5,
            MinHeight = 26,
            ItemTemplate = new FuncDataTemplate<ProjectEntry>((entry, _) =>
            {
                var text = new TextBlock { Text = entry?.Display ?? "", VerticalAlignment = VerticalAlignment.Center };
                if (entry != null) ToolTip.SetTip(text, entry.FullPath);
                return text;
            }),
        };
        _projectCombo.SelectionChanged += (_, _) =>
        {
            if (_suppressProjectChanged) return;
            if (_projectCombo.SelectedItem is ProjectEntry entry) LoadDirectory(Path.Combine(entry.Dir, "memory"));
        };
        ToolTip.SetTip(_projectCombo, Loc.Get("Project"));

        var btnRefresh = new Button
        {
            Content = "⟳",
            FontSize = 13,
            Padding = new Thickness(6, 1),
            MinHeight = 26,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        btnRefresh.Click += (_, _) => Reload();
        ToolTip.SetTip(btnRefresh, Loc.Get("Refresh"));

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(8, 6, 6, 4),
        };
        header.Children.Add(_projectCombo);
        Grid.SetColumn(btnRefresh, 1);
        header.Children.Add(btnRefresh);

        _search = new TextBox
        {
            PlaceholderText = Loc.Get("MemorySearch"),
            FontSize = 12,
            Margin = new Thickness(8, 0, 8, 4),
            MinHeight = 26,
        };
        _search.TextChanged += (_, _) => RebuildList();

        _count = new TextBlock
        {
            FontSize = 10.5,
            Foreground = new SolidColorBrush(MemoryColors.DimText(_isDark)),
            Margin = new Thickness(10, 0, 8, 4),
        };

        _list = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        var listScroll = new ScrollViewer
        {
            Content = _list,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"),
        };
        root.Children.Add(header);
        Grid.SetRow(_search, 1);
        root.Children.Add(_search);
        Grid.SetRow(_count, 2);
        root.Children.Add(_count);
        Grid.SetRow(listScroll, 3);
        root.Children.Add(listScroll);

        Content = root;
    }

    // ── Loading ────────────────────────────────────────────────────────

    /// <summary>
    /// Points the panel at the project the active window is on. Called whenever the window the
    /// user is typing in changes, so the notes follow the terminal rather than lagging behind it.
    /// </summary>
    public void SetProject(string? projectFolder)
    {
        var folder = projectFolder ?? "";
        if (string.Equals(folder, _folder, StringComparison.OrdinalIgnoreCase) && _projects.Count > 0) return;
        _folder = folder;
        Reload();
    }

    /// <summary>Re-reads the project list and the notes of whichever project is selected.</summary>
    public void Reload()
    {
        var wanted = _projectCombo.SelectedItem is ProjectEntry current && _projects.Count > 0
            ? current.Dir
            : (string.IsNullOrEmpty(_folder) ? "" : ClaudeProjectPaths.ProjectDir(_folder));

        RebuildProjects(wanted);

        if (_projectCombo.SelectedItem is ProjectEntry entry)
            LoadDirectory(Path.Combine(entry.Dir, "memory"));
        else
            LoadDirectory("");
    }

    private void RebuildProjects(string preferredDir)
    {
        _projects.Clear();

        // The active project comes first whether or not it has notes yet: its absence from the
        // list would read as "no such project" rather than "nothing remembered".
        var active = string.IsNullOrEmpty(_folder) ? "" : ClaudeProjectPaths.ProjectDir(_folder);
        if (active.Length > 0)
            _projects.Add(new ProjectEntry(active, _folder));

        foreach (var dir in ClaudeProjectPaths.ProjectsWithMemory())
        {
            if (string.Equals(dir, active, StringComparison.OrdinalIgnoreCase)) continue;
            _projects.Add(new ProjectEntry(dir, ClaudeProjectPaths.DisplayPath(dir)));
        }

        _suppressProjectChanged = true;
        _projectCombo.ItemsSource = null;
        _projectCombo.ItemsSource = _projects;

        int index = _projects.FindIndex(p => string.Equals(p.Dir, preferredDir, StringComparison.OrdinalIgnoreCase));
        _projectCombo.SelectedIndex = _projects.Count == 0 ? -1 : Math.Max(index, 0);
        _suppressProjectChanged = false;
    }

    private void LoadDirectory(string memoryDir)
    {
        _store = MemoryStore.Load(memoryDir);
        _selected = null;
        RebuildList();
    }

    // ── The list ───────────────────────────────────────────────────────

    private void RebuildList()
    {
        _list.Children.Clear();
        _rows.Clear();

        if (_projects.Count == 0)
        {
            _count.Text = "";
            _list.Children.Add(Placeholder(Loc.Get("MemoryNoProject")));
            return;
        }

        var query = _search.Text ?? "";
        bool searching = !string.IsNullOrWhiteSpace(query);
        var notes = _store.Search(query);

        _count.Text = string.Format(Loc.Get("MemoryCountFmt"), notes.Count);

        if (_store.IsEmpty)
        {
            _list.Children.Add(Placeholder(Loc.Get("MemoryNoNotes")));
            _list.Children.Add(Placeholder(Loc.Get("MemoryNoNotesHint")));
            return;
        }

        // The index is not one note among many - it is the file Claude reads every time - so it
        // sits above the groups rather than inside one, and stays put while a search filters.
        if (_store.Index != null && !searching)
        {
            _list.Children.Add(NoteRow(_store.Index, Loc.Get("MemoryIndex")));
            _list.Children.Add(new Border { Height = 6 });
        }

        if (notes.Count == 0)
        {
            _list.Children.Add(Placeholder(Loc.Get("NoMatches")));
            return;
        }

        if (searching)
        {
            foreach (var note in notes) _list.Children.Add(NoteRow(note, null));
            return;
        }

        string? group = null;
        foreach (var note in notes)
        {
            var label = TypeLabel(note.Type);
            if (!string.Equals(label, group, StringComparison.Ordinal))
            {
                group = label;
                _list.Children.Add(GroupHeader(label));
            }
            _list.Children.Add(NoteRow(note, null));
        }
    }

    private Border NoteRow(MemoryNote note, string? overrideTitle)
    {
        var title = new TextBlock
        {
            Text = overrideTitle ?? note.Name,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(MemoryColors.Text(_isDark)),
            TextWrapping = TextWrapping.Wrap,
        };

        var stack = new StackPanel { Spacing = 1 };
        stack.Children.Add(title);

        var summary = overrideTitle != null ? Loc.Get("MemoryIndexHint") : note.Summary;
        if (!string.IsNullOrWhiteSpace(summary))
        {
            stack.Children.Add(new TextBlock
            {
                Text = summary,
                FontSize = 10.5,
                Foreground = new SolidColorBrush(MemoryColors.DimText(_isDark)),
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }

        var row = new Border
        {
            Child = stack,
            Padding = new Thickness(10, 5, 8, 5),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = note.Slug,
        };
        ToolTip.SetTip(row, note.FilePath);

        // A click opens the note in a window of its own; the second click of a double opens the
        // file itself, so the editor stays one gesture away from the reading view.
        row.PointerPressed += (_, e) =>
        {
            if (e.ClickCount >= 2) _host.OpenInEditor(note.FilePath);
            else OpenNote(note);
        };

        var menu = new ContextMenu();
        var open = new MenuItem { Header = Loc.Get("MemoryOpenFile") };
        open.Click += (_, _) => _host.OpenInEditor(note.FilePath);
        var reveal = new MenuItem { Header = Loc.Get("OpenInExplorer") };
        reveal.Click += (_, _) => _host.RevealInExplorer(note.FilePath);
        menu.Items.Add(open);
        menu.Items.Add(reveal);
        row.ContextMenu = menu;

        _rows[note.Slug] = row;
        if (_selected != null && string.Equals(_selected.Slug, note.Slug, StringComparison.OrdinalIgnoreCase))
            row.Background = new SolidColorBrush(MemoryColors.SelectionBg(_isDark));

        return row;
    }

    private Control GroupHeader(string label) => new TextBlock
    {
        Text = label,
        FontSize = 10,
        FontWeight = FontWeight.Bold,
        Foreground = new SolidColorBrush(MemoryColors.DimText(_isDark)),
        Margin = new Thickness(10, 10, 8, 3),
    };

    private Control Placeholder(string text) => new TextBlock
    {
        Text = text,
        FontSize = 11.5,
        Foreground = new SolidColorBrush(MemoryColors.DimText(_isDark)),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(10, 6, 10, 0),
    };

    // ── The note ───────────────────────────────────────────────────────

    /// <summary>
    /// Opens a note for reading in its own window on the MDI canvas. The panel itself stays a
    /// list: a note is worth keeping open beside the terminal it is about, which a pane pinned to
    /// the bottom of the sidebar cannot do.
    /// </summary>
    private void OpenNote(MemoryNote note)
    {
        Select(note);
        _host.OpenNoteWindow(new MemoryNoteView(_isDark, _mono, _store, note, _host));
    }

    /// <summary>Marks which note the list is pointing at. Nothing else - the reading is elsewhere.</summary>
    private void Select(MemoryNote? note)
    {
        foreach (var row in _rows.Values) row.Background = Brushes.Transparent;
        _selected = note;

        if (note != null && _rows.TryGetValue(note.Slug, out var selectedRow))
            selectedRow.Background = new SolidColorBrush(MemoryColors.SelectionBg(_isDark));
    }

    // ── Theme ──────────────────────────────────────────────────────────

    internal static string TypeLabel(string type) => type.ToLowerInvariant() switch
    {
        "user" => Loc.Get("MemoryTypeUser"),
        "feedback" => Loc.Get("MemoryTypeFeedback"),
        "project" => Loc.Get("MemoryTypeProject"),
        "reference" => Loc.Get("MemoryTypeReference"),
        _ => Loc.Get("MemoryTypeOther"),
    };
}
