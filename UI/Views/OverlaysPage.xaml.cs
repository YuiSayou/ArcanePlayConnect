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
using SvgImageSource = Microsoft.UI.Xaml.Media.Imaging.SvgImageSource;

namespace ArcanePlayConnect.UI.Views;

public sealed partial class OverlaysPage : Page
{
    public MainViewModel ViewModel { get; } = MainViewModel.Instance;

    private readonly ObservableCollection<OverlayDisplayItem> _overlayItems = new();
    private readonly List<TikTokGift> _selectedGifts = new();

    private string _selectedLayout = "Vertical";
    private string _selectedTheme = "Cyberpunk";
    private string _selectedStyle = "Default";

    /// <summary>When non-null, the form is in edit mode for the overlay with this ID.</summary>
    private string? _editingOverlayId = null;

    // Theme definitions for the preview picker
    private static readonly ThemePreviewInfo[] _themes =
    [
        new("Cyberpunk",    "\u26A1 Cyberpunk",     "#00C8FF", "#B400FF", "#FF3278"),
        new("NeonFire",     "\U0001F525 Neon Fire",     "#FF6B00", "#FF0044", "#FFCC00"),
        new("ArcticFrost",  "\u2744 Arctic Frost",  "#88DDFF", "#44AAFF", "#FFFFFF"),
        new("DragonForge",  "\U0001F409 Dragon Forge",  "#FF4400", "#884400", "#FFAA00"),
        new("SakuraBloom",  "\U0001F338 Sakura Bloom",  "#FF88B4", "#CC44AA", "#FFCCDD"),
        new("VoidShadow",   "\U0001F311 Void Shadow",   "#AA44FF", "#4400AA", "#DD88FF"),
        new("MidnightGold", "\U0001F31F Midnight Gold", "#FFD700", "#C8A200", "#FFF4B0"),
        new("ToxicWaste",   "\u2622 Toxic Waste",   "#39FF14", "#00CC00", "#CCFF00"),
        new("OceanDepth",   "\U0001F30A Ocean Depth",   "#00BFFF", "#0066CC", "#66D9FF"),
        new("RetroWave",    "\U0001F680 Retro Wave",    "#FF6EC7", "#7B2DFF", "#00F0FF"),
    ];

    public OverlaysPage()
    {
        InitializeComponent();
        OverlayListView.ItemsSource = _overlayItems;
        ViewModel.OverlayServer.StatusChanged += OnServerStatusChanged;
        ViewModel.OverlayPushService.StatusChanged += OnCloudRelayStatusChanged;

        BuildThemePicker();
        LoadOverlays();
        UpdateServerStatus();
        UpdateCloudRelayStatus();
        UpdateEmptyState();
        UpdateLayoutSelection();
        UpdateThemeSelection();
        UpdateStyleSelection();

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
        LayoutGiftWallVertical.BorderBrush = _selectedLayout == "GiftWallVertical" ? selectedBrush : unselectedBrush;
        LayoutLikesVertical.BorderBrush = _selectedLayout == "LikesVertical" ? selectedBrush : unselectedBrush;
        LayoutLikesHorizontal.BorderBrush = _selectedLayout == "LikesHorizontal" ? selectedBrush : unselectedBrush;
        LayoutGiftRankVertical.BorderBrush = _selectedLayout == "GiftRankVertical" ? selectedBrush : unselectedBrush;
        LayoutGiftRankHorizontal.BorderBrush = _selectedLayout == "GiftRankHorizontal" ? selectedBrush : unselectedBrush;

        // Toggle panels based on layout selection
        var isGiftWall = _selectedLayout == "GiftWall" || _selectedLayout == "GiftWallVertical";
        var isLiveRanking = _selectedLayout is "LikesVertical" or "LikesHorizontal" or "GiftRankVertical" or "GiftRankHorizontal";
        RankingSettingsPanel.Visibility = (isGiftWall || isLiveRanking) ? Visibility.Collapsed : Visibility.Visible;
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

    // ── Style picker ──

    private void StyleCard_Click(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b && b.Tag is string tag)
        {
            _selectedStyle = tag;
            UpdateStyleSelection();
        }
    }

    private void UpdateStyleSelection()
    {
        var selectedBrush = Application.Current.Resources.TryGetValue("NeonBlueBrush", out var nb) && nb is Brush sb
            ? sb : NeonBlue;
        var unselectedBrush = Application.Current.Resources.TryGetValue("DarkBg3Brush", out var db) && db is Brush ub
            ? ub : DarkBg3;

        if (StyleDefault != null) StyleDefault.BorderBrush = _selectedStyle == "Default" ? selectedBrush : unselectedBrush;
        if (StyleCompact != null) StyleCompact.BorderBrush = _selectedStyle == "Compact" ? selectedBrush : unselectedBrush;
        if (StyleMinimal != null) StyleMinimal.BorderBrush = _selectedStyle == "Minimal" ? selectedBrush : unselectedBrush;
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
                Source = GetGiftImageSource(gift)
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
                Text = gift.IsFreeInteraction ? "FREE" : $"{gift.CoinPrice}\U0001FA99",
                FontSize = 9,
                Foreground = new SolidColorBrush(gift.IsFreeInteraction
                    ? Windows.UI.Color.FromArgb(255, 180, 0, 255)
                    : Windows.UI.Color.FromArgb(255, 255, 149, 0)),
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

        // Build gift text label inputs
        RefreshGiftTextInputs();
    }

    private readonly Dictionary<string, string> _giftTextLabels = new();

    private void RefreshGiftTextInputs()
    {
        GiftTextLabelsPanel.Visibility = _selectedGifts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        GiftTextInputsList.Children.Clear();

        foreach (var gift in _selectedGifts)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32, GridUnitType.Pixel) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var img = new Image
            {
                Width = 28,
                Height = 28,
                Source = GetGiftImageSource(gift),
                VerticalAlignment = VerticalAlignment.Center
            };
            var imgBorder = new Border
            {
                Width = 32, Height = 32, CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 26, 26, 46)),
                Child = img, VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(imgBorder, 0);

            var nameBlock = new TextBlock
            {
                Text = gift.Name,
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 224, 224, 255)),
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 80
            };
            Grid.SetColumn(nameBlock, 1);

            _giftTextLabels.TryGetValue(gift.Name, out var existingText);
            var textBox = new TextBox
            {
                Text = existingText ?? string.Empty,
                PlaceholderText = "e.g. Summon Zombie",
                FontSize = 11,
                Padding = new Thickness(8, 6, 8, 6),
                Tag = gift.Name,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (Application.Current.Resources.TryGetValue("CyberpunkTextBoxStyle", out var style) && style is Style s)
                textBox.Style = s;

            textBox.TextChanged += (sender, args) =>
            {
                if (sender is TextBox tb && tb.Tag is string gName)
                    _giftTextLabels[gName] = tb.Text;
            };
            Grid.SetColumn(textBox, 2);

            row.Children.Add(imgBorder);
            row.Children.Add(nameBlock);
            row.Children.Add(textBox);
            GiftTextInputsList.Children.Add(row);
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

        var isGiftWall = _selectedLayout == "GiftWall" || _selectedLayout == "GiftWallVertical";

        if (_editingOverlayId != null)
        {
            // ── Update existing overlay ──
            var existingItem = _overlayItems.FirstOrDefault(o => o.Id == _editingOverlayId);
            if (existingItem != null)
            {
                var cfg = existingItem.Config;
                cfg.Name = string.IsNullOrWhiteSpace(OverlayNameBox.Text) ? "My Overlay" : OverlayNameBox.Text.Trim();
                cfg.Type = _selectedLayout switch
                {
                    "Horizontal" => OverlayType.RankingHorizontal,
                    "GiftWall" => OverlayType.GiftWall,
                    "GiftWallVertical" => OverlayType.GiftWallVertical,
                    "LikesVertical" => OverlayType.LikesRankingVertical,
                    "LikesHorizontal" => OverlayType.LikesRankingHorizontal,
                    "GiftRankVertical" => OverlayType.GiftRankingVertical,
                    "GiftRankHorizontal" => OverlayType.GiftRankingHorizontal,
                    _ => OverlayType.RankingVertical
                };
                cfg.Theme = Enum.TryParse<OverlayTheme>(_selectedTheme, out var t) ? t : OverlayTheme.Cyberpunk;
                cfg.Style = Enum.TryParse<OverlayStyle>(_selectedStyle, out var st) ? st : OverlayStyle.Default;
                cfg.ShowHP = ShowHPCheck.IsChecked == true;
                cfg.ShowDamage = ShowDmgCheck.IsChecked == true;
                cfg.ShowKills = ShowKillsCheck.IsChecked == true;
                cfg.MaxPlayers = maxPlayers;
                cfg.RefreshIntervalMs = refreshMs;
                cfg.Port = port;
                cfg.SelectedGiftNames = isGiftWall ? _selectedGifts.Select(g => g.Name).ToList() : new();
                cfg.GiftTextLabels = isGiftWall ? new Dictionary<string, string>(_giftTextLabels.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))) : new();
                cfg.CloudflareBaseUrl = OverlayConfig.DefaultCloudflareUrl;

                ViewModel.OverlayServer.RegisterOverlay(cfg);

                existingItem.Name = cfg.Name;
                existingItem.Url = ViewModel.OverlayServer.IsRunning
                    ? ViewModel.OverlayServer.GetOverlayUrl(cfg.Id)
                    : $"http://localhost:{cfg.Port}/overlay/{cfg.Id}";
                existingItem.CloudUrl = OverlayServerService.GetCloudOverlayUrl(cfg);

                // Refresh the list to reflect changes
                RefreshList();
                SaveOverlays();
            }

            // Exit edit mode
            _editingOverlayId = null;
            SetEditMode(false);
        }
        else
        {
            // ── Create new overlay ──
            var config = new OverlayConfig
            {
                Name = string.IsNullOrWhiteSpace(OverlayNameBox.Text) ? "My Overlay" : OverlayNameBox.Text.Trim(),
                Type = _selectedLayout switch
                {
                    "Horizontal" => OverlayType.RankingHorizontal,
                    "GiftWall" => OverlayType.GiftWall,
                    "GiftWallVertical" => OverlayType.GiftWallVertical,
                    "LikesVertical" => OverlayType.LikesRankingVertical,
                    "LikesHorizontal" => OverlayType.LikesRankingHorizontal,
                    "GiftRankVertical" => OverlayType.GiftRankingVertical,
                    "GiftRankHorizontal" => OverlayType.GiftRankingHorizontal,
                    _ => OverlayType.RankingVertical
                },
                Theme = Enum.TryParse<OverlayTheme>(_selectedTheme, out var t) ? t : OverlayTheme.Cyberpunk,
                Style = Enum.TryParse<OverlayStyle>(_selectedStyle, out var st) ? st : OverlayStyle.Default,
                ShowHP = ShowHPCheck.IsChecked == true,
                ShowDamage = ShowDmgCheck.IsChecked == true,
                ShowKills = ShowKillsCheck.IsChecked == true,
                MaxPlayers = maxPlayers,
                RefreshIntervalMs = refreshMs,
                Port = port,
                SelectedGiftNames = isGiftWall ? _selectedGifts.Select(g => g.Name).ToList() : new(),
                GiftTextLabels = isGiftWall ? new Dictionary<string, string>(_giftTextLabels.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))) : new(),
                CloudflareBaseUrl = OverlayConfig.DefaultCloudflareUrl
            };

            ViewModel.OverlayServer.RegisterOverlay(config);

            var url = ViewModel.OverlayServer.IsRunning
                ? ViewModel.OverlayServer.GetOverlayUrl(config.Id)
                : $"http://localhost:{config.Port}/overlay/{config.Id}";

            _overlayItems.Add(OverlayDisplayItem.From(config, url));
            SaveOverlays();
            UpdateEmptyState();
        }
    }

    // ── Actions ──

    private void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn)
            return;

        var url = btn.Tag as string;
        if (string.IsNullOrEmpty(url))
            return;

        try
        {
            var dp = new DataPackage();
            dp.SetText(url);
            Clipboard.SetContent(dp);
        }
        catch
        {
            // Clipboard may be locked by another process – silently ignore
            return;
        }

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

    // ── Edit Overlay ──

    private void EditOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            var item = _overlayItems.FirstOrDefault(o => o.Id == id);
            if (item == null) return;

            var cfg = item.Config;

            // Populate the form with the overlay's config
            _editingOverlayId = cfg.Id;
            OverlayNameBox.Text = cfg.Name;

            // Layout
            _selectedLayout = cfg.Type switch
            {
                OverlayType.RankingHorizontal => "Horizontal",
                OverlayType.GiftWall => "GiftWall",
                OverlayType.GiftWallVertical => "GiftWallVertical",
                OverlayType.LikesRankingVertical => "LikesVertical",
                OverlayType.LikesRankingHorizontal => "LikesHorizontal",
                OverlayType.GiftRankingVertical => "GiftRankVertical",
                OverlayType.GiftRankingHorizontal => "GiftRankHorizontal",
                _ => "Vertical"
            };
            UpdateLayoutSelection();

            // Theme
            _selectedTheme = cfg.Theme.ToString();
            UpdateThemeSelection();

            // Style
            _selectedStyle = cfg.Style.ToString();
            UpdateStyleSelection();

            // Stats
            ShowHPCheck.IsChecked = cfg.ShowHP;
            ShowDmgCheck.IsChecked = cfg.ShowDamage;
            ShowKillsCheck.IsChecked = cfg.ShowKills;

            // Max players & refresh
            MaxPlayersBox.Text = cfg.MaxPlayers.ToString();
            RefreshBox.Text = cfg.RefreshIntervalMs.ToString();

            // Gift wall selections
            _selectedGifts.Clear();
            _giftTextLabels.Clear();
            foreach (var giftName in cfg.SelectedGiftNames)
            {
                var gift = TikTokGiftLibrary.FindByName(giftName);
                if (gift != null) _selectedGifts.Add(gift);
            }
            foreach (var kv in cfg.GiftTextLabels)
                _giftTextLabels[kv.Key] = kv.Value;

            RefreshSelectedGiftsDisplay();

            // Update UI to edit mode
            SetEditMode(true);
        }
    }

    private void CancelEdit_Click(object sender, RoutedEventArgs e)
    {
        _editingOverlayId = null;

        // Reset the form to defaults
        OverlayNameBox.Text = "My Overlay";
        _selectedLayout = "Vertical";
        _selectedTheme = "Cyberpunk";
        _selectedStyle = "Default";
        UpdateLayoutSelection();
        UpdateThemeSelection();
        UpdateStyleSelection();
        ShowHPCheck.IsChecked = true;
        ShowDmgCheck.IsChecked = true;
        ShowKillsCheck.IsChecked = true;
        MaxPlayersBox.Text = "5";
        RefreshBox.Text = "2000";
        _selectedGifts.Clear();
        _giftTextLabels.Clear();
        RefreshSelectedGiftsDisplay();

        SetEditMode(false);
    }

    private void SetEditMode(bool editing)
    {
        CreateEditIcon.Glyph = editing ? "\uE70F" : "\uE710";
        CreateEditTitle.Text = editing ? "EDIT OVERLAY" : "CREATE OVERLAY";
        CancelEditBtn.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        CreateSaveBtnIcon.Glyph = editing ? "\uE74E" : "\uE710";
        CreateSaveBtnText.Text = editing ? "Save Changes" : "Create Overlay";
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

    // ── Cloud Relay ──

    private void StartCloudRelay_Click(object sender, RoutedEventArgs e)
    {
        // Start pushing data for all overlays that have cloud push enabled
        foreach (var item in _overlayItems)
        {
            if (item.Config.CloudPushEnabled)
            {
                ViewModel.OverlayPushService.StartPushing(item.Config);
            }
        }
        UpdateCloudRelayStatus();
    }

    private void StopCloudRelay_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.OverlayPushService.StopAll();
        UpdateCloudRelayStatus();
    }

    private void OnCloudRelayStatusChanged()
    {
        DispatcherQueue.TryEnqueue(UpdateCloudRelayStatus);
    }

    private void UpdateCloudRelayStatus()
    {
        var running = ViewModel.OverlayPushService.IsRunning;
        CloudRelayStatusDot.Fill = new SolidColorBrush(running
            ? Windows.UI.Color.FromArgb(255, 180, 0, 255)
            : Windows.UI.Color.FromArgb(255, 255, 50, 80));
        CloudRelayStatusText.Text = running ? "Pushing" : "Stopped";
        StartCloudRelayBtn.IsEnabled = !running;
        StopCloudRelayBtn.IsEnabled = running;
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

    /// <summary>
    /// Returns an appropriate ImageSource for a gift. Uses local cached SVG for built-in icons
    /// (Like/Follow) since BitmapImage doesn't support data: URIs, otherwise uses the remote URL.
    /// </summary>
    private static Microsoft.UI.Xaml.Media.ImageSource GetGiftImageSource(TikTokGift gift)
    {
        // For built-in icons, load from the locally cached SVG file
        var cachedPath = GiftImageService.GetCachedImagePath(gift);
        if (cachedPath != null && cachedPath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            return new SvgImageSource(new Uri(cachedPath));
        }

        // For regular gifts, use the remote image URL
        return new BitmapImage(new Uri(gift.ImageUrl));
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
    public string CloudUrl { get; set; } = string.Empty;
    public OverlayConfig Config { get; set; } = new();

    public Visibility HasCloudUrl => string.IsNullOrEmpty(CloudUrl) ? Visibility.Collapsed : Visibility.Visible;

    public string LayoutLabel => Config.Type switch
    {
        OverlayType.RankingVertical => "Vertical",
        OverlayType.RankingHorizontal => "Horizontal",
        OverlayType.GiftWall => "Gift Grid",
        OverlayType.GiftWallVertical => "Gift List",
        OverlayType.LikesRankingVertical => "Likes Vertical",
        OverlayType.LikesRankingHorizontal => "Likes Horizontal",
        OverlayType.GiftRankingVertical => "Gift Rank Vertical",
        OverlayType.GiftRankingHorizontal => "Gift Rank Horizontal",
        _ => "Unknown"
    };

    public string LayoutGlyph => Config.Type switch
    {
        OverlayType.RankingVertical => "\uF0E2",
        OverlayType.RankingHorizontal => "\uE8A9",
        OverlayType.GiftWall => "\uE8E1",
        OverlayType.GiftWallVertical => "\uE8E1",
        OverlayType.LikesRankingVertical => "\uEB51",
        OverlayType.LikesRankingHorizontal => "\uEB51",
        OverlayType.GiftRankingVertical => "\uE8E1",
        OverlayType.GiftRankingHorizontal => "\uE8E1",
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
        OverlayTheme.MidnightGold => "\U0001F31F Midnight Gold",
        OverlayTheme.ToxicWaste => "\u2622 Toxic Waste",
        OverlayTheme.OceanDepth => "\U0001F30A Ocean Depth",
        OverlayTheme.RetroWave => "\U0001F680 Retro Wave",
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
        OverlayTheme.MidnightGold => Windows.UI.Color.FromArgb(255, 255, 215, 0),
        OverlayTheme.ToxicWaste => Windows.UI.Color.FromArgb(255, 57, 255, 20),
        OverlayTheme.OceanDepth => Windows.UI.Color.FromArgb(255, 0, 191, 255),
        OverlayTheme.RetroWave => Windows.UI.Color.FromArgb(255, 255, 110, 199),
        _ => Windows.UI.Color.FromArgb(255, 0, 200, 255)
    });

    public string StatsLabel
    {
        get
        {
            if (Config.Type == OverlayType.GiftWall || Config.Type == OverlayType.GiftWallVertical)
                return $"{Config.SelectedGiftNames.Count} gifts";

            if (Config.Type is OverlayType.LikesRankingVertical or OverlayType.LikesRankingHorizontal)
                return "Live Likes";

            if (Config.Type is OverlayType.GiftRankingVertical or OverlayType.GiftRankingHorizontal)
                return "Live Gifts";

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
        CloudUrl = OverlayServerService.GetCloudOverlayUrl(config),
        Config = config
    };
}
