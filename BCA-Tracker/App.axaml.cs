using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BCATracker.UI.Services;

namespace BCATracker.UI;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Wire crash plumbing first.
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException     += OnTaskUnhandledException;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Switch to explicit shutdown — by default Avalonia shuts the app
            // down when the desktop's MainWindow closes. We have a launcher
            // that closes BEFORE MainWindow opens, so the default shuts us
            // down between the two windows. We control shutdown manually.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            try
            {
                AppServices.Initialise();
                AppSettings settings = AppServices.Settings;

                // Forcing the launcher: useful when "Don't ask again" was set
                // to Legacy and the user can't reach the GUI to undo it.
                //   --launcher  CLI flag (works for shortcuts)
                //   Shift held  on launch (works without modifying anything)
                bool forceLauncher = false;
                string[] args = (desktop.Args as string[]) ?? Array.Empty<string>();
                foreach (string a in args)
                    if (string.Equals(a, "--launcher", StringComparison.OrdinalIgnoreCase))
                        forceLauncher = true;
                if (!forceLauncher && IsShiftHeld())
                    forceLauncher = true;

                if (settings.RememberLauncherChoice && !forceLauncher)
                {
                    LaunchChoice(desktop, settings.PreferredUI);
                }
                else
                {
                    var launcher = new LauncherWindow(settings);

                    launcher.PickConfirmed += (_, choice) =>
                    {
                        // Launch the chosen UI BEFORE closing the launcher,
                        // so there's always at least one window alive on the
                        // desktop (cosmetic — avoids a brief no-window blink).
                        LaunchChoice(desktop, choice);
                        launcher.Close();
                    };
                    launcher.Closed += (_, _) =>
                    {
                        // If the launcher closed without a choice (X button),
                        // we need to shut the app down — there's nothing else
                        // alive. PickWasMade gates this.
                        if (!launcher.PickWasMade)
                            desktop.Shutdown();
                    };

                    launcher.Show();
                }
            }
            catch (Exception ex)
            {
                ReportFatal("Startup failed", ex);
                desktop.Shutdown(1);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    static void LaunchChoice(IClassicDesktopStyleApplicationLifetime desktop, UIChoice choice)
    {
        switch (choice)
        {
            case UIChoice.Modern:
                var main = new MainWindow();
                desktop.MainWindow = main;
                // We're in OnExplicitShutdown mode so closing this window
                // doesn't auto-shutdown — wire it ourselves.
                main.Closed += (_, _) => desktop.Shutdown();
                main.Show();
                break;

            case UIChoice.Legacy:
                LaunchLegacyAndExit(desktop);
                break;

            default:
                desktop.Shutdown();
                break;
        }
    }

    static void LaunchLegacyAndExit(IClassicDesktopStyleApplicationLifetime desktop)
    {
        string? legacy = AppPaths.LegacyExePath;
        if (legacy is null || !File.Exists(legacy))
        {
            // We can't show MessageBox in Avalonia natively without a package.
            // Fall back to writing to crash log and shutting down.
            ReportFatal("Could not find BCA-Tracker.Legacy.exe",
                        new FileNotFoundException("Looked at: " + (legacy ?? "<null>")));
            desktop.Shutdown(1);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = legacy,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(legacy)!,
            });
        }
        catch (Exception ex)
        {
            ReportFatal("Failed to start the legacy console", ex);
        }

        desktop.Shutdown();
    }

    // ── Crash plumbing ───────────────────────────────────────────────────────

    /// <summary>True if either Shift key was held when the process started
    /// (we test on the first dispatcher tick). Lets users force the launcher
    /// to show even when "Don't ask again" was checked previously.</summary>
    static bool IsShiftHeld()
    {
        try
        {
            // VK_SHIFT = 0x10. High bit of return = currently down.
            return (GetAsyncKeyState(0x10) & 0x8000) != 0;
        }
        catch
        {
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

    static bool s_crashShown;

    void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (s_crashShown) return;
        s_crashShown = true;
        Exception? ex = e.ExceptionObject as Exception;
        ReportFatal("Unhandled background exception", ex ?? new Exception("non-CLS exception"));
    }

    void OnTaskUnhandledException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        if (s_crashShown) return;
        s_crashShown = true;
        ReportFatal("Unobserved task exception", e.Exception);
    }

    /// <summary>
    /// Avalonia doesn't have a built-in MessageBox. We log to %AppData% and
    /// rely on the user picking it up from there. (We could add the
    /// MessageBox.Avalonia package later if dialogs become useful enough.)
    /// </summary>
    public static void ReportFatal(string title, Exception ex)
    {
        string crashPath = Path.Combine(AppPaths.AppDataFolder, "crash.log");
        try
        {
            Directory.CreateDirectory(AppPaths.AppDataFolder);
            var sb = new StringBuilder();
            sb.AppendLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            sb.AppendLine(title);
            sb.AppendLine();
            sb.AppendLine(ex.ToString());
            sb.AppendLine();
            File.AppendAllText(crashPath, sb.ToString());
        }
        catch
        {
            // best-effort
        }
    }
}
