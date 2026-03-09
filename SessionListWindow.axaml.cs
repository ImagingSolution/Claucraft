using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ClaudeCodeMDI.Services;

namespace ClaudeCodeMDI;

public partial class SessionListWindow : Window
{
    private readonly List<SessionInfo> _sessions;

    public SessionListWindow()
    {
        _sessions = new List<SessionInfo>();
        InitializeComponent();
    }

    public SessionListWindow(List<SessionInfo> sessions)
    {
        _sessions = sessions;
        InitializeComponent();
        SessionList.ItemsSource = sessions;
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        BtnResume.IsEnabled = SessionList.SelectedItem != null;
    }

    private void OnResume(object? sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is SessionInfo session)
        {
            Close(session);
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}
