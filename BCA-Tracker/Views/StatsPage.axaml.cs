using Avalonia.Controls;
using Avalonia.Interactivity;

namespace BCATracker.UI.Views;

/// <summary>
/// Stats shell: segmented button switcher over all analysis sections.
/// All real logic lives in the child UserControls; this container just
/// decides which one to show.
/// </summary>
public partial class StatsPage : UserControl
{
    /// <summary>
    /// Which section a newly-created StatsPage should land on. Set by
    /// callers like BucketDetailPage's Back button before navigating
    /// here; consumed and cleared in the constructor so subsequent
    /// normal navs (clicking "Stats" in the sidebar) open Trends.
    /// </summary>
    public static StatsTab? InitialTab { get; set; }

    public enum StatsTab { Trends, Maps, Weapons, Sessions, Records, Loadouts, Rivals }

    // Cached child instances. Created once on first use and reused
    // across switches so scroll position, hovered chart points,
    // selected filters etc. persist within a session.
    TrendsPage?   _trends;
    MapsPage?     _maps;
    WeaponsPage?  _weapons;
    SessionsPage? _sessions;
    RecordsPage?  _records;
    LoadoutsPage? _loadouts;
    RivalsPage?   _rivals;

    public StatsPage()
    {
        InitializeComponent();

        var startTab = InitialTab ?? StatsTab.Trends;
        InitialTab = null;

        AttachedToVisualTree += (_, _) => ShowSection(startTab);
    }

    void Section_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;
        var t = tag switch
        {
            "maps"     => StatsTab.Maps,
            "weapons"  => StatsTab.Weapons,
            "sessions" => StatsTab.Sessions,
            "records"  => StatsTab.Records,
            "loadouts" => StatsTab.Loadouts,
            "rivals"   => StatsTab.Rivals,
            _          => StatsTab.Trends,
        };
        ShowSection(t);
    }

    void ShowSection(StatsTab tab)
    {
        UserControl page = tab switch
        {
            StatsTab.Maps     => _maps     ??= new MapsPage(),
            StatsTab.Weapons  => _weapons  ??= new WeaponsPage(),
            StatsTab.Sessions => _sessions ??= new SessionsPage(),
            StatsTab.Records  => _records  ??= new RecordsPage(),
            StatsTab.Loadouts => _loadouts ??= new LoadoutsPage(),
            StatsTab.Rivals   => _rivals   ??= new RivalsPage(),
            _                 => _trends   ??= new TrendsPage(),
        };

        // Hide the inner page's own "page-title" since the StatsPage
        // header already labels what we're looking at. If the inner
        // page doesn't expose a PageTitleText name we just skip.
        var innerTitle = page.FindControl<TextBlock>("PageTitleText");
        if (innerTitle is not null) innerTitle.IsVisible = false;

        SectionHost.Content = page;

        // Active button styling: inactive sections get "ghost", active
        // loses it so the stylesheet renders it filled.
        SectionTrendsBtn  .Classes.Set("ghost", tab != StatsTab.Trends);
        SectionMapsBtn    .Classes.Set("ghost", tab != StatsTab.Maps);
        SectionWeaponsBtn .Classes.Set("ghost", tab != StatsTab.Weapons);
        SectionSessionsBtn.Classes.Set("ghost", tab != StatsTab.Sessions);
        SectionRecordsBtn .Classes.Set("ghost", tab != StatsTab.Records);
        SectionLoadoutsBtn.Classes.Set("ghost", tab != StatsTab.Loadouts);
        SectionRivalsBtn  .Classes.Set("ghost", tab != StatsTab.Rivals);
    }
}
