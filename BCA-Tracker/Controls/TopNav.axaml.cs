using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace BCATracker.UI.Controls;

public partial class TopNav : UserControl
{
    readonly List<NavItem> _items = new();

    // Each NavItem is bound to a (button, label, underline) trio so we can
    // restyle on active/inactive without rebuilding the visual tree.
    record TabVisual(Button Button, TextBlock Label, Border Underline);
    readonly Dictionary<NavItem, TabVisual> _visuals = new();

    public IList<NavItem> Items => _items;

    public event EventHandler<NavItem>? Navigated;

    public TopNav()
    {
        InitializeComponent();
    }

    public void RefreshTabs()
    {
        TabsHost.Children.Clear();
        _visuals.Clear();

        foreach (NavItem item in _items)
        {
            // Each tab is a Button containing a Grid (label + underline).
            var label = new TextBlock
            {
                Text = item.Label,
                FontSize = 13,
                FontWeight = FontWeight.Medium,
                Foreground = GetBrush("Fg.Secondary"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            var underline = new Border
            {
                Height = 2,
                Background = GetBrush("Accent"),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(12, 0),
                IsVisible = false,
            };

            var grid = new Grid();
            grid.Children.Add(label);
            grid.Children.Add(underline);

            var btn = new Button
            {
                Content = grid,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                Padding = new Thickness(16, 0),
                MinHeight = 48,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                VerticalAlignment = VerticalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            btn.Click += (_, _) => Pick(item);

            // Hover effect: lighten the label.
            btn.PointerEntered += (_, _) =>
            {
                if (!IsActive(item)) label.Foreground = GetBrush("Fg.Primary");
            };
            btn.PointerExited += (_, _) =>
            {
                if (!IsActive(item)) label.Foreground = GetBrush("Fg.Secondary");
            };

            _visuals[item] = new TabVisual(btn, label, underline);
            TabsHost.Children.Add(btn);
        }
    }

    public void SetActive(Type pageType)
    {
        foreach (var (item, v) in _visuals)
        {
            bool isActive = item.PageType == pageType;
            v.Label.Foreground = isActive ? GetBrush("Fg.Primary") : GetBrush("Fg.Secondary");
            v.Label.FontWeight = isActive ? FontWeight.SemiBold : FontWeight.Medium;
            v.Underline.IsVisible = isActive;
        }
    }

    bool IsActive(NavItem item)
        => _visuals.TryGetValue(item, out var v) && v.Underline.IsVisible;

    void Pick(NavItem item)
    {
        SetActive(item.PageType);
        Navigated?.Invoke(this, item);
    }

    static IBrush GetBrush(string key)
    {
        if (Application.Current?.Resources.TryGetResource(key, null, out object? value) == true
            && value is IBrush brush)
            return brush;
        return Brushes.Gray;
    }
}
