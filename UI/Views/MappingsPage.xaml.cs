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

public sealed partial class MappingsPage : Page
{
    public MainViewModel ViewModel { get; } = MainViewModel.Instance;

    private ActionTriggerType _selectedType = ActionTriggerType.Gift;
    private bool _useButtonAction;
    private readonly List<TikTokGift> _selectedGifts = new();
    private bool _formOpen;
    private bool _libraryOpen;

    public MappingsPage()
    {
        InitializeComponent();
        ApplyTypeSelection(ActionTriggerType.Gift);
        ApplyActionMode(false);
        LoadFormData();
        UpdateEmptyState();

        ViewModel.CurrentMappings.CollectionChanged += (_, _) => UpdateEmptyState();

        _ = GiftImageService.PreloadAllAsync();
    }

    private void LoadFormData()
    {
        SavedCommandsList.ItemsSource = new ObservableCollection<SavedCommand>(ViewModel.SavedCommands);
        ButtonCombo.ItemsSource = ViewModel.CommandButtons.ToList();
        ButtonCombo.DisplayMemberPath = "Name";
    }

    private void UpdateEmptyState()
    {
        var count = ViewModel.CurrentMappings.Count;
        MappingCountText.Text = $"{count}";
        EmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MappingsList.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ?? Form toggle ?????????????????????????????????????????????????????????

    private void ToggleForm_Click(object sender, RoutedEventArgs e)
    {
        _formOpen = !_formOpen;
        AddFormPanel.Visibility = _formOpen ? Visibility.Visible : Visibility.Collapsed;

        if (_formOpen)
        {
            if (_selectedType == ActionTriggerType.Gift)
                GiftPickerPanel.Visibility = Visibility.Visible;
            if (_selectedType == ActionTriggerType.Follow)
                FollowOptionsPanel.Visibility = Visibility.Visible;

            LoadFormData();
        }
        else
        {
            GiftPickerPanel.Visibility = Visibility.Collapsed;
            FollowOptionsPanel.Visibility = Visibility.Collapsed;
        }
    }

    // ?? Library toggle ??????????????????????????????????????????????????????

    private void ToggleLibrary_Click(object sender, RoutedEventArgs e)
    {
        _libraryOpen = !_libraryOpen;
        LibraryListBorder.Visibility = _libraryOpen ? Visibility.Visible : Visibility.Collapsed;
        LibraryToggleIcon.Glyph = _libraryOpen ? "\uE70E" : "\uE70D";
    }

    // ?? Trigger type ????????????????????????????????????????????????????????

    private void TriggerType_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border btn && btn.Tag is string tag &&
            Enum.TryParse<ActionTriggerType>(tag, out var type))
            ApplyTypeSelection(type);
    }

    private void ApplyTypeSelection(ActionTriggerType type)
    {
        _selectedType = type;

        SetInactive(GiftTypeBtn, GiftLabel);
        SetInactive(FollowTypeBtn, FollowLabel);
        SetInactive(ChatTypeBtn, ChatLabel);
        SetInactive(LikeTypeBtn, LikeLabel);
        SetInactive(JoinTypeBtn, JoinLabel);

        switch (type)
        {
            case ActionTriggerType.Gift:   SetActive(GiftTypeBtn, GiftLabel, "#FFFF3278"); break;
            case ActionTriggerType.Follow: SetActive(FollowTypeBtn, FollowLabel, "#FFB400FF"); break;
            case ActionTriggerType.Chat:   SetActive(ChatTypeBtn, ChatLabel, "#FF00C8FF"); break;
            case ActionTriggerType.Like:   SetActive(LikeTypeBtn, LikeLabel, "#FFFF5050"); break;
            case ActionTriggerType.Join:   SetActive(JoinTypeBtn, JoinLabel, "#FF00E6B4"); break;
        }

        // Show Follow-specific options
        FollowOptionsPanel.Visibility = type == ActionTriggerType.Follow && _formOpen
            ? Visibility.Visible : Visibility.Collapsed;

        if (type == ActionTriggerType.Follow || type == ActionTriggerType.Join)
        {
            TriggerKeyPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            TriggerKeyPanel.Visibility = Visibility.Visible;
            if (type == ActionTriggerType.Gift)
            {
                TriggerKeyLabel.Text = "GIFT NAME";
                GiftPickerPanel.Visibility = _formOpen ? Visibility.Visible : Visibility.Collapsed;
                TriggerKeyBox.Visibility = Visibility.Collapsed;
            }
            else
            {
                GiftPickerPanel.Visibility = Visibility.Collapsed;
                TriggerKeyBox.Visibility = Visibility.Visible;
                TriggerKeyLabel.Text = type switch
                {
                    ActionTriggerType.Like => "MIN LIKES (leave empty for any)",
                    _ => "KEYWORD (leave empty to match all chat)"
                };
                TriggerKeyBox.PlaceholderText = type switch
                {
                    ActionTriggerType.Like => "e.g. 10",
                    _ => "e.g. !spawn (optional)"
                };
            }
        }
    }

    // ?? Action mode ?????????????????????????????????????????????????????????

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
            LibraryPanel.Visibility = Visibility.Collapsed;
            ButtonPanel.Visibility = Visibility.Visible;
        }
        else
        {
            SetModeActive(ActionCommandBtn, ActionCommandLabel, "#FF00FF88");
            SetModeInactive(ActionButtonBtn, ActionButtonLabel);
            CommandPanel.Visibility = Visibility.Visible;
            LibraryPanel.Visibility = Visibility.Visible;
            ButtonPanel.Visibility = Visibility.Collapsed;
        }
    }

    // ?? Command hint ????????????????????????????????????????????????????????

    private void CommandBox_TextChanged(object sender, string text)
    {
        var hasNickname = text.Contains("{nickname}", StringComparison.OrdinalIgnoreCase);
        var hasUsername = text.Contains("{username}", StringComparison.OrdinalIgnoreCase);

        if (hasNickname || hasUsername)
        {
            var preview = EventProcessor.BuildCommand(text, "TestPlayer", "testplayer");
            SetHint($"Preview: {preview}", "#FF00FF88", "#FF0D1F0D");
        }
        else
        {
            SetHint("{nickname} = display name  |  {username} = TikTok username",
                "#FF8888AA", "#FF0D0D1A");
        }
    }

    private void SetHint(string msg, string fgHex, string bgHex)
    {
        CommandHintText.Text = msg;
        CommandHintText.Foreground = new SolidColorBrush(ParseColor(fgHex));
        CommandHintBorder.Background = new SolidColorBrush(ParseColor(bgHex));
        CommandHintBorder.BorderBrush = new SolidColorBrush(ParseColor(fgHex));
    }

    // ?? Gift picker ?????????????????????????????????????????????????????????

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
            sender.ItemsSource = string.IsNullOrEmpty(query)
                ? TikTokGiftLibrary.All.Where(g => !_selectedGifts.Any(s => s.Name == g.Name)).ToList()
                : TikTokGiftLibrary.Search(query).Where(g => !_selectedGifts.Any(s => s.Name == g.Name)).ToList();
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
                AddGiftToSelection(found);
            sender.Text = string.Empty;
        }
    }

    private void AddGiftToSelection(TikTokGift gift)
    {
        if (_selectedGifts.Any(g => g.Name == gift.Name)) return;
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
            stack.Children.Add(new Image { Width = 18, Height = 18, Source = new BitmapImage(new Uri(gift.ImageUrl)) });
            stack.Children.Add(new TextBlock
            {
                Text = gift.Name, FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 224, 224, 255)),
                VerticalAlignment = VerticalAlignment.Center
            });
            stack.Children.Add(new TextBlock
            {
                Text = $"{gift.CoinPrice}\U0001FA99", FontSize = 9,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 149, 0)),
                VerticalAlignment = VerticalAlignment.Center
            });
            var removeBtn = new Button
            {
                Content = "\u2715", FontSize = 9, Padding = new Thickness(3, 1, 3, 1),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 136, 136, 170)),
                BorderThickness = new Thickness(0), Tag = gift, VerticalAlignment = VerticalAlignment.Center
            };
            removeBtn.Click += (s, _) => { if (s is Button b && b.Tag is TikTokGift g) RemoveGiftFromSelection(g); };
            stack.Children.Add(removeBtn);
            chip.Child = stack;
            SelectedGiftsList.Items.Add(chip);
        }
    }

    // ?? Library ?????????????????????????????????????????????????????????????

    private void SavedCommand_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SavedCommand cmd)
            CommandBox.Text = cmd.Command;
    }

    private void SaveCommand_Click(object sender, RoutedEventArgs e)
    {
        var command = CommandBox.Text?.Trim() ?? string.Empty;
        var label = SaveLabelBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(label)) return;

        var saved = new SavedCommand { Label = label, Command = command };
        ViewModel.AddSavedCommand(saved);

        if (SavedCommandsList.ItemsSource is ObservableCollection<SavedCommand> list)
        {
            var existing = list.FirstOrDefault(c => string.Equals(c.Label, label, StringComparison.OrdinalIgnoreCase));
            if (existing != null) existing.Command = command;
            else list.Add(saved);
        }
        SaveLabelBox.Text = string.Empty;
    }

    private void DeleteSavedCommand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SavedCommand cmd)
        {
            ViewModel.DeleteSavedCommand(cmd);
            if (SavedCommandsList.ItemsSource is ObservableCollection<SavedCommand> list)
                list.Remove(cmd);
        }
    }

    // ?? Add / Cancel ????????????????????????????????????????????????????????

    private void ConfirmAdd_Click(object sender, RoutedEventArgs e)
    {
        string triggerKey;
        if (_selectedType == ActionTriggerType.Gift)
        {
            if (_selectedGifts.Count == 0) return;
            triggerKey = string.Join("|", _selectedGifts.Select(g => g.Name));
        }
        else
        {
            triggerKey = TriggerKeyBox.Text?.Trim() ?? string.Empty;
        }

        var replaceJoinMob = _selectedType == ActionTriggerType.Follow && ReplaceJoinMobCheck.IsChecked == true;

        ActionMappingItem result;
        if (_useButtonAction)
        {
            if (ButtonCombo.SelectedItem is not CommandButton selectedButton) return;
            var btnName = ViewModel.CommandButtons
                .FirstOrDefault(b => b.Id == selectedButton.Id)?.Name ?? string.Empty;
            result = new ActionMappingItem
            {
                TriggerType = _selectedType, TriggerKey = triggerKey,
                Command = string.Empty, TargetButtonId = selectedButton.Id,
                TargetButtonName = btnName, ReplaceJoinMob = replaceJoinMob
            };
        }
        else
        {
            var command = CommandBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(command)) return;
            result = new ActionMappingItem
            {
                TriggerType = _selectedType, TriggerKey = triggerKey,
                Command = command, TargetButtonId = string.Empty,
                ReplaceJoinMob = replaceJoinMob
            };
        }

        ViewModel.AddActionMapping(result);
        ResetForm();
    }

    private void CancelAdd_Click(object sender, RoutedEventArgs e)
    {
        ResetForm();
    }

    private void ResetForm()
    {
        _formOpen = false;
        _libraryOpen = false;
        AddFormPanel.Visibility = Visibility.Collapsed;
        GiftPickerPanel.Visibility = Visibility.Collapsed;
        FollowOptionsPanel.Visibility = Visibility.Collapsed;
        LibraryListBorder.Visibility = Visibility.Collapsed;
        LibraryToggleIcon.Glyph = "\uE70D";
        _selectedGifts.Clear();
        RefreshSelectedGiftsDisplay();
        GiftSuggestBox.Text = string.Empty;
        CommandBox.Text = string.Empty;
        TriggerKeyBox.Text = string.Empty;
        SaveLabelBox.Text = string.Empty;
        ReplaceJoinMobCheck.IsChecked = false;
        ApplyTypeSelection(ActionTriggerType.Gift);
        ApplyActionMode(false);
    }

    // ?? Mapping list actions ????????????????????????????????????????????????

    private void TestMapping_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ActionMappingItem item)
            _ = ViewModel.TestMappingCommand.ExecuteAsync(item);
    }

    private void RemoveMapping_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ActionMappingItem item)
            ViewModel.RemoveActionMapping(item);
    }

    // ?? Style helpers ???????????????????????????????????????????????????????

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
        var inactive = new SolidColorBrush(ParseColor("#FF8888AA"));
        border.BorderBrush = new SolidColorBrush(ParseColor("#FF1A1A2E"));
        border.Background = new SolidColorBrush(ParseColor("#FF16162A"));
        label.Foreground = inactive;
        if (border.Child is StackPanel sp && sp.Children.Count > 0 && sp.Children[0] is FontIcon icon)
            icon.Foreground = inactive;
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

    private static Windows.UI.Color ParseColor(string hex)
    {
        hex = hex.TrimStart('#');
        return Windows.UI.Color.FromArgb(
            Convert.ToByte(hex[0..2], 16),
            Convert.ToByte(hex[2..4], 16),
            Convert.ToByte(hex[4..6], 16),
            Convert.ToByte(hex[6..8], 16));
    }
}
