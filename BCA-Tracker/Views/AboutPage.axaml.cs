using System;
using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace BCATracker.UI.Views;

public partial class AboutPage : UserControl
{
    const string GitHubBase = "https://github.com/SSI-BCA/BCA-Tracker";

    public AboutPage()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => PopulateVersion();
    }

    void PopulateVersion()
    {
        try
        {
            // Read version from the assembly. The .csproj sets
            // VersionPrefix/VersionSuffix so this comes back as e.g.
            // "0.15.0-beta1".
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            string v = info?.InformationalVersion ?? asm.GetName().Version?.ToString() ?? "(unknown)";
            // The SDK sometimes appends a build-metadata suffix like
            // "+abcd1234" (the source-commit hash). It looks ugly to
            // end users, so trim it.
            int plus = v.IndexOf('+');
            if (plus >= 0) v = v.Substring(0, plus);
            VersionText.Text = v;
        }
        catch
        {
            VersionText.Text = "(unknown)";
        }
    }

    void GitHub_Click(object? sender, RoutedEventArgs e)
        => OpenUrl(GitHubBase);

    void Issues_Click(object? sender, RoutedEventArgs e)
        => OpenUrl(GitHubBase + "/issues");

    void Releases_Click(object? sender, RoutedEventArgs e)
        => OpenUrl(GitHubBase + "/releases");

    static void OpenUrl(string url)
    {
        // Process.Start with UseShellExecute=true is how you open a
        // URL with the default browser on Windows from a non-Win32
        // .NET app. Wrapped in try/catch because a borked default
        // browser association would otherwise crash the click.
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = url,
                UseShellExecute = true,
            });
        }
        catch { }
    }
}
