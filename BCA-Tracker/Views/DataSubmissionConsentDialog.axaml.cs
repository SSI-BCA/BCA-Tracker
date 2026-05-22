using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace BCATracker.UI.Views;

public partial class DataSubmissionConsentDialog : Window
{
    /// <summary>
    /// True/false set when the user clicks one of the buttons. Null if the
    /// user closed the dialog with the X button — the caller should treat
    /// that as "ask again next launch" rather than "no thanks".
    /// </summary>
    public bool? UserConsented { get; private set; }

    public DataSubmissionConsentDialog()
    {
        InitializeComponent();
    }

    void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    void AcceptButton_Click(object? sender, RoutedEventArgs e)
    {
        UserConsented = true;
        Close();
    }

    void DeclineButton_Click(object? sender, RoutedEventArgs e)
    {
        UserConsented = false;
        Close();
    }
}
