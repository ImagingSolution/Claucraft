using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Claucraft.Services;

namespace Claucraft.Controls;

/// <summary>What the pane next to Chat View is showing.</summary>
public enum SidePaneTab { Terminal, Preview }

/// <summary>
/// The pane to the right of Chat View: a strip of tabs over one content area. Every tab but
/// Terminal hosts a control of its own. The Terminal tab hosts nothing - the terminal control
/// that owns this pane draws its own grid in the empty area, so the pane leaves it transparent
/// and lets clicks through.
/// </summary>
public sealed class ChatSidePane : Panel
{
    public const double StripHeight = 32;

    private readonly Border _strip;
    private readonly StackPanel _tabs;
    private readonly Button _closeButton;
    private readonly Border _host;
    private readonly Dictionary<SidePaneTab, Control> _contents = new();
    private readonly Dictionary<SidePaneTab, Button> _tabButtons = new();
    private SidePaneTab _active = SidePaneTab.Terminal;
    private bool _isDark;

    /// <summary>The reader picked another tab.</summary>
    public event Action<SidePaneTab>? TabChanged;

    /// <summary>The reader closed the pane.</summary>
    public event Action? CloseRequested;

    public SidePaneTab ActiveTab => _active;

    public ChatSidePane(bool isDark)
    {
        _isDark = isDark;
        _tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        foreach (var tab in Enum.GetValues<SidePaneTab>())
        {
            var t = tab;
            var b = new Button
            {
                Content = Loc.Get("SidePane" + tab),
                Padding = new Thickness(10, 3),
                FontSize = 12,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0, 0, 0, 2),
                CornerRadius = new CornerRadius(0),
                Cursor = new Cursor(StandardCursorType.Hand),
                Focusable = false,
            };
            b.Click += (_, _) => Select(t);
            _tabButtons[tab] = b;
            _tabs.Children.Add(b);
        }

        _closeButton = new Button
        {
            Content = "✕",
            Padding = new Thickness(8, 2),
            FontSize = 11,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Focusable = false,
        };
        ToolTip.SetTip(_closeButton, Loc.Get("SidePaneClose"));
        _closeButton.Click += (_, _) => CloseRequested?.Invoke();

        var stripGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        stripGrid.Children.Add(_tabs);
        Grid.SetColumn(_closeButton, 1);
        stripGrid.Children.Add(_closeButton);
        _strip = new Border
        {
            Height = StripHeight,
            Padding = new Thickness(6, 0, 4, 0),
            BorderThickness = new Thickness(1, 0, 0, 1),
            Child = stripGrid,
        };

        _host = new Border { BorderThickness = new Thickness(1, 0, 0, 0) };

        Children.Add(_host);
        Children.Add(_strip);
        ApplyTheme(isDark);
    }

    /// <summary>Gives a tab its control; null leaves the tab's area empty.</summary>
    public void SetContent(SidePaneTab tab, Control? content)
    {
        if (content == null) _contents.Remove(tab);
        else _contents[tab] = content;
        if (tab == _active) ShowActive();
    }

    public void Select(SidePaneTab tab)
    {
        if (_active == tab) return;
        _active = tab;
        ShowActive();
        TabChanged?.Invoke(tab);
    }

    /// <summary>Selects a tab without raising <see cref="TabChanged"/>, for restoring saved state.</summary>
    public void SelectSilently(SidePaneTab tab)
    {
        _active = tab;
        ShowActive();
    }

    private void ShowActive()
    {
        _host.Child = _contents.TryGetValue(_active, out var c) ? c : null;
        // The terminal draws underneath; anything painted here would hide it
        _host.Background = _active == SidePaneTab.Terminal ? null : new SolidColorBrush(ChatTheme.Background(_isDark));
        UpdateTabLook();
    }

    public void ApplyTheme(bool isDark)
    {
        _isDark = isDark;
        var outline = new SolidColorBrush(ChatTheme.Outline(isDark));
        _strip.Background = new SolidColorBrush(ChatTheme.Surface(isDark));
        _strip.BorderBrush = outline;
        _host.BorderBrush = outline;
        _closeButton.Foreground = new SolidColorBrush(isDark ? Color.FromRgb(160, 160, 165) : Color.FromRgb(90, 90, 95));
        ShowActive();
    }

    private void UpdateTabLook()
    {
        var dim = new SolidColorBrush(_isDark ? Color.FromRgb(150, 150, 155) : Color.FromRgb(100, 100, 105));
        var fg = new SolidColorBrush(_isDark ? Color.FromRgb(230, 230, 232) : Color.FromRgb(30, 30, 32));
        foreach (var (tab, b) in _tabButtons)
        {
            bool on = tab == _active;
            // A tab shows once it has something to show; Terminal is drawn by the owner
            b.IsVisible = on || tab == SidePaneTab.Terminal || _contents.ContainsKey(tab);
            b.Foreground = on ? fg : dim;
            b.BorderBrush = on ? new SolidColorBrush(ChatTheme.Accent(_isDark)) : Brushes.Transparent;
            b.FontWeight = on ? FontWeight.SemiBold : FontWeight.Normal;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _strip.Measure(new Size(availableSize.Width, StripHeight));
        _host.Measure(new Size(availableSize.Width, Math.Max(0, availableSize.Height - StripHeight)));
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _strip.Arrange(new Rect(0, 0, finalSize.Width, StripHeight));
        _host.Arrange(new Rect(0, StripHeight, finalSize.Width, Math.Max(0, finalSize.Height - StripHeight)));
        return finalSize;
    }
}
