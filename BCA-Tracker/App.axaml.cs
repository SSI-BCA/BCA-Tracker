using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using BCATracker.UI.Services;
using BCATracker.UI.Views;

namespace BCATracker.UI;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException     += OnTaskUnhandledException;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                AppServices.Initialise();

                var main = new MainWindow();
                desktop.MainWindow = main;
                main.Show();

                // First-run consent for data submission. Deferred until the
                // main window is shown so it appears as a modal child rather
                // than a free-floating window.
                if (!AppServices.Settings.DataSubmissionAsked)
                {
                    Dispatcher.UIThread.Post(async () =>
                    {
                        await ShowDataSubmissionConsentDialog(main);
                    }, DispatcherPriority.Background);
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

    static async Task ShowDataSubmissionConsentDialog(Window owner)
    {
        try
        {
            var dlg = new DataSubmissionConsentDialog();
            await dlg.ShowDialog(owner);

            // Whether the user opted in, opted out, or closed the dialog,
            // we mark it as asked so we don't pester them next launch.
            AppServices.Settings.DataSubmissionAsked = true;
            if (dlg.UserConsented.HasValue)
                AppServices.Settings.DataSubmissionEnabled = dlg.UserConsented.Value;
            AppServices.Settings.Save();
            AppServices.ApplyUploaderConfig();
        }
        catch (Exception ex)
        {
            // Don't crash startup over a consent dialog. The user can still
            // toggle the option from Settings later, and we'll just ask again
            // on next launch since DataSubmissionAsked stays false.
            ReportFatal("Consent dialog failed", ex);
        }
    }

    // ── Crash plumbing ───────────────────────────────────────────────────────

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
