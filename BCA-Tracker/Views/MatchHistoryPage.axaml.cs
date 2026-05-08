using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using BCATracker.Core;
using BCATracker.UI.Controls;
using BCATracker.UI.Services;

namespace BCATracker.UI.Views;

public partial class MatchHistoryPage : UserControl
{
    List<MatchRecord> _all = new();
    string? _knownAccountId;
    bool _suppressFilterEvents = true;

    public MatchHistoryPage()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Load();
    }

    void Load()
    {
        _all = AppServices.Matches.LoadAll();
        _knownAccountId = Stats.FindKnownAccountId(_all);

        _suppressFilterEvents = true;
        PopulateFilter(MapFilter,  _all.Select(m => m.Map).Where(s => !string.IsNullOrEmpty(s)));
        PopulateFilter(ModeFilter, _all.Select(m => m.GameMode).Where(s => !string.IsNullOrEmpty(s)));
        _suppressFilterEvents = false;

        Render();
    }

    void PopulateFilter(ComboBox combo, IEnumerable<string> values)
    {
        var items = new List<ComboBoxItem> { new() { Content = "All", Tag = null } };
        foreach (string v in values.Distinct().OrderBy(s => s))
            items.Add(new ComboBoxItem { Content = v, Tag = v });
        combo.ItemsSource = items;
        combo.SelectedIndex = 0;
    }

    void Filter_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressFilterEvents) return;
        Render();
    }

    void Reset_Click(object? sender, RoutedEventArgs e)
    {
        _suppressFilterEvents = true;
        MapFilter.SelectedIndex  = 0;
        ModeFilter.SelectedIndex = 0;
        WinsOnlyCheck.IsChecked  = false;
        _suppressFilterEvents = false;
        Render();
    }

    void Render()
    {
        string? mapKey  = (MapFilter.SelectedItem  as ComboBoxItem)?.Tag as string;
        string? modeKey = (ModeFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        bool winsOnly = WinsOnlyCheck.IsChecked == true;

        IEnumerable<MatchRecord> filtered = _all;
        if (!string.IsNullOrEmpty(mapKey))  filtered = filtered.Where(m => m.Map      == mapKey);
        if (!string.IsNullOrEmpty(modeKey)) filtered = filtered.Where(m => m.GameMode == modeKey);
        if (winsOnly)                       filtered = filtered.Where(m => Stats.DidLocalWin(m, _knownAccountId) == true);

        var list = filtered.ToList();

        var cards = new List<MatchCardView>();
        foreach (MatchRecord m in list)
        {
            var card = new MatchCardView();
            card.Bind(m, _knownAccountId);
            card.Clicked += OnMatchCardClicked;
            cards.Add(card);
        }
        MatchList.ItemsSource = cards;

        SubtitleText.Text = list.Count == _all.Count
            ? $"Every saved match ({_all.Count})"
            : $"{list.Count} of {_all.Count} matches match the filters";
    }

    void OnMatchCardClicked(object? sender, MatchRecord m)
    {
        if (Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<MainWindow>(this) is MainWindow win)
            win.NavigateTo(new MatchDetailPage(m));
    }
}
