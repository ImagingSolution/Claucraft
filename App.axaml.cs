using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;

namespace ClaudeCodeMDI;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    public void SetTheme(bool isDark)
    {
        RequestedThemeVariant = isDark ? ThemeVariant.Dark : ThemeVariant.Light;

        if (isDark)
        {
            Resources["ToolBarBg"] = new SolidColorBrush(Color.Parse("#1A1A2E"));
            Resources["StatusBarBg"] = new SolidColorBrush(Color.Parse("#0F0F1A"));
            Resources["SurfaceBg"] = new SolidColorBrush(Color.Parse("#16162A"));
            Resources["SubtleText"] = new SolidColorBrush(Color.Parse("#80FFFFFF"));
            Resources["DividerColor"] = new SolidColorBrush(Color.Parse("#20FFFFFF"));
        }
        else
        {
            Resources["ToolBarBg"] = new SolidColorBrush(Color.Parse("#F0F0F5"));
            Resources["StatusBarBg"] = new SolidColorBrush(Color.Parse("#E8E8EE"));
            Resources["SurfaceBg"] = new SolidColorBrush(Color.Parse("#FAFAFA"));
            Resources["SubtleText"] = new SolidColorBrush(Color.Parse("#606070"));
            Resources["DividerColor"] = new SolidColorBrush(Color.Parse("#D0D0DA"));
        }
    }
}
