using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Claucraft.Services;

namespace Claucraft.Controls;

/// <summary>Colours shared by the memory list and the note windows opened from it.</summary>
internal static class MemoryColors
{
    public static Color Text(bool dark) => dark ? Color.FromRgb(220, 220, 225) : Color.FromRgb(28, 28, 30);

    public static Color DimText(bool dark) => dark ? Color.FromRgb(140, 140, 148) : Color.FromRgb(84, 84, 92);

    public static Color Divider(bool dark) => dark ? Color.FromRgb(56, 56, 58) : Color.FromRgb(210, 210, 215);

    public static Color Link(bool dark) => dark ? Color.FromRgb(100, 180, 255) : Color.FromRgb(0, 100, 200);

    public static Color BrokenLink(bool dark) => dark ? Color.FromRgb(230, 150, 110) : Color.FromRgb(175, 85, 20);

    public static Color SelectionBg(bool dark) => dark ? Color.FromArgb(56, 0, 122, 255) : Color.FromArgb(40, 0, 122, 255);
}

/// <summary>
/// One memory note, rendered for reading: the body as Markdown, its links followable, and below
/// them what links back and what has not been written yet. A link moves this same view to its
/// target, except in the index, which opens each entry in its own window - see <see cref="Follow"/>.
///
/// The view carries the snapshot of the store it was opened with, so following a link never has to
/// go back to disk and a note stays readable while the panel behind it reloads.
/// </summary>
public sealed class MemoryNoteView : UserControl
{
    private readonly bool _isDark;
    private readonly Typeface _mono;
    private readonly MemoryStore _store;
    private readonly MemoryHost _host;
    private readonly StackPanel _body;
    private readonly ScrollViewer _scroll;

    /// <summary>Whether this view has been in the visual tree before - see OnAttachedToVisualTree.</summary>
    private bool _attachedOnce;

    /// <summary>Raised when navigation changes which note is on screen, so the window can retitle.</summary>
    public event Action<string>? TitleChanged;

    public MemoryNote Note { get; private set; }

    public MemoryNoteView(bool isDark, Typeface mono, MemoryStore store, MemoryNote note, MemoryHost host)
    {
        _isDark = isDark;
        _mono = mono;
        _store = store;
        _host = host;
        Note = note;

        _body = new StackPanel { Margin = new Thickness(16, 12, 16, 18) };
        _scroll = new ScrollViewer
        {
            Content = _body,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        Content = _scroll;
        Render();
    }

    /// <summary>The name a window showing this note should carry.</summary>
    public string Title => TitleOf(Note);

    private string TitleOf(MemoryNote note) => IsIndex(note) ? Loc.Get("MemoryIndex") : note.Name;

    private bool IsIndex(MemoryNote note) =>
        _store.Index != null && string.Equals(_store.Index.Slug, note.Slug, StringComparison.OrdinalIgnoreCase);

    /// <summary>Moves this view to another note of the same store.</summary>
    public void ShowNote(MemoryNote note)
    {
        Note = note;
        Render();
        TitleChanged?.Invoke(Title);
    }

    /// <summary>
    /// Opens a note this one links to. The index is a table of contents, and a contents page that
    /// replaces itself with the first thing you look up stops being one - so from there a link
    /// opens its own window (or raises the window already showing that note). Inside an ordinary
    /// note a link is a digression, and this window follows it.
    /// </summary>
    private void Follow(MemoryNote note)
    {
        if (IsIndex(Note))
            _host.OpenNoteWindow(new MemoryNoteView(_isDark, _mono, _store, note, _host));
        else
            ShowNote(note);
    }

    /// <summary>
    /// Rebuilds the body when the view comes back into the visual tree. A link is drawn as a
    /// control inside an <c>InlineUIContainer</c>, and Avalonia builds those only while the text
    /// block is attached - it does not build them again afterwards, so a paragraph holding a link
    /// comes back empty. The dock host takes a window out of the tree on every rearrange, which is
    /// what switching MDI tabs does, so the body is built again each time it returns.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (_attachedOnce) Render(keepScroll: true);
        _attachedOnce = true;
    }

    private void Render(bool keepScroll = false)
    {
        var offset = _scroll.Offset;
        _body.Children.Clear();

        _body.Children.Add(new TextBlock
        {
            Text = TitleOf(Note),
            FontSize = 17,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(MemoryColors.Text(_isDark)),
            TextWrapping = TextWrapping.Wrap,
        });

        var meta = new List<string>();
        if (!string.IsNullOrEmpty(Note.Type)) meta.Add(MemoryPanel.TypeLabel(Note.Type));
        if (Note.Modified > DateTime.MinValue)
            meta.Add(string.Format(Loc.Get("MemoryModifiedFmt"), Note.Modified.ToString("yyyy-MM-dd HH:mm")));

        var metaRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 3, 0, 10),
        };
        metaRow.Children.Add(new TextBlock
        {
            Text = string.Join("  ·  ", meta),
            FontSize = 11,
            Foreground = new SolidColorBrush(MemoryColors.DimText(_isDark)),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });

        var btnOpen = new Button
        {
            Content = Loc.Get("MemoryOpenFile"),
            FontSize = 11,
            Padding = new Thickness(8, 2),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(MemoryColors.Divider(_isDark)),
            Foreground = new SolidColorBrush(MemoryColors.DimText(_isDark)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        btnOpen.Click += (_, _) => _host.OpenInEditor(Note.FilePath);
        ToolTip.SetTip(btnOpen, Note.FilePath);
        Grid.SetColumn(btnOpen, 1);
        metaRow.Children.Add(btnOpen);
        _body.Children.Add(metaRow);

        var wiki = new WikiLinkOptions(Navigate, _store.Exists, OpenUrl);
        foreach (var control in MarkdownParser.Parse(Note.Body, _isDark, _mono, 13, wiki))
            _body.Children.Add(control);

        AddBacklinks(_store.BacklinksTo(Note.Slug));

        var dangling = _store.DanglingIn(Note);
        if (dangling.Count > 0) AddDangling(dangling);

        // A rebuilt body has not been measured yet, so the offset it kept is restored once it has.
        if (keepScroll)
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => _scroll.Offset = offset, Avalonia.Threading.DispatcherPriority.Loaded);
        else
            _scroll.Offset = new Vector(0, 0);
    }

    private void AddBacklinks(IReadOnlyList<MemoryNote> notes)
    {
        if (notes.Count == 0) return;

        _body.Children.Add(SectionHeader(Loc.Get("MemoryBacklinks")));

        foreach (var note in notes)
        {
            var link = new TextBlock
            {
                Text = note.Name,
                FontSize = 12.5,
                Foreground = new SolidColorBrush(MemoryColors.Link(_isDark)),
                TextDecorations = TextDecorations.Underline,
                Cursor = new Cursor(StandardCursorType.Hand),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 1),
            };
            var target = note;
            link.PointerPressed += (_, e) => { Follow(target); e.Handled = true; };
            _body.Children.Add(link);
        }
    }

    private void AddDangling(IReadOnlyList<string> targets)
    {
        _body.Children.Add(SectionHeader(Loc.Get("MemoryDangling")));
        _body.Children.Add(new TextBlock
        {
            Text = Loc.Get("MemoryDanglingHint"),
            FontSize = 11,
            Foreground = new SolidColorBrush(MemoryColors.DimText(_isDark)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 3),
        });

        foreach (var target in targets)
        {
            _body.Children.Add(new TextBlock
            {
                Text = target,
                FontSize = 12.5,
                Foreground = new SolidColorBrush(MemoryColors.BrokenLink(_isDark)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 1),
            });
        }
    }

    private Control SectionHeader(string text) => new TextBlock
    {
        Text = text,
        FontSize = 10.5,
        FontWeight = FontWeight.Bold,
        Foreground = new SolidColorBrush(MemoryColors.DimText(_isDark)),
        Margin = new Thickness(0, 16, 0, 4),
    };

    /// <summary>
    /// Follows a <c>[[wikilink]]</c>. A target that does not resolve is not an error worth a dialog
    /// - the note simply has not been written - so it is reported as a message and the current note
    /// stays on screen.
    /// </summary>
    private void Navigate(string slug)
    {
        var note = _store.BySlug(slug);
        if (note == null)
        {
            _host.ShowMessage(Loc.Get("MemoryDangling"), $"{slug} — {Loc.Get("MemoryNoteMissing")}");
            return;
        }

        Follow(note);
    }

    /// <summary>
    /// Follows an <c>http(s)</c> link. A note that cites an address is citing something to look at,
    /// and nothing in the app can show it, so it goes to whatever the system opens links with.
    /// </summary>
    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // No browser association, or the user cancelled the shell prompt.
        }
    }
}
