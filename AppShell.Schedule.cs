using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Snipyard.Services;

namespace Snipyard;

/// <summary>
/// Scheduled tasks: prompts that open a session of their own at a set time, every day, or every
/// so many minutes, and remote control for a running session. The schedule only runs while the
/// application does, and only in the main window, so a dragged-out shell does not fire it twice.
/// </summary>
internal partial class AppShell
{
    private DispatcherTimer? _scheduleTimer;

    private void StartScheduler()
    {
        if (!_primary) return;
        _scheduleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _scheduleTimer.Tick += (_, _) => RunDueSchedules();
        _scheduleTimer.Start();
    }

    private void RunDueSchedules()
    {
        if (_shutdown) return;
        foreach (var task in ScheduleStore.Shared.TakeDue(DateTime.Now))
            RunScheduledTask(task);
    }

    /// <summary>Opens a new session in the task's project with its prompt as the first turn.</summary>
    private void RunScheduledTask(ScheduledTask task)
    {
        if (!Directory.Exists(task.ProjectFolder) || string.IsNullOrWhiteSpace(task.Prompt)) return;
        if (!string.Equals(_projectFolder, task.ProjectFolder, StringComparison.OrdinalIgnoreCase))
            SetProjectFolder(task.ProjectFolder);
        CreateNewChild(
            _cli.BuildNewCommand(task.Prompt, ActiveLaunchProfile()),
            "⏰ " + (string.IsNullOrWhiteSpace(task.Name) ? _cli.Active.Name : task.Name));
    }

    private static string DescribeSchedule(ScheduledTask t) => t.Kind switch
    {
        ScheduleKind.Once => string.Format(Loc.Get("ScheduleOnceAt"), t.At.ToString("yyyy/MM/dd HH:mm")),
        ScheduleKind.Interval => string.Format(Loc.Get("ScheduleEveryMinutes"), t.IntervalMinutes),
        _ => string.Format(Loc.Get("ScheduleDailyAt"), t.At.ToString("HH:mm")),
    };

    // ── List dialog ──

    private void ShowScheduleDialog()
    {
        var store = ScheduleStore.Shared;
        var list = new StackPanel { Spacing = 6 };
        var dialog = CreateToolDialog(Loc.Get("ScheduleTitle"), 640, 480);

        void Rebuild()
        {
            list.Children.Clear();
            if (store.Tasks.Count == 0)
            {
                list.Children.Add(new TextBlock
                {
                    Text = Loc.Get("ScheduleEmpty"),
                    Foreground = new SolidColorBrush(DialogSubtle()),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                });
                return;
            }
            foreach (var task in store.Tasks.ToList())
                list.Children.Add(BuildScheduleRow(task, dialog, Rebuild));
        }

        var add = new Button { Content = Loc.Get("ScheduleAdd"), MinWidth = 110, HorizontalContentAlignment = HorizontalAlignment.Center };
        add.Click += async (_, _) =>
        {
            var created = await ShowScheduleEditor(dialog, null);
            if (created == null) return;
            store.Tasks.Add(created);
            store.Save();
            Rebuild();
        };
        var close = new Button { Content = Loc.Get("Close"), MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
        close.Click += (_, _) => dialog.Close();

        var hint = new TextBlock
        {
            Text = Loc.Get("ScheduleHint"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = new SolidColorBrush(DialogSubtle()),
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(add);
        buttons.Children.Add(close);

        var root = new DockPanel { Margin = new Thickness(22, 20), LastChildFill = true };
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        hint.Margin = new Thickness(0, 0, 0, 12);
        buttons.Margin = new Thickness(0, 12, 0, 0);
        root.Children.Add(hint);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { Content = list });
        dialog.Content = root;

        Rebuild();
        _ = dialog.ShowDialog(HostWindow);
    }

    private Control BuildScheduleRow(ScheduledTask task, Window owner, Action rebuild)
    {
        var store = ScheduleStore.Shared;
        var enabled = new CheckBox { IsChecked = task.Enabled, VerticalAlignment = VerticalAlignment.Center };
        enabled.IsCheckedChanged += (_, _) =>
        {
            task.Enabled = enabled.IsChecked == true;
            // Switched back on, it starts counting from now rather than firing for the time it was off
            if (task.Enabled && task.Kind != ScheduleKind.Once) task.LastRun = DateTime.Now;
            store.Save();
            rebuild();
        };

        var next = task.Enabled ? task.NextRun() : null;
        var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(task.Name) ? task.Prompt : task.Name,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        info.Children.Add(new TextBlock
        {
            Text = DescribeSchedule(task) + "  ·  " + Path.GetFileName(task.ProjectFolder.TrimEnd('\\', '/'))
                + "  ·  " + (next is DateTime n
                    ? string.Format(Loc.Get("ScheduleNext"), n.ToString("MM/dd HH:mm"))
                    : Loc.Get("ScheduleNoNext")),
            FontSize = 11,
            Foreground = new SolidColorBrush(DialogSubtle()),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        ToolTip.SetTip(info, task.Prompt);

        Button Small(string key)
            => new() { Content = Loc.Get(key), Padding = new Thickness(10, 3), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var run = Small("ScheduleRunNow");
        run.Click += (_, _) =>
        {
            task.LastRun = DateTime.Now;
            store.Save();
            RunScheduledTask(task);
            rebuild();
        };
        var edit = Small("ScheduleEdit");
        edit.Click += async (_, _) =>
        {
            var changed = await ShowScheduleEditor(owner, task);
            if (changed == null) return;
            int i = store.Tasks.IndexOf(task);
            if (i >= 0) store.Tasks[i] = changed;
            store.Save();
            rebuild();
        };
        var delete = Small("ScheduleDelete");
        delete.Click += (_, _) =>
        {
            store.Tasks.Remove(task);
            store.Save();
            rebuild();
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"), ColumnSpacing = 8 };
        grid.Children.Add(enabled);
        grid.Children.Add(info); Grid.SetColumn(info, 1);
        grid.Children.Add(run); Grid.SetColumn(run, 2);
        grid.Children.Add(edit); Grid.SetColumn(edit, 3);
        grid.Children.Add(delete); Grid.SetColumn(delete, 4);

        return new Border
        {
            Child = grid,
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(_isDark ? Color.FromRgb(44, 44, 46) : Colors.White),
        };
    }

    // ── Editor dialog ──

    /// <summary>Edits a copy of <paramref name="source"/> (or a new task); null when cancelled.</summary>
    private Task<ScheduledTask?> ShowScheduleEditor(Window owner, ScheduledTask? source)
    {
        var result = new TaskCompletionSource<ScheduledTask?>();
        var task = source == null
            ? new ScheduledTask { ProjectFolder = _projectFolder ?? "", At = DateTime.Today.AddHours(DateTime.Now.Hour + 1) }
            : new ScheduledTask
            {
                Id = source.Id, Name = source.Name, ProjectFolder = source.ProjectFolder, Prompt = source.Prompt,
                Kind = source.Kind, At = source.At, IntervalMinutes = source.IntervalMinutes,
                Enabled = source.Enabled, Created = source.Created, LastRun = source.LastRun,
            };

        TextBlock Label(string key) => new() { Text = Loc.Get(key), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };

        var name = new TextBox { Text = task.Name, Watermark = Loc.Get("ScheduleNameHint") };
        var folder = new TextBox { Text = task.ProjectFolder };
        var browse = new Button { Content = "…", Padding = new Thickness(10, 4) };
        browse.Click += async (_, _) =>
        {
            var picked = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
            if (picked.Count > 0) folder.Text = picked[0].Path.LocalPath;
        };
        var folderRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        folderRow.Children.Add(folder);
        folderRow.Children.Add(browse); Grid.SetColumn(browse, 1);

        var kind = new ComboBox
        {
            ItemsSource = new[] { Loc.Get("ScheduleKindOnce"), Loc.Get("ScheduleKindDaily"), Loc.Get("ScheduleKindInterval") },
            SelectedIndex = (int)task.Kind,
            MinWidth = 160,
        };
        var date = new TextBox { Text = task.At.ToString("yyyy/MM/dd"), Width = 110 };
        var time = new TextBox { Text = task.At.ToString("HH:mm"), Width = 70 };
        var minutes = new NumericUpDown { Value = task.IntervalMinutes, Minimum = 1, Maximum = 10080, Increment = 5, Width = 130, FormatString = "0" };
        var minutesUnit = Label("ScheduleMinutes");
        var when = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        when.Children.Add(kind);
        when.Children.Add(date);
        when.Children.Add(time);
        when.Children.Add(minutes);
        when.Children.Add(minutesUnit);
        void ShowFields()
        {
            date.IsVisible = kind.SelectedIndex == 0;
            time.IsVisible = kind.SelectedIndex != 2;
            minutes.IsVisible = minutesUnit.IsVisible = kind.SelectedIndex == 2;
        }
        kind.SelectionChanged += (_, _) => ShowFields();
        ShowFields();

        var prompt = new TextBox
        {
            Text = task.Prompt,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 150,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        var error = new TextBlock { Foreground = Brushes.IndianRed, FontSize = 12, IsVisible = false, TextWrapping = TextWrapping.Wrap };

        var save = new Button { Content = Loc.Get("ScheduleSave"), MinWidth = 100, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = Loc.Get("HandoffCancel"), MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);

        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(22, 20) };
        panel.Children.Add(Label("ScheduleName"));
        panel.Children.Add(name);
        panel.Children.Add(Label("ScheduleFolder"));
        panel.Children.Add(folderRow);
        panel.Children.Add(Label("ScheduleWhen"));
        panel.Children.Add(when);
        panel.Children.Add(Label("SchedulePrompt"));
        panel.Children.Add(prompt);
        panel.Children.Add(error);
        buttons.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(buttons);

        var dialog = CreateToolDialog(Loc.Get(source == null ? "ScheduleAddTitle" : "ScheduleEditTitle"), 560, 0);
        dialog.SizeToContent = SizeToContent.Height;
        dialog.Content = panel;

        void Fail(string key) { error.Text = Loc.Get(key); error.IsVisible = true; }
        save.Click += (_, _) =>
        {
            if (!Directory.Exists(folder.Text?.Trim())) { Fail("ScheduleBadFolder"); return; }
            if (string.IsNullOrWhiteSpace(prompt.Text)) { Fail("ScheduleNoPrompt"); return; }
            if (!TimeSpan.TryParseExact(time.Text?.Trim(), @"h\:mm", CultureInfo.InvariantCulture, out var tod)
                || tod >= TimeSpan.FromDays(1)) { Fail("ScheduleBadTime"); return; }
            var day = DateTime.Today;
            if (kind.SelectedIndex == 0
                && !DateTime.TryParseExact(date.Text?.Trim(), "yyyy/M/d", CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
            { Fail("ScheduleBadTime"); return; }

            var newKind = (ScheduleKind)kind.SelectedIndex;
            var newAt = day.Date + tod;
            // A changed schedule starts afresh: a one-off set again is due again, and the others
            // count from now instead of catching up on a slot that passed under the old settings.
            if (newKind != task.Kind || newAt != task.At || (int)(minutes.Value ?? 60) != task.IntervalMinutes)
                task.LastRun = newKind == ScheduleKind.Once ? null : DateTime.Now;
            task.Name = name.Text?.Trim() ?? "";
            task.ProjectFolder = folder.Text!.Trim();
            task.Prompt = prompt.Text!.Trim();
            task.Kind = newKind;
            task.At = newAt;
            task.IntervalMinutes = (int)(minutes.Value ?? 60);
            if (source == null) task.Created = DateTime.Now;
            result.TrySetResult(task);
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => result.TrySetResult(null);

        _ = dialog.ShowDialog(owner);
        return result.Task;
    }
}
