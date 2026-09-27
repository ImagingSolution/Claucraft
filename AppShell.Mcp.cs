using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Claucraft.Services;

namespace Claucraft;

/// <summary>
/// The MCP manager: every server <c>claude mcp list</c> knows with its live health, and adding,
/// inspecting and removing them through the CLI's own subcommands.
/// </summary>
internal partial class AppShell
{
    private void ShowMcpManager()
    {
        var folder = _projectFolder ?? Environment.CurrentDirectory;
        var list = new StackPanel { Spacing = 6 };
        var status = new TextBlock { FontSize = 12, Foreground = new SolidColorBrush(DialogSubtle()), TextWrapping = TextWrapping.Wrap };
        var detail = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily(_settings.FontFamily),
            FontSize = 12,
            Height = 130,
            IsVisible = false,
        };
        var dialog = CreateToolDialog(Loc.Get("McpManagerTitle"), 760, 640);

        async Task Reload()
        {
            list.Children.Clear();
            status.Text = Loc.Get("McpChecking");
            var (servers, error) = await McpService.ListAsync(_cli.Active, folder);
            status.Text = error != null
                ? error
                : servers.Count == 0 ? Loc.Get("McpNone") : string.Format(Loc.Get("McpCountFmt"), servers.Count);
            foreach (var s in servers)
                list.Children.Add(BuildMcpRow(s, folder, detail, status, Reload));
            // The extensions panel reads the same configs, so it has to catch up with any change
            _extensions = null;
            if (ExtensionsPanel.IsVisible) RefreshExtensionsPanel();
        }

        var add = new Button { Content = Loc.Get("McpAdd"), MinWidth = 110, HorizontalContentAlignment = HorizontalAlignment.Center };
        add.Click += async (_, _) =>
        {
            if (await ShowMcpAddDialog(dialog, folder)) await Reload();
        };
        var refresh = new Button { Content = Loc.Get("McpRefresh"), MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
        refresh.Click += async (_, _) => await Reload();
        var close = new Button { Content = Loc.Get("Close"), MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
        close.Click += (_, _) => dialog.Close();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(refresh);
        buttons.Children.Add(add);
        buttons.Children.Add(close);

        var top = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 10) };
        top.Children.Add(new TextBlock
        {
            Text = Loc.Get("McpManagerHint"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(DialogSubtle()),
        });
        top.Children.Add(status);

        var bottom = new StackPanel { Spacing = 0 };
        detail.Margin = new Thickness(0, 10, 0, 0);
        bottom.Children.Add(detail);
        bottom.Children.Add(buttons);

        var root = new DockPanel { Margin = new Thickness(22, 20) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { Content = list });
        dialog.Content = root;

        _ = dialog.ShowDialog(HostWindow);
        _ = Reload();
    }

    private Control BuildMcpRow(McpServer server, string folder, TextBox detail, TextBlock status, Func<Task> reload)
    {
        var color = server.Status switch
        {
            McpStatus.Connected => Color.FromRgb(48, 209, 88),
            McpStatus.NeedsAuth => Color.FromRgb(255, 159, 10),
            McpStatus.Failed => Color.FromRgb(255, 69, 58),
            _ => Color.FromRgb(142, 142, 147),
        };
        var dot = new TextBlock { Text = "●", Foreground = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        ToolTip.SetTip(dot, server.StatusText);

        var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = server.Name, FontSize = 13, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        info.Children.Add(new TextBlock
        {
            Text = server.StatusText + "  ·  " + server.Target,
            FontSize = 11,
            Foreground = new SolidColorBrush(DialogSubtle()),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        ToolTip.SetTip(info, server.Target);

        Button Small(string key) => new() { Content = Loc.Get(key), Padding = new Thickness(10, 3), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var details = Small("McpDetails");
        details.Click += async (_, _) =>
        {
            detail.IsVisible = true;
            detail.Text = Loc.Get("McpChecking");
            var r = await McpService.GetAsync(_cli.Active, folder, server.Name);
            detail.Text = r.Ok ? r.StdOut.Trim() : r.Message;
        };

        var remove = Small("McpRemove");
        bool removable = !server.Managed && !server.Name.StartsWith('-');
        remove.IsEnabled = removable;
        ToolTip.SetTip(remove, removable ? null : Loc.Get("McpManaged"));
        bool armed = false;
        remove.Click += async (_, _) =>
        {
            // Two presses: the first only asks, so a stray click cannot drop a server
            if (!armed) { armed = true; remove.Content = Loc.Get("McpRemoveConfirm"); return; }
            remove.IsEnabled = false;
            var r = await McpService.RemoveAsync(_cli.Active, folder, server.Name);
            if (!r.Ok) { status.Text = r.Message; remove.IsEnabled = true; return; }
            await reload();
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8 };
        grid.Children.Add(dot);
        grid.Children.Add(info); Grid.SetColumn(info, 1);
        grid.Children.Add(details); Grid.SetColumn(details, 2);
        grid.Children.Add(remove); Grid.SetColumn(remove, 3);
        return new Border
        {
            Child = grid,
            Padding = new Thickness(10, 7),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(_isDark ? Color.FromRgb(44, 44, 46) : Colors.White),
        };
    }

    /// <summary>Collects one server and runs <c>claude mcp add</c>; true once it was added.</summary>
    private Task<bool> ShowMcpAddDialog(Window owner, string folder)
    {
        var result = new TaskCompletionSource<bool>();
        TextBlock Label(string key) => new() { Text = Loc.Get(key), FontSize = 12 };

        var name = new TextBox { Watermark = "my-server" };
        var transport = new ComboBox { ItemsSource = new[] { "stdio", "http", "sse" }, SelectedIndex = 0, MinWidth = 120 };
        var scope = new ComboBox
        {
            ItemsSource = new[] { Loc.Get("McpScopeLocal"), Loc.Get("McpScopeProject"), Loc.Get("McpScopeUser") },
            SelectedIndex = 0,
            MinWidth = 220,
        };
        var scopes = new[] { "local", "project", "user" };
        var target = new TextBox();
        var args = new TextBox();
        var env = new TextBox { AcceptsReturn = true, Height = 70, VerticalContentAlignment = VerticalAlignment.Top, Watermark = "API_KEY=..." };
        var headers = new TextBox { AcceptsReturn = true, Height = 70, VerticalContentAlignment = VerticalAlignment.Top, Watermark = "Authorization: Bearer ..." };
        var targetLabel = Label("McpCommand");
        var argsLabel = Label("McpArgs");
        var envLabel = Label("McpEnv");
        var headersLabel = Label("McpHeaders");

        void ShowFields()
        {
            bool stdio = transport.SelectedIndex == 0;
            targetLabel.Text = Loc.Get(stdio ? "McpCommand" : "McpUrl");
            target.Watermark = stdio ? "npx -y @modelcontextprotocol/server-filesystem" : "https://example.com/mcp";
            argsLabel.IsVisible = args.IsVisible = envLabel.IsVisible = env.IsVisible = stdio;
            headersLabel.IsVisible = headers.IsVisible = !stdio;
        }
        transport.SelectionChanged += (_, _) => ShowFields();
        ShowFields();

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var tCol = new StackPanel { Spacing = 4 }; tCol.Children.Add(Label("McpTransport")); tCol.Children.Add(transport);
        var sCol = new StackPanel { Spacing = 4 }; sCol.Children.Add(Label("McpScope")); sCol.Children.Add(scope);
        row.Children.Add(tCol);
        row.Children.Add(sCol);

        var error = new TextBlock { Foreground = Brushes.IndianRed, FontSize = 12, IsVisible = false, TextWrapping = TextWrapping.Wrap };
        var ok = new Button { Content = Loc.Get("McpAdd"), MinWidth = 100, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = Loc.Get("HandoffCancel"), MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(22, 20) };
        panel.Children.Add(Label("McpName"));
        panel.Children.Add(name);
        panel.Children.Add(row);
        panel.Children.Add(targetLabel);
        panel.Children.Add(target);
        panel.Children.Add(argsLabel);
        panel.Children.Add(args);
        panel.Children.Add(envLabel);
        panel.Children.Add(env);
        panel.Children.Add(headersLabel);
        panel.Children.Add(headers);
        panel.Children.Add(error);
        panel.Children.Add(buttons);

        var dialog = CreateToolDialog(Loc.Get("McpAddTitle"), 560, 0);
        dialog.SizeToContent = SizeToContent.Height;
        dialog.Content = panel;

        void Fail(string text) { error.Text = text; error.IsVisible = true; }
        ok.Click += async (_, _) =>
        {
            var n = name.Text?.Trim() ?? "";
            var t = target.Text?.Trim() ?? "";
            if (!McpService.IsValidName(n)) { Fail(Loc.Get("McpBadName")); return; }
            if (t.Length == 0) { Fail(Loc.Get("McpNoTarget")); return; }
            bool stdio = transport.SelectedIndex == 0;
            if (!stdio && !(Uri.TryCreate(t, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https")))
            { Fail(Loc.Get("McpBadUrl")); return; }
            var envLines = McpService.Lines(env.Text);
            if (stdio && envLines.Any(l => !l.Contains('=') || l.StartsWith('='))) { Fail(Loc.Get("McpBadEnv")); return; }

            // A stdio command typed with its arguments on one line is split the way a shell would
            var command = stdio ? CliOneShotRunner.SplitArgs(t) : new[] { t };
            if (command.Length == 0) { Fail(Loc.Get("McpNoTarget")); return; }
            var req = new McpAddRequest(
                n, (string)transport.SelectedItem!, scopes[Math.Max(0, scope.SelectedIndex)], command[0],
                stdio ? command.Skip(1).Concat(CliOneShotRunner.SplitArgs(args.Text ?? "")).ToList() : Array.Empty<string>(),
                stdio ? envLines : Array.Empty<string>(),
                stdio ? Array.Empty<string>() : McpService.Lines(headers.Text));

            ok.IsEnabled = false;
            var r = await McpService.AddAsync(_cli.Active, folder, req);
            ok.IsEnabled = true;
            if (!r.Ok) { Fail(r.Message); return; }
            result.TrySetResult(true);
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => result.TrySetResult(false);

        _ = dialog.ShowDialog(owner);
        return result.Task;
    }
}
