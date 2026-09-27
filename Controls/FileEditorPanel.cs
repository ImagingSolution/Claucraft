using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Claucraft.Services;

namespace Claucraft.Controls;

/// <summary>
/// The side pane's Files tab: one file open for editing beside the conversation. Reads and
/// writes through <see cref="TextFileEditor"/>, so a save keeps the file's encoding and line
/// endings. While the tab is in view it watches the file; a clean buffer follows what Claude
/// writes, and a dirty one is told the file moved under it.
/// </summary>
public sealed class FileEditorPanel : DockPanel
{
    private readonly FontFamily _mono;
    private readonly TextBlock _title;
    private readonly TextBlock _dirtyMark;
    private readonly Button _reloadButton;
    private readonly Button _saveButton;
    private readonly Border _notice;
    private readonly TextBlock _noticeText;
    private readonly StackPanel _noticeButtons;
    private readonly TextBox _editor;
    private readonly TextBlock _empty;
    private readonly DispatcherTimer _timer;
    private TextFileDocument? _doc;
    private bool _dirty;
    private bool _loadingText;
    private bool _conflictShown;
    private bool _isDark;

    public FileEditorPanel(bool isDark, FontFamily mono)
    {
        _isDark = isDark;
        _mono = mono;

        _title = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.PathSegmentEllipsis };
        _dirtyMark = new TextBlock { Text = " ●", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(ChatTheme.Accent), IsVisible = false };
        _reloadButton = ToolButton("⟳", () => Reload());
        ToolTip.SetTip(_reloadButton, Loc.Get("EditorReload"));
        _saveButton = ToolButton(Loc.Get("EditorSave"), () => Save(force: false));
        ToolTip.SetTip(_saveButton, "Ctrl+S");

        var toolbar = new DockPanel { Margin = new Thickness(10, 6, 6, 6) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        buttons.Children.Add(_reloadButton);
        buttons.Children.Add(_saveButton);
        DockPanel.SetDock(buttons, Dock.Right);
        toolbar.Children.Add(buttons);
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(_title);
        titleRow.Children.Add(_dirtyMark);
        toolbar.Children.Add(titleRow);
        DockPanel.SetDock(toolbar, Dock.Top);
        Children.Add(toolbar);

        _noticeText = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        _noticeButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var noticeRow = new DockPanel();
        DockPanel.SetDock(_noticeButtons, Dock.Right);
        noticeRow.Children.Add(_noticeButtons);
        noticeRow.Children.Add(_noticeText);
        _notice = new Border { Padding = new Thickness(10, 6), Child = noticeRow, IsVisible = false };
        DockPanel.SetDock(_notice, Dock.Top);
        Children.Add(_notice);

        _editor = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = mono,
            FontSize = 12.5,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(10, 6),
            IsVisible = false,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_editor, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_editor, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        // TextChanged arrives after the load has finished, so dirty is judged by content, which
        // also makes undoing back to the saved text clean again
        _editor.TextChanged += (_, _) =>
        {
            if (_loadingText || _doc == null || !_doc.Editable) return;
            SetDirty(!SameText(_editor.Text ?? "", _doc.Text));
        };
        _editor.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                Save(force: false);
                e.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        _empty = new TextBlock
        {
            Text = Loc.Get("EditorEmpty"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(20),
        };
        var body = new Panel();
        body.Children.Add(_empty);
        body.Children.Add(_editor);
        Children.Add(body);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => CheckDisk();
        ApplyTheme(isDark);
        UpdateChrome();
    }

    /// <summary>The file on show, or null.</summary>
    public string? FilePath => _doc?.Path;

    public bool HasUnsavedChanges => _dirty;

    /// <summary>Opens <paramref name="path"/>. Refuses to throw away unsaved edits to another file.</summary>
    public void Open(string path)
    {
        if (_doc != null && string.Equals(_doc.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            if (!_dirty) Reload();
            return;
        }
        if (_dirty)
        {
            ShowNotice(string.Format(Loc.Get("EditorUnsavedSwitch"), System.IO.Path.GetFileName(_doc!.Path)),
                (Loc.Get("EditorSave"), () => { if (Save(force: false)) Open(path); }),
                (Loc.Get("EditorDiscardEdits"), () => { SetDirty(false); Open(path); }));
            return;
        }
        Load(path);
    }

    public void SetActive(bool active)
    {
        if (active == _timer.IsEnabled) return;
        if (active) { _timer.Start(); CheckDisk(); }
        else _timer.Stop();
    }

    public void ApplyTheme(bool isDark)
    {
        _isDark = isDark;
        var bg = new SolidColorBrush(ChatTheme.Background(isDark));
        var fg = new SolidColorBrush(isDark ? Color.FromRgb(225, 225, 228) : Color.FromRgb(35, 35, 38));
        Background = bg;
        _title.Foreground = fg;
        _empty.Foreground = new SolidColorBrush(isDark ? Color.FromRgb(150, 150, 155) : Color.FromRgb(105, 105, 110));
        _editor.Background = bg;
        _editor.Foreground = fg;
        _editor.Resources["TextControlBackgroundFocused"] = bg;
        _editor.Resources["TextControlBackgroundPointerOver"] = bg;
        _editor.Resources["TextControlForegroundFocused"] = fg;
        _editor.Resources["TextControlForegroundPointerOver"] = fg;
        _notice.Background = new SolidColorBrush(ChatTheme.Surface(isDark));
        _noticeText.Foreground = fg;
        foreach (var b in new[] { _reloadButton, _saveButton })
        {
            b.Foreground = fg;
            b.BorderBrush = new SolidColorBrush(ChatTheme.Outline(isDark));
        }
    }

    private Button ToolButton(string text, Action onClick)
    {
        var b = new Button
        {
            Content = text,
            Padding = new Thickness(8, 2),
            FontSize = 12,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(ChatTheme.Outline(_isDark)),
            CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private void Load(string path)
    {
        TextFileDocument doc;
        try { doc = TextFileEditor.Read(path); }
        catch (Exception ex)
        {
            ShowNotice(ex.Message);
            return;
        }
        _doc = doc;
        _loadingText = true;
        _editor.Text = doc.Text;
        _editor.CaretIndex = 0;
        _loadingText = false;
        _editor.IsReadOnly = !doc.Editable;
        SetDirty(false);
        _conflictShown = false;
        if (doc.Editable) HideNotice();
        else ShowNotice(Loc.Get(doc.Block switch
        {
            EditBlock.TooLarge => "EditorTooLarge",
            EditBlock.Binary => "EditorBinary",
            _ => "EditorNotUtf8",
        }));
        UpdateChrome();
    }

    private void Reload()
    {
        if (_doc == null) return;
        if (!System.IO.File.Exists(_doc.Path))
        {
            ShowNotice(Loc.Get("EditorDeleted"));
            return;
        }
        var caret = _editor.CaretIndex;
        Load(_doc.Path);
        _editor.CaretIndex = Math.Min(caret, _editor.Text?.Length ?? 0);
    }

    private bool Save(bool force)
    {
        if (_doc == null || !_doc.Editable) return false;
        if (!force && TextFileEditor.ChangedOnDisk(_doc))
        {
            ShowConflict();
            return false;
        }
        var saved = TextFileEditor.Write(_doc, _editor.Text ?? "", out var error);
        if (saved == null)
        {
            ShowNotice(string.Format(Loc.Get("EditorSaveFailed"), error));
            return false;
        }
        _doc = saved;
        _conflictShown = false;
        SetDirty(false);
        HideNotice();
        return true;
    }

    private void CheckDisk()
    {
        if (_doc == null || !TextFileEditor.ChangedOnDisk(_doc)) return;
        if (!_dirty)
        {
            if (System.IO.File.Exists(_doc.Path)) Reload();
            else ShowNotice(Loc.Get("EditorDeleted"));
            return;
        }
        if (!_conflictShown) ShowConflict();
    }

    private void ShowConflict()
    {
        _conflictShown = true;
        ShowNotice(Loc.Get("EditorChangedOnDisk"),
            (Loc.Get("EditorReload"), () => Reload()),
            (Loc.Get("EditorOverwrite"), () => Save(force: true)));
    }

    private void ShowNotice(string text, params (string Label, Action OnClick)[] actions)
    {
        _noticeText.Text = text;
        _noticeButtons.Children.Clear();
        foreach (var (label, onClick) in actions)
        {
            var b = ToolButton(label, onClick);
            b.Foreground = _noticeText.Foreground;
            _noticeButtons.Children.Add(b);
        }
        _notice.IsVisible = true;
    }

    private void HideNotice() => _notice.IsVisible = false;

    private static bool SameText(string a, string b) =>
        a.Length == b.Length ? a == b : a.Replace("\r\n", "\n") == b.Replace("\r\n", "\n");

    private void SetDirty(bool dirty)
    {
        _dirty = dirty;
        _dirtyMark.IsVisible = dirty;
        _saveButton.IsEnabled = dirty && _doc?.Editable == true;
    }

    private void UpdateChrome()
    {
        bool has = _doc != null;
        _editor.IsVisible = has;
        _empty.IsVisible = !has;
        _reloadButton.IsEnabled = has;
        _title.Text = has ? System.IO.Path.GetFileName(_doc!.Path) : Loc.Get("SidePaneFiles");
        ToolTip.SetTip(_title, _doc?.Path);
        SetDirty(_dirty);
    }
}
