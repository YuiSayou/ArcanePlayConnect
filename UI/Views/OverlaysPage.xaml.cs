using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using ArcanePlayConnect.Core;
using ArcanePlayConnect.Core.Models;
using ArcanePlayConnect.Services;
using ArcanePlayConnect.UI.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using IOPath = System.IO.Path;
using IODirectory = System.IO.Directory;
using IOFile = System.IO.File;

namespace ArcanePlayConnect.UI.Views;

public sealed partial class OverlaysPage : Page
{
    public MainViewModel ViewModel { get; } = MainViewModel.Instance;

    private readonly ObservableCollection<OverlayDisplayItem> _overlayItems = new();
    private readonly List<TikTokGift> _selectedGifts = new();

    private string _selectedLayout = "Vertical";
    private string _selectedTheme = "Cyberpunk";

    // Theme definitions for the preview picker
    private static readonly ThemePreviewInfo[] _themes =
    [
        new("Cyberpunk",    "⚡ Cyberpunk",     "#00C8FF", "#B400FF", "#FF3278"),
        new("NeonFire",     "🔥 Neon Fire",     "#FF6B00", "#FF0044", "#FFCC00"),
        new("ArcticFrost",  "❄ Arctic Frost",  "#88DDFF", "#44AAFF", "#FFFFFF"),
        new("DragonForge",  "🐉 Dragon Forge",  "#FF4400", "#884400", "#FFAA00"),
        new("SakuraBloom",  "🌸 Sakura Bloom",  "#FF88B4", "#CC44AA", "#FFCCDD"),
        new("VoidShadow",   "🌑 Void Shadow",   "#AA44FF", "#4400AA", "#DD88FF"),
    ];

    public OverlaysPage()
    {
        InitializeComponent();
        OverlayListView.ItemsSource = _overlayItems;
        ViewModel.OverlayServer.StatusChanged += OnServerStatusChanged;

        BuildThemePicker();
        LoadOverlays();
        UpdateServerStatus();
        UpdateEmptyState();
        UpdateLayoutSelection();
        UpdateThemeSelection();

        // Preload gift images in background
        _ = GiftImageService.PreloadAllAsync();
    }

    // ── Theme picker ──

    private void BuildThemePicker()
    {
        ThemePicker.Items.Clear();
        foreach (var t in _themes)
        {
            var card = CreateThemeCard(t);
            ThemePicker.Items.Add(card);
        }
    }

    private static Border CreateThemeCard(ThemePreviewInfo t)
    {
        var c1 = ParseColor(t.Color1);
        var c2 = ParseColor(t.Color2);
        var c3 = ParseColor(t.Color3);

        var card = new Border
        {
            Tag = t.Key,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 255, 255)),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 22, 22, 42)),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        var stack = new StackPanel { Spacing = 4, Padding = new Thickness(8, 6, 8, 6) };

        // Color bar preview
        var barGrid = new Grid { Height = 24, ColumnSpacing = 3 };
        barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var bar1 = new Border { CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(c1) };
        var bar2 = new Border { CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(c2) };
        var bar3 = new Border { CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(c3) };
        Grid.SetColumn(bar1, 0); Grid.SetColumn(bar2, 1); Grid.SetColumn(bar3, 2);
        barGrid.Children.Add(bar1); barGrid.Children.Add(bar2); barGrid.Children.Add(bar3);
        stack.Children.Add(barGrid);

        // Gradient line
        var gradLine = new Border
        {
            Height = 3, CornerRadius = new CornerRadius(1), Margin = new Thickness(0, 2, 0, 0),
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 0),
                GradientStops = {
                    new GradientStop { Color = c1, Offset = 0 },
                    new GradientStop { Color = c2, Offset = 0.5 },
                    new GradientStop { Color = c3, Offset = 1 },
                }
            }
        };
        stack.Children.Add(gradLine);

        // Label
        var label = new TextBlock
        {
            Text = t.Label,
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(c1),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0)
        };
        stack.Children.Add(label);

        card.Child = stack;
        return card;
    }

    private static Windows.UI.Color ParseColor(string hex)
    {
        hex = hex.TrimStart('#');
        return Windows.UI.Color.FromArgb(255,
            byte.Parse(hex[..2], System.Globalization.NumberStyles.HexNumber),
            byte.Parse(hex[2..4], System.Globalization.NumberStyles.HexNumber),
            byte.Parse(hex[4..6], System.Globalization.NumberStyles.HexNumber));
    }

    // ── Layout picker ──

    private void LayoutCard_Click(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b && b.Tag is string tag)
        {
            _selectedLayout = tag;
            UpdateLayoutSelection();
        }
    }

    private static SolidColorBrush NeonBlue => new(Windows.UI.Color.FromArgb(255, 0, 200, 255));
    private static SolidColorBrush DarkBg3 => new(Windows.UI.Color.FromArgb(255, 26, 26, 46));

    private void UpdateLayoutSelection()
    {
        var selectedBrush = Application.Current.Resources.TryGetValue("NeonBlueBrush", out var nb) && nb is Brush b
            ? b : NeonBlue;
        var unselectedBrush = Application.Current.Resources.TryGetValue("DarkBg3Brush", out var db) && db is Brush ub
            ? ub : DarkBg3;

        LayoutVertical.BorderBrush = _selectedLayout == "Vertical" ? selectedBrush : unselectedBrush;
        LayoutHorizontal.BorderBrush = _selectedLayout == "Horizontal" ? selectedBrush : unselectedBrush;
        LayoutGiftWall.BorderBrush = _selectedLayout == "GiftWall" ? selectedBrush : unselectedBrush;

        // Toggle panels based on layout selection
        var isGiftWall = _selectedLayout == "GiftWall";
        RankingSettingsPanel.Visibility = isGiftWall ? Visibility.Collapsed : Visibility.Visible;
        GiftWallPanel.Visibility = isGiftWall ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Theme card click ──

    private void ThemeCard_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Border b && b.Tag is string tag)
        {
            _selectedTheme = tag;
            UpdateThemeSelection();
        }
    }

    private void UpdateThemeSelection()
    {
        var accentBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 200, 255));
        var dimBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 255, 255));

        foreach (var item in ThemePicker.Items)
        {
            if (item is Border b && b.Tag is string tag)
            {
                var isSelected = tag == _selectedTheme;
                b.BorderBrush = isSelected ? accentBrush : dimBrush;
                b.Opacity = isSelected ? 1.0 : 0.6;
            }
        }
    }

    // ── Server Control ──

    private void StartServer_Click(object sender, RoutedEventArgs e)
    {
        var port = ParseInt(ServerPortBox.Text, 7700, 1024, 65535);
        ServerPortBox.Text = port.ToString();
        ViewModel.OverlayServer.Start(port);

        foreach (var item in _overlayItems)
        {
            item.Config.Port = port;
            ViewModel.OverlayServer.RegisterOverlay(item.Config);
            item.Url = ViewModel.OverlayServer.GetOverlayUrl(item.Config.Id);
        }

        UpdateServerStatus();
        RefreshList();
        SaveOverlays();
    }

    private void StopServer_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.OverlayServer.Stop();
        UpdateServerStatus();
    }

    private void OnServerStatusChanged()
    {
        DispatcherQueue.TryEnqueue(UpdateServerStatus);
    }

    private void UpdateServerStatus()
    {
        var running = ViewModel.OverlayServer.IsRunning;
        ServerStatusDot.Fill = new SolidColorBrush(running
            ? Windows.UI.Color.FromArgb(255, 0, 255, 136)
            : Windows.UI.Color.FromArgb(255, 255, 50, 80));
        ServerStatusText.Text = running ? $"Running (:{ViewModel.OverlayServer.Port})" : "Stopped";
        StartServerBtn.IsEnabled = !running;
        StopServerBtn.IsEnabled = running;
    }

    // ── Gift Wall Picker ──

    private void OverlayGiftSearch_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is AutoSuggestBox box)
        {
            // Show all available gifts when user clicks into the box
            box.ItemsSource = TikTokGiftLibrary.All
                .Where(g => !_selectedGifts.Any(s => s.Name == g.Name))
                .ToList();
            box.IsSuggestionListOpen = true;
        }
    }

    private void OverlayGiftSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            var query = sender.Text?.Trim() ?? string.Empty;
            List<TikTokGift> results;
            if (string.IsNullOrEmpty(query))
            {
                // Show all gifts when text is empty
                results = TikTokGiftLibrary.All
                    .Where(g => !_selectedGifts.Any(s => s.Name == g.Name))
                    .ToList();
            }
            else
            {
                // Filter/sort when user is typing
                results = TikTokGiftLibrary.Search(query)
                    .Where(g => !_selectedGifts.Any(s => s.Name == g.Name))
                    .ToList();
            }
            sender.ItemsSource = results;
        }
    }

    private void OverlayGiftSearch_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is TikTokGift gift)
        {
            AddGiftToSelection(gift);
            sender.Text = string.Empty;
        }
    }

    private void OverlayGiftSearch_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is TikTokGift gift)
        {
            AddGiftToSelection(gift);
            sender.Text = string.Empty;
        }
        else if (!string.IsNullOrWhiteSpace(args.QueryText))
        {
            var found = TikTokGiftLibrary.FindByName(args.QueryText);
            if (found != null && !_selectedGifts.Any(s => s.Name == found.Name))
            {
                AddGiftToSelection(found);
            }
            sender.Text = string.Empty;
        }
    }

    private void AddGiftToSelection(TikTokGift gift)
    {
        if (_selectedGifts.Any(g => g.Name == gift.Name))
            return;

        _selectedGifts.Add(gift);
        RefreshSelectedGiftsDisplay();
    }

    private void RemoveGiftFromSelection(TikTokGift gift)
    {
        _selectedGifts.RemoveAll(g => g.Name == gift.Name);
        RefreshSelectedGiftsDisplay();
    }

    private void RefreshSelectedGiftsDisplay()
    {
        SelectedGiftsCountText.Text = $"{_selectedGifts.Count} gift{(_selectedGifts.Count != 1 ? "s" : "")} selected";
        SelectedGiftsList.Items.Clear();

        foreach (var gift in _selectedGifts)
        {
            var chip = new Border
            {
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(50, 255, 50, 120)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(100, 255, 50, 120)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 3, 4, 3),
                Tag = gift
            };

            var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };

            var img = new Image
            {
                Width = 18,
                Height = 18,
                Source = new BitmapImage(new Uri(gift.ImageUrl))
            };
            stack.Children.Add(img);

            var nameBlock = new TextBlock
            {
                Text = gift.Name,
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 224, 224, 255)),
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(nameBlock);

            var priceBlock = new TextBlock
            {
                Text = $"{gift.CoinPrice}\U0001FA99",
                FontSize = 9,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 149, 0)),
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(priceBlock);

            var removeBtn = new Button
            {
                Content = "\u2715",
                FontSize = 9,
                Padding = new Thickness(3, 1, 3, 1),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 136, 136, 170)),
                BorderThickness = new Thickness(0),
                Tag = gift,
                VerticalAlignment = VerticalAlignment.Center
            };
            removeBtn.Click += (s, e) =>
            {
                if (s is Button b && b.Tag is TikTokGift g)
                    RemoveGiftFromSelection(g);
            };
            stack.Children.Add(removeBtn);

            chip.Child = stack;
            SelectedGiftsList.Items.Add(chip);
        }
    }

    // ── Create Overlay ──

    private void CreateOverlay_Click(object sender, RoutedEventArgs e)
    {
        var port = ParseInt(ServerPortBox.Text, 7700, 1024, 65535);
        var maxPlayers = ParseInt(MaxPlayersBox.Text, 5, 1, 20);
        var refreshMs = ParseInt(RefreshBox.Text, 2000, 500, 30000);

        MaxPlayersBox.Text = maxPlayers.ToString();
        RefreshBox.Text = refreshMs.ToString();
        ServerPortBox.Text = port.ToString();

        var isGiftWall = _selectedLayout == "GiftWall";

        var config = new OverlayConfig
        {
            Name = string.IsNullOrWhiteSpace(OverlayNameBox.Text) ? "My Overlay" : OverlayNameBox.Text.Trim(),
            Type = _selectedLayout switch
            {
                "Horizontal" => OverlayType.RankingHorizontal,
                "GiftWall" => OverlayType.GiftWall,
                _ => OverlayType.RankingVertical
            },
            Theme = Enum.TryParse<OverlayTheme>(_selectedTheme, out var t) ? t : OverlayTheme.Cyberpunk,
            ShowHP = ShowHPCheck.IsChecked == true,
            ShowDamage = ShowDmgCheck.IsChecked == true,
            ShowKills = ShowKillsCheck.IsChecked == true,
            MaxPlayers = maxPlayers,
            RefreshIntervalMs = refreshMs,
            Port = port,
            SelectedGiftNames = isGiftWall ? _selectedGifts.Select(g => g.Name).ToList() : new()
        };

        ViewModel.OverlayServer.RegisterOverlay(config);

        var url = ViewModel.OverlayServer.IsRunning
            ? ViewModel.OverlayServer.GetOverlayUrl(config.Id)
            : $"http://localhost:{config.Port}/overlay/{config.Id}";

        _overlayItems.Add(OverlayDisplayItem.From(config, url));
        SaveOverlays();
        UpdateEmptyState();
    }

    // ── Actions ──

    private void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string url)
        {
            var dp = new DataPackage();
            dp.SetText(url);
            Clipboard.SetContent(dp);
            Clipboard.Flush();

            if (btn.Content is FontIcon icon)
            {
                var original = icon.Glyph;
                icon.Glyph = "\uE73E";
                icon.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 255, 136));

                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                timer.Tick += (s, args) =>
                {
                    icon.Glyph = original;
                    icon.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 200, 255));
                    timer.Stop();
                };
                timer.Start();
            }
        }
    }

    private void PreviewOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        }
    }

    private void DeleteOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            var item = _overlayItems.FirstOrDefault(o => o.Id == id);
            if (item != null)
            {
                _overlayItems.Remove(item);
                ViewModel.OverlayServer.UnregisterOverlay(id);
                SaveOverlays();
                UpdateEmptyState();
            }
        }
    }

    // ── Persistence ──

    private void SaveOverlays()
    {
        try
        {
            var configs = _overlayItems.Select(i => i.Config).ToList();
            var json = JsonSerializer.Serialize(configs, new JsonSerializerOptions { WriteIndented = true });
            var path = GetOverlaysFilePath();
            IODirectory.CreateDirectory(IOPath.GetDirectoryName(path)!);
            IOFile.WriteAllText(path, json);
        }
        catch { }
    }

    private void LoadOverlays()
    {
        try
        {
            var path = GetOverlaysFilePath();
            if (!IOFile.Exists(path)) return;

            var json = IOFile.ReadAllText(path);
            var configs = JsonSerializer.Deserialize<List<OverlayConfig>>(json);
            if (configs == null) return;

            _overlayItems.Clear();
            foreach (var cfg in configs)
            {
                ViewModel.OverlayServer.RegisterOverlay(cfg);
                var url = ViewModel.OverlayServer.IsRunning
                    ? ViewModel.OverlayServer.GetOverlayUrl(cfg.Id)
                    : $"http://localhost:{cfg.Port}/overlay/{cfg.Id}";
                _overlayItems.Add(OverlayDisplayItem.From(cfg, url));
            }

            if (configs.Count > 0)
                ServerPortBox.Text = configs[0].Port.ToString();
        }
        catch { }
    }

    private static string GetOverlaysFilePath()
    {
        var appDir = IOPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArcanePlayConnect");
        return IOPath.Combine(appDir, "overlays.json");
    }

    private void RefreshList()
    {
        var items = _overlayItems.ToList();
        _overlayItems.Clear();
        foreach (var item in items)
            _overlayItems.Add(item);
    }

    private void UpdateEmptyState()
    {
        OverlayEmptyState.Visibility = _overlayItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        OverlayListView.Visibility = _overlayItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        OverlayCountText.Text = $"({_overlayItems.Count})";
    }

    private static int ParseInt(string text, int fallback, int min, int max)
    {
        if (int.TryParse(text.Trim(), out var v))
            return Math.Clamp(v, min, max);
        return fallback;
    }
}

/// <summary>Theme preview metadata for the picker.</summary>
public record ThemePreviewInfo(string Key, string Label, string Color1, string Color2, string Color3);

/// <summary>Display wrapper for overlay items in the list.</summary>
public class OverlayDisplayItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public OverlayConfig Config { get; set; } = new();

    public string LayoutLabel => Config.Type switch
    {
        OverlayType.RankingVertical => "Vertical",
        OverlayType.RankingHorizontal => "Horizontal",
        OverlayType.GiftWall => "Gift Wall",
        _ => "Unknown"
    };

    public string LayoutGlyph => Config.Type switch
    {
        OverlayType.RankingVertical => "\uF0E2",
        OverlayType.RankingHorizontal => "\uE8A9",
        OverlayType.GiftWall => "\uE8E1",
        _ => "\uE8A9"
    };

    public string ThemeLabel => Config.Theme switch
    {
        OverlayTheme.Cyberpunk => "\u26A1 Cyberpunk",
        OverlayTheme.NeonFire => "\U0001F525 Neon Fire",
        OverlayTheme.ArcticFrost => "\u2744 Arctic Frost",
        OverlayTheme.DragonForge => "\U0001F409 Dragon Forge",
        OverlayTheme.SakuraBloom => "\U0001F338 Sakura Bloom",
        OverlayTheme.VoidShadow => "\U0001F311 Void Shadow",
        _ => "Unknown"
    };

    public SolidColorBrush ThemeAccentBrush => new(Config.Theme switch
    {
        OverlayTheme.Cyberpunk => Windows.UI.Color.FromArgb(255, 0, 200, 255),
        OverlayTheme.NeonFire => Windows.UI.Color.FromArgb(255, 255, 107, 0),
        OverlayTheme.ArcticFrost => Windows.UI.Color.FromArgb(255, 136, 221, 255),
        OverlayTheme.DragonForge => Windows.UI.Color.FromArgb(255, 255, 68, 0),
        OverlayTheme.SakuraBloom => Windows.UI.Color.FromArgb(255, 255, 136, 180),
        OverlayTheme.VoidShadow => Windows.UI.Color.FromArgb(255, 170, 68, 255),
        _ => Windows.UI.Color.FromArgb(255, 0, 200, 255)
    });

    public string StatsLabel
    {
        get
        {
            if (Config.Type == OverlayType.GiftWall)
                return $"{Config.SelectedGiftNames.Count} gifts";

            var parts = new List<string>();
            if (Config.ShowHP) parts.Add("HP");
            if (Config.ShowDamage) parts.Add("DMG");
            if (Config.ShowKills) parts.Add("KILLS");
            return parts.Count > 0 ? string.Join(" \u00B7 ", parts) : "No stats";
        }
    }

    public static OverlayDisplayItem From(OverlayConfig config, string url) => new()
    {
        Id = config.Id,
        Name = config.Name,
        Url = url,
        Config = config
    };
}
