using Avalonia;
using System;

namespace Claucraft;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Claude Code runs this on every status line refresh. It has to answer before the debug
        // banner below could reach stdout, and without paying for the UI it never shows.
        if (args.Length > 0 && args[0] == Services.StatusLineRelay.Flag)
        {
            Environment.Exit(Services.StatusLineRelay.Run());
            return;
        }

        RunApp(args);
    }

    /// <summary>Kept out of Main so a status line run never loads the Avalonia assemblies.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RunApp(string[] args)
    {
#if DEBUG
        // Debugビルド時はコンソールウィンドウにDebug出力を表示
        System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.ConsoleTraceListener());
        Console.WriteLine("=== Debug Console ===");
#endif
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
