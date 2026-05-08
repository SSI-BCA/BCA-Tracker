using System;
using Avalonia;

namespace BCATracker.UI;

public static class Program
{
    /// <summary>
    /// Avalonia entry point. Equivalent to WPF's hidden Main from
    /// generated App.g.cs — but in Avalonia we own it.
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
        => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    /// <summary>Used by the XAML previewer in IDEs (Rider, VS, VS Code).</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder
            .Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
