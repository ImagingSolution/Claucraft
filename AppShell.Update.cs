using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Claucraft.Services;

namespace Claucraft;

/// <summary>
/// The update notice: the corner of the shell that says a newer release exists and installs it.
///
/// The order of the steps is the whole design. The executable is fetched and verified while the
/// application is still running, so cancelling or a broken connection costs nothing; only once
/// there is a whole new executable on disk does anything close. What the user sees is the flow
/// they asked for - press the button, the application closes, the new version comes up - with no
/// window in which a failed download could leave them with nothing to start.
/// </summary>
internal partial class AppShell
{
    private UpdateInfo? _update;
    private CancellationTokenSource? _updateDownload;

    /// <summary>
    /// The percentage last painted. 80MB arrives in 64KB reads, and repainting the bar on every
    /// one of them is 1,300 layout passes to show the same hundred positions.
    /// </summary>
    private int _updatePercent = -1;

    /// <summary>
    /// Asks GitHub whether there is a newer release and raises the notice if there is. Fired
    /// rather than awaited: an unreachable API must not hold up the window coming up, and a
    /// check that finds nothing says nothing.
    /// </summary>
    private void StartUpdateCheck()
    {
        // Whatever the last update left behind, cleared on the run after it - the old executable
        // could not be deleted while it was the one running.
        UpdateService.CleanupOldExe();

        if (!_settings.CheckUpdateOnStartup) return;
        if (!UpdateService.CanCheck) return;

        _ = Task.Run(async () =>
        {
            var info = await UpdateService.CheckAsync().ConfigureAwait(false);
            if (info is null) return;
            Dispatcher.UIThread.Post(() => ShowUpdateBanner(info));
        });
    }

    private void ShowUpdateBanner(UpdateInfo info)
    {
        _update = info;

        UpdateTitleText.Text = Loc.Get("UpdateTitle");
        UpdateVersionText.Text = string.Format(
            Loc.Get("UpdateVersionFmt"), UpdateService.CurrentVersion, info.Version);

        UpdateNotes.Text = info.ReleaseNotes;
        UpdateNotesScroll.IsVisible = info.ReleaseNotes.Length > 0;

        UpdateError.IsVisible = false;
        ResetUpdateBanner();
        UpdateBanner.IsVisible = true;
    }

    /// <summary>Downloads the new executable, then hands over to the swap.</summary>
    private async void OnUpdateApply(object? sender, RoutedEventArgs e)
    {
        if (_update is not { } info || _updateDownload != null) return;

        // The sessions are the work in progress. Ending them is the decision of the person who
        // started them, so it is asked rather than assumed.
        var sessions = Shells.Sum(s => s._children.Count);
        if (sessions > 0)
        {
            var proceed = await ShowConfirmDialog(
                Loc.Get("UpdateTitle"),
                string.Format(Loc.Get("UpdateSessionsRunningFmt"), sessions));
            if (!proceed) return;
        }

        // Asked before the download rather than after it: an install under Program Files should
        // say so in a second, not 80MB later.
        if (!UpdateService.CanWriteToInstallDir())
        {
            ShowUpdateFailure(Loc.Get("UpdateNoPermission"));
            OpenReleasePage();
            return;
        }

        var cts = new CancellationTokenSource();
        _updateDownload = cts;
        _updatePercent = -1;

        BtnUpdateApply.IsEnabled = false;
        UpdateError.IsVisible = false;
        UpdateProgress.Value = 0;
        UpdateProgress.IsVisible = true;
        UpdateProgressText.Text = "";
        UpdateProgressText.IsVisible = true;
        LblUpdateCancel.Text = Loc.Get("UpdateAbort");

        string staged;
        try
        {
            // Constructed on the UI thread, so its callbacks arrive there too.
            var progress = new Progress<(long Read, long Total)>(ReportUpdateProgress);
            staged = await UpdateService.DownloadAsync(info, progress, cts.Token);
        }
        catch (OperationCanceledException)
        {
            ResetUpdateBanner();
            return;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppShell] Update download failed: {ex.GetType().Name}");
            ResetUpdateBanner();
            ShowUpdateFailure(Loc.Get("UpdateFailed"));
            return;
        }
        finally
        {
            _updateDownload = null;
            cts.Dispose();
        }

        // Only the published single-file build replaces itself. A development copy has come this
        // far to prove the notice and the download work, and stops before touching the folder it
        // was built into.
        if (!UpdateService.CanSelfUpdate)
        {
            UpdateService.CleanupOldExe();
            ResetUpdateBanner();
            ShowUpdateFailure(Loc.Get("UpdateNoPermission"));
            OpenReleasePage();
            return;
        }

        await ApplyUpdateAndRestartAsync(staged);
    }

    /// <summary>
    /// Winds the application down, replaces the executable, and starts the new one.
    ///
    /// The shutdown mode is pinned first, and that is not incidental: the default ends the
    /// process the moment the last window closes, which is several lines before the swap.
    /// </summary>
    private async Task ApplyUpdateAndRestartAsync(string staged)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Sends /exit to every terminal and waits for them, saves the settings, closes the windows.
        await ShutdownApplicationAsync();

        try
        {
            UpdateService.SwapAndRestart(staged);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppShell] Update swap failed: {ex.GetType().Name}");
            // The old executable has been put back, but the windows are gone and there is nothing
            // left to show a message in. Leave the user at the release page instead.
            OpenReleasePage();
        }

        desktop.Shutdown();
    }

    private void ReportUpdateProgress((long Read, long Total) p)
    {
        if (p.Total <= 0) return;

        var percent = (int)(p.Read * 100 / p.Total);
        if (percent == _updatePercent) return;
        _updatePercent = percent;

        UpdateProgress.Value = percent;
        UpdateProgressText.Text = string.Format(
            Loc.Get("UpdateDownloadingFmt"),
            UpdateService.FormatSize(p.Read),
            UpdateService.FormatSize(p.Total));
    }

    /// <summary>
    /// Dismisses the notice, and stops the download if one is under way. Nothing is remembered:
    /// the next start asks again, which is what makes this the light half of the choice.
    /// </summary>
    private void OnUpdateCancel(object? sender, RoutedEventArgs e)
    {
        _updateDownload?.Cancel();
        UpdateBanner.IsVisible = false;
    }

    private void OnUpdateNotesLink(object? sender, RoutedEventArgs e) => OpenReleasePage();

    private void OpenReleasePage()
    {
        var url = _update?.HtmlUrl ?? UpdateService.ReleasesUrl;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppShell] Could not open the release page: {ex.GetType().Name}");
        }
    }

    /// <summary>Back to the state the notice opens in: offer the update, no progress, no error.</summary>
    private void ResetUpdateBanner()
    {
        UpdateProgress.IsVisible = false;
        UpdateProgressText.IsVisible = false;
        BtnUpdateApply.IsEnabled = true;
        LblUpdateCancel.Text = Loc.Get("Cancel");
    }

    private void ShowUpdateFailure(string message)
    {
        UpdateError.Text = message;
        UpdateError.IsVisible = true;
        UpdateBanner.IsVisible = true;
    }

    private void OnCheckUpdateSettingChanged(object? sender, RoutedEventArgs e)
    {
        if (!_settingsInitialized || _suppressSettingsChanged) return;

        _settings.CheckUpdateOnStartup = ChkCheckUpdate.IsChecked == true;
        _settings.Save();
    }
}
