using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using ArcanePlayConnect.Core;
using ArcanePlayConnect.Core.Models;
using ArcanePlayConnect.Services;
using ArcanePlayConnect.UI.ViewModels;

namespace ArcanePlayConnect.UI.Views;

public sealed partial class AddActionPage : Page
{
    private ActionTriggerType _selectedType = ActionTriggerType.Gift;
    private bool _useButtonAction;
    private readonly List<TikTokGift> _selectedGifts = new();

    public ActionMappingItem? Result { get; private set; }

    public event Action? Confirmed;
    public event Action? Cancelled;
    public event Action<SavedCommand>? SaveCommandToLibrary;
    public event Action<SavedCommand>? DeleteCommandFromLibrary;

    public AddActionPage()
    {
        InitializeComponent();
        ApplyTypeSelection(ActionTriggerType.Gift);
        ApplyActionMode(false);

        // Show the gift picker only after layout is complete to prevent auto-focus
        Loaded += (_, _) =>
        {
            if (_selectedType == ActionTriggerType.Gift)
                GiftPickerPanel.Visibility = Visibility.Visible;
        };

        // Preload gift images in background
        _ = GiftImageService.PreloadAllAsync();
    }

    public void LoadSavedCommands(IEnumerable<SavedCommand> commands)
    {
        var source = new ObservableCollection<SavedCommand>(commands);
        SavedCommandsList.ItemsSource = source;
    }

    public void LoadCommandButtons(IEnumerable<CommandButton> buttons)
    {
        var list = buttons.ToList();
        ButtonCombo.ItemsSource = list;
        ButtonCombo.DisplayMemberPath = "Name";
    }

    // ── Action mode selector ────────────────────────────────────────────────

    private void ActionMode_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border && border.Tag is string tag)
            ApplyActionMode(tag == "Button");
    }

    private void ApplyActionMode(bool useButton)
    {
        _useButtonAction = useButton;

        if (useButton)
        {
            SetModeActive(ActionButtonBtn, ActionButtonLabel, "#FFFF9500");
            SetModeInactive(ActionCommandBtn, ActionCommandLabel);
            CommandPanel.Visibility = Visibility.Collapsed;
            ButtonPanel.Visibility = Visibility.Visible;
        }
        else
        {
            SetModeActive(ActionCommandBtn, ActionCommandLabel, "#FF00FF88");
            SetModeInactive(ActionButtonBtn, ActionButtonLabel);
            CommandPanel.Visibility = Visibility.Visible;
            ButtonPanel.Visibility = Visibility.Collapsed;
        }
    }

    private static void SetModeActive(Border border, TextBlock label, string colorHex)
    {
        var color = ParseColor(colorHex);
        var brush = new SolidColorBrush(color);
        border.BorderBrush = brush;
        border.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, color.R, color.G, color.B));
        label.Foreground = brush;
        if (border.Child is StackPanel sp && sp.Children.Count > 0 && sp.Children[0] is FontIcon icon)
            icon.Foreground = brush;
    }

    private static void SetModeInactive(Border border, TextBlock label)
    {
        var inactive = new SolidColorBrush(ParseColor("#FF8888AA"));
        border.BorderBrush = new SolidColorBrush(ParseColor("#FF1A1A2E"));
        border.Background = new SolidColorBrush(ParseColor("#FF16162A"));
        label.Foreground = inactive;
        if (border.Child is StackPanel sp && sp.Children.Count > 0 && sp.Children[0] is FontIcon icon)
            icon.Foreground = inactive;
    }

    // ── Live command hint ───────────────────────────────────────────────────

    private void CommandBox_TextChanged(object sender, string text)
    {
        UpdateCommandHint(text);
    }

    private void UpdateCommandHint(string text)
    {
        var hasLegacyPlaceholder = text.Contains("{safe}", StringComparison.OrdinalIgnoreCase);
        var hasDoubleBraces      = text.Contains("{{") || text.Contains("}}");
        var hasJsonComponent     = System.Text.RegularExpressions.Regex.IsMatch(
                                       text, @"CustomName\s*:\s*'.*?""text"".*?'",
                                       System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var hasNickname          = text.Contains("{nickname}", StringComparison.OrdinalIgnoreCase);
        var hasUsername          = text.Contains("{username}", StringComparison.OrdinalIgnoreCase);

        if (hasLegacyPlaceholder)
        {
            SetHint("Warning: Found {safe} \u2014 will be auto-fixed to {nickname} when sent.",
                "#FFFFA500", "#FF1F1200");
        }
        else if (hasJsonComponent)
        {
            SetHint("Warning: JSON component CustomName:'{}'  shows raw text on PaperMC 1.20.5+.\n" +
                "  Use plain string instead:  CustomName:\"{nickname}\"",
                "#FFFFA500", "#FF1F1200");
        }
        else if (hasDoubleBraces)
        {
            SetHint("Warning: Found {{ or }} \u2014 will be collapsed to { } when sent.\n" +
                "  Type braces normally: {CustomName:\"{nickname}\",CustomNameVisible:1b}",
                "#FFFFA500", "#FF1F1200");
        }
        else if (hasNickname || hasUsername)
        {
            var preview = EventProcessor.BuildCommand(text, "TestPlayer", "testplayer");
            SetHint($"Preview: {preview}", "#FF00FF88", "#FF0D1F0D");
        }
        else
        {
            SetHint(
                "{nickname} = display name (for CustomName)\n" +
                "{username} = TikTok username (for execute as)\n" +
                "Example: execute as {username} at @s run summon zombie ^ ^ ^2 {CustomName:\"{nickname}\",CustomNameVisible:1b}",
                "#FF8888AA", "#FF0D0D1A");
        }
    }

    private void SetHint(string message, string fgHex, string bgHex)
    {
        CommandHintText.Text = message;
        CommandHintText.Foreground = new SolidColorBrush(ParseColor(fgHex));
        CommandHintBorder.Background = new SolidColorBrush(ParseColor(bgHex));
        CommandHintBorder.BorderBrush = new SolidColorBrush(ParseColor(fgHex));
    }

    // ── Type selector ───────────────────────────────────────────────────────

    private void TriggerType_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border btn && btn.Tag is string tag &&
            Enum.TryParse<ActionTriggerType>(tag, out var type))
        {
            ApplyTypeSelection(type);
        }
    }

    private void ApplyTypeSelection(ActionTriggerType type)
    {
        _selectedType = type;

        SetInactive(GiftTypeBtn,   GiftLabel);
        SetInactive(FollowTypeBtn, FollowLabel);
        SetInactive(ChatTypeBtn,   ChatLabel);
        SetInactive(LikeTypeBtn,   LikeLabel);
        SetInactive(JoinTypeBtn,   JoinLabel);
        SetInactive(ShareTypeBtn,  ShareLabel);
        SetInactive(SubscribeTypeBtn, SubscribeLabel);

        switch (type)
        {
            case ActionTriggerType.Gift:
                SetActive(GiftTypeBtn,   GiftLabel,   "#FFFF3278");
                break;
            case ActionTriggerType.Follow:
                SetActive(FollowTypeBtn, FollowLabel, "#FFB400FF");
                break;
            case ActionTriggerType.Chat:
                SetActive(ChatTypeBtn,   ChatLabel,   "#FF00C8FF");
                break;
            case ActionTriggerType.Like:
                SetActive(LikeTypeBtn,   LikeLabel,   "#FFFF5050");
                break;
            case ActionTriggerType.Join:
                SetActive(JoinTypeBtn,   JoinLabel,   "#FF00E6B4");
                break;
            case ActionTriggerType.Share:
                SetActive(ShareTypeBtn,  ShareLabel,  "#FFFF9500");
                break;
            case ActionTriggerType.Subscribe:
                SetActive(SubscribeTypeBtn, SubscribeLabel, "#FFFFD700");
                break;
        }

        if (type == ActionTriggerType.Follow || type == ActionTriggerType.Join ||
            type == ActionTriggerType.Share || type == ActionTriggerType.Subscribe)
        {
            TriggerKeyPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            TriggerKeyPanel.Visibility = Visibility.Visible;

            if (type == ActionTriggerType.Gift)
            {
                TriggerKeyLabel.Text = "GIFT NAME";
                GiftPickerPanel.Visibility = Visibility.Visible;
                TriggerKeyBox.Visibility = Visibility.Collapsed;
            }
            else
            {
                GiftPickerPanel.Visibility = Visibility.Collapsed;
                TriggerKeyBox.Visibility = Visibility.Visible;

                TriggerKeyLabel.Text = type switch
                {
                    ActionTriggerType.Like => "MIN LIKES (leave empty for any)",
                    _                      => "KEYWORD (leave empty to match all chat)"
                };
                TriggerKeyBox.PlaceholderText = type switch
                {
                    ActionTriggerType.Like => "e.g. 10 (trigger when \u226510 likes sent)",
                    _                      => "e.g. !spawn (optional)"
                };
            }
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static void SetActive(Border border, TextBlock label, string colorHex)
    {
        var color = ParseColor(colorHex);
        var brush = new SolidColorBrush(color);
        border.BorderBrush = brush;
        border.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, color.R, color.G, color.B));
        label.Foreground = brush;
        if (border.Child is StackPanel sp && sp.Children.Count > 0 && sp.Children[0] is FontIcon icon)
            icon.Foreground = brush;
    }

    private static void SetInactive(Border border, TextBlock label)
    {
        var inactiveBrush = new SolidColorBrush(ParseColor("#FF8888AA"));
        border.BorderBrush = new SolidColorBrush(ParseColor("#FF1A1A2E"));
        border.Background = new SolidColorBrush(ParseColor("#FF16162A"));
        label.Foreground = inactiveBrush;
        if (border.Child is StackPanel sp && sp.Children.Count > 0 && sp.Children[0] is FontIcon icon)
            icon.Foreground = inactiveBrush;
    }

    private static Windows.UI.Color ParseColor(string hex)
    {
        hex = hex.TrimStart('#');
        return Windows.UI.Color.FromArgb(
            Convert.ToByte(hex[0..2], 16),
            Convert.ToByte(hex[2..4], 16),
            Convert.ToByte(hex[4..6], 16),
            Convert.ToByte(hex[6..8], 16));
    }

    // ── Library ─────────────────────────────────────────────────────────────

    private void SavedCommand_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SavedCommand cmd)
        {
            CommandBox.Text = cmd.Command;
        }
    }

    private void SaveCommand_Click(object sender, RoutedEventArgs e)
    {
        var command = CommandBox.Text?.Trim() ?? string.Empty;
        var label   = SaveLabelBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(command))
        {
            CommandBox.BorderBrush = new SolidColorBrush(ParseColor("#FFFF3278"));
            return;
        }
        if (string.IsNullOrWhiteSpace(label))
        {
            SaveLabelBox.BorderBrush = new SolidColorBrush(ParseColor("#FFFF3278"));
            return;
        }

        var saved = new SavedCommand { Label = label, Command = command };
        SaveCommandToLibrary?.Invoke(saved);

        if (SavedCommandsList.ItemsSource is ObservableCollection<SavedCommand> list)
        {
            SavedCommand? existing = null;
            foreach (var sc in list)
            {
                if (string.Equals(sc.Label, label, StringComparison.OrdinalIgnoreCase))
                { existing = sc; break; }
            }
            if (existing != null) existing.Command = command;
            else list.Add(saved);
        }

        SaveLabelBox.Text = string.Empty;
        SaveLabelBox.BorderBrush = new SolidColorBrush(ParseColor("#FF1A1A2E"));
    }

    private void DeleteSavedCommand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SavedCommand cmd)
        {
            DeleteCommandFromLibrary?.Invoke(cmd);
            if (SavedCommandsList.ItemsSource is ObservableCollection<SavedCommand> list)
                list.Remove(cmd);
        }
    }

    // ── Action buttons ──────────────────────────────────────────────────────

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        // Get trigger key from the correct input control
        string triggerKey;
        if (_selectedType == ActionTriggerType.Gift)
        {
            if (_selectedGifts.Count == 0)
            {
                GiftSuggestBox.BorderBrush = new SolidColorBrush(ParseColor("#FFFF3278"));
                return;
            }
            triggerKey = string.Join("|", _selectedGifts.Select(g => g.Name));
        }
        else
        {
            triggerKey = TriggerKeyBox.Text?.Trim() ?? string.Empty;
        }

        if (_useButtonAction)
        {
            if (ButtonCombo.SelectedItem is not CommandButton selectedButton)
            {
                return;
            }

            Result = new ActionMappingItem
            {
                TriggerType    = _selectedType,
                TriggerKey     = triggerKey,
                Command        = string.Empty,
                TargetButtonId = selectedButton.Id
            };
        }
        else
        {
            var command = CommandBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(command))
            {
                CommandBox.BorderBrush = new SolidColorBrush(ParseColor("#FFFF3278"));
                return;
            }

            Result = new ActionMappingItem
            {
                TriggerType    = _selectedType,
                TriggerKey     = triggerKey,
                Command        = command,
                TargetButtonId = string.Empty
            };
        }

        Confirmed?.Invoke();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Result = null;
        Cancelled?.Invoke();
    }

    // ── Gift Picker ─────────────────────────────────────────────────────────

    private void GiftSuggestBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is AutoSuggestBox box)
        {
            box.ItemsSource = TikTokGiftLibrary.All
                .Where(g => !_selectedGifts.Any(s => s.Name == g.Name))
                .ToList();
            box.IsSuggestionListOpen = true;
        }
    }

    private void GiftSuggestBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            var query = sender.Text?.Trim() ?? string.Empty;
            List<TikTokGift> results;
            if (string.IsNullOrEmpty(query))
            {
                results = TikTokGiftLibrary.All
                    .Where(g => !_selectedGifts.Any(s => s.Name == g.Name))
                    .ToList();
            }
            else
            {
                results = TikTokGiftLibrary.Search(query)
                    .Where(g => !_selectedGifts.Any(s => s.Name == g.Name))
                    .ToList();
            }
            sender.ItemsSource = results;
        }
    }

    private void GiftSuggestBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is TikTokGift gift)
        {
            AddGiftToSelection(gift);
            sender.Text = string.Empty;
        }
    }

    private void GiftSuggestBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
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
        SelectedGiftsBorder.Visibility = _selectedGifts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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

        // Reset border color after valid selection
        GiftSuggestBox.BorderBrush = new SolidColorBrush(ParseColor("#FF1A1A2E"));
    }
}
