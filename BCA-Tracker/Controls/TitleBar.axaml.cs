using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace BCATracker.UI.Controls;

public partial class TitleBar : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<TitleBar, string>(nameof(Title), "BCA-Tracker");

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public TitleBar() => InitializeComponent();


    Window? _hostWindow;

    /// <summary>
    /// Override property-changed instead of subscribing to an observable —
    /// it's the canonical Avalonia pattern for reacting to your own
    /// StyledProperty values.
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TitleProperty && TitleText is not null)
            TitleText.Text = (string?)change.NewValue ?? "";
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Pick up the initial title value (OnPropertyChanged only fires on
        // changes; the first assignment from XAML may have happened before
        // TitleText was constructed).
        if (TitleText is not null) TitleText.Text = Title;

        _hostWindow = TopLevel.GetTopLevel(this) as Window;
        if (_hostWindow is not null)
        {
            _hostWindow.PropertyChanged += (_, args) =>
            {
                if (args.Property == Window.WindowStateProperty) UpdateMaxIcon();
            };
            UpdateMaxIcon();
        }
    }

    void UpdateMaxIcon()
    {
        if (_hostWindow is null || MaxIcon is null) return;
        MaxIcon.Data = _hostWindow.WindowState == WindowState.Maximized
            ? Geometry.Parse("M 2,0 H 10 V 8 M 0,2 H 8 V 10 H 0 Z")  // restore (two squares)
            : Geometry.Parse("M 0,0 H 10 V 10 H 0 Z");                // maximise (single square)
    }

    void DragRegion_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Double-click toggles maximise. Single-click drags.
        if (e.ClickCount == 2)
        {
            if (_hostWindow is not null) ToggleMaximise();
            return;
        }
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            _hostWindow?.BeginMoveDrag(e);
    }

    void Min_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_hostWindow is not null) _hostWindow.WindowState = WindowState.Minimized;
    }

    void Max_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ToggleMaximise();

    void ToggleMaximise()
    {
        if (_hostWindow is null) return;
        _hostWindow.WindowState = _hostWindow.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    void Close_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _hostWindow?.Close();
}
