using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;

namespace Claucraft;

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
            // Apple Dark Mode
            Resources["ToolBarBg"] = new SolidColorBrush(Color.Parse("#2C2C2E"));
            Resources["StatusBarBg"] = new SolidColorBrush(Color.Parse("#1C1C1E"));
            Resources["SurfaceBg"] = new SolidColorBrush(Color.Parse("#000000"));
            Resources["SubtleText"] = new SolidColorBrush(Color.Parse("#98989D"));
            Resources["DividerColor"] = new SolidColorBrush(Color.Parse("#38383A"));
            Resources["ActivityBarBg"] = new SolidColorBrush(Color.Parse("#1C1C1E"));
            Resources["SidePanelBg"] = new SolidColorBrush(Color.Parse("#2C2C2E"));
        }
        else
        {
            // Apple Light Mode
            Resources["ToolBarBg"] = new SolidColorBrush(Color.Parse("#F2F2F7"));
            Resources["StatusBarBg"] = new SolidColorBrush(Color.Parse("#E5E5EA"));
            Resources["SurfaceBg"] = new SolidColorBrush(Color.Parse("#FFFFFF"));
            Resources["SubtleText"] = new SolidColorBrush(Color.Parse("#8E8E93"));
            Resources["DividerColor"] = new SolidColorBrush(Color.Parse("#C6C6C8"));
            Resources["ActivityBarBg"] = new SolidColorBrush(Color.Parse("#E5E5EA"));
            Resources["SidePanelBg"] = new SolidColorBrush(Color.Parse("#F2F2F7"));
        }
    }
}
