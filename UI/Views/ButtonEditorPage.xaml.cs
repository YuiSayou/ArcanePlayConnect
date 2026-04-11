using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ArcanePlayConnect.Core;
using ArcanePlayConnect.Core.Models;
using ArcanePlayConnect.Services;

namespace ArcanePlayConnect.UI.Views;

public sealed partial class ButtonEditorPage : Page
{
    private CommandButtonType _selectedType = CommandButtonType.General;
    private readonly ObservableCollection<string> _commands = new();
    private bool _isRecordingShortcut;
    private string _recordedShortcut = string.Empty;

    /// <summary>All summonable entity types from the Minecraft command engine.</summary>
    private static readonly string[] AllEntities;

    static ButtonEditorPage()
    {
        // Pull entity list from the command engine's builder steps for "summon"
        var steps = MinecraftCommandEngine.GetBuilderSteps("summon");
        var entityStep = steps.FirstOrDefault(s => s.Label == "Entity");
        AllEntities = entityStep?.Options ?? Array.Empty<string>();
    }

    public CommandButton? Result { get; private set; }
    public event Action<CommandButton>? Saved;
    public event Action? Cancelled;

    /// <summary>
    /// Helper to safely read a NumberBox value, returning 0 for NaN.
    /// Prioritizes parsing the displayed Text (which reflects what the user
    /// actually typed) because NumberBox.Value may not yet be committed if the
    /// user hasn't pressed Enter or moved focus.
    /// </summary>
    private static float SafeFloat(NumberBox? box)
    {
        if (box == null) return 0f;

        // 1. Always try to parse the displayed text first - it reflects what
        //    the user actually typed, even when Value hasn't been committed yet.
        var text = box.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(text))
        {
            // Try the current UI culture first (matches NumberBox's locale formatting)
            if (float.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands,
                               CultureInfo.CurrentCulture, out var fromCulture) && fromCulture >= 0)
                return fromCulture;

            // Fallback: invariant culture (dot decimal separator)
            if (float.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands,
                               CultureInfo.InvariantCulture, out var fromInvariant) && fromInvariant >= 0)
                return fromInvariant;
        }

        // 2. Fall back to the committed Value property
        var v = box.Value;
        if (!double.IsNaN(v))
            return (float)v;

        return 0f;
    }

    public ButtonEditorPage(CommandButton? existing)
    {
        InitializeComponent();
        CommandsList.ItemsSource = _commands;

        // Attach keyboard listener for shortcut recording
        this.KeyDown += OnPageKeyDown;

        if (existing != null)
        {
            HeaderText.Text = "EDIT COMMAND BUTTON";
            NameBox.Text = existing.Name;
            _selectedType = existing.ButtonType;
            UseNicknameToggle.IsOn = existing.UseNickname;
            TrackCreatureToggle.IsOn = existing.SummonTrackCreature;
            BossToggle.IsOn = existing.SummonIsBoss;
            BossNameBox.Text = existing.SummonBossName;
            ContinuousToggle.IsOn = existing.RunContinuously;
            IntervalBox.Value = existing.IntervalSeconds;

            // Keyboard shortcut
            if (!string.IsNullOrWhiteSpace(existing.KeyboardShortcut))
            {
                _recordedShortcut = existing.KeyboardShortcut;
                ShortcutText.Text = existing.KeyboardShortcut;
                ShortcutText.Foreground = new SolidColorBrush(ParseColor("#FFFF9500"));
            }

            // Summon settings
            if (!string.IsNullOrEmpty(existing.SummonEntityType))
                EntityTypeBox.Text = existing.SummonEntityType;
            if (!string.IsNullOrEmpty(existing.SummonPosition))
                PositionBox.Text = existing.SummonPosition;
            CustomHealthBox.Value = existing.SummonCustomHealth;
            CustomAttackBox.Value = existing.SummonCustomAttack;

            // Buff settings
            BuffHealCheck.IsChecked = existing.BuffApplyHeal;
            BuffHealAmountBox.Value = existing.BuffHealAmount;
            BuffDamageCheck.IsChecked = existing.BuffApplyDamage;
            BuffDamageAmountBox.Value = existing.BuffDamageAmount;
            BuffUseNicknameToggle.IsOn = existing.UseNickname;

            foreach (var cmd in existing.Commands)
                _commands.Add(cmd);
        }

        ApplyTypeSelection(_selectedType);
        UpdateBossNameVisibility();
        UpdateSummonPreview();
    }

    private void TypeBtn_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border && border.Tag is string tag &&
            Enum.TryParse<CommandButtonType>(tag, out var type))
        {
            ApplyTypeSelection(type);
        }
    }

    private void ApplyTypeSelection(CommandButtonType type)
    {
        _selectedType = type;

        SetTypeInactive(GeneralTypeBtn, GeneralIcon, GeneralLabel);
        SetTypeInactive(SummonTypeBtn, SummonIcon, SummonLabel);
        SetTypeInactive(HealthCheckTypeBtn, HealthIcon, HealthLabel);

        switch (type)
        {
            case CommandButtonType.General:
                SetTypeActive(GeneralTypeBtn, GeneralIcon, GeneralLabel, "#FF00C8FF");
                SummonBuilderPanel.Visibility = Visibility.Collapsed;
                HealthCheckPanel.Visibility = Visibility.Collapsed;
                CommandsLabel.Text = "COMMANDS (executed in order)";
                TagHint.Visibility = Visibility.Collapsed;
                break;
            case CommandButtonType.Summon:
                SetTypeActive(SummonTypeBtn, SummonIcon, SummonLabel, "#FFFF9500");
                SummonBuilderPanel.Visibility = Visibility.Visible;
                HealthCheckPanel.Visibility = Visibility.Collapsed;
                CommandsLabel.Text = "ADDITIONAL COMMANDS (optional, run after summon)";
                TagHint.Visibility = Visibility.Visible;
                break;
            case CommandButtonType.Buff:
                SetTypeActive(HealthCheckTypeBtn, HealthIcon, HealthLabel, "#FF00C8FF");
                SummonBuilderPanel.Visibility = Visibility.Collapsed;
                HealthCheckPanel.Visibility = Visibility.Visible;
                IntervalPanel.Visibility = ContinuousToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
                CommandsLabel.Text = "ADDITIONAL COMMANDS (optional, run after buff)";
                TagHint.Visibility = Visibility.Visible;
                break;
        }
    }

    private static void SetTypeActive(Border border, FontIcon icon, TextBlock label, string colorHex)
    {
        var color = ParseColor(colorHex);
        var brush = new SolidColorBrush(color);
        border.BorderBrush = brush;
        border.Background = new SolidColorBrush(
            Windows.UI.Color.FromArgb(40, color.R, color.G, color.B));
        icon.Foreground = brush;
        label.Foreground = brush;
    }

    private static void SetTypeInactive(Border border, FontIcon icon, TextBlock label)
    {
        var inactive = new SolidColorBrush(ParseColor("#FF8888AA"));
        border.BorderBrush = new SolidColorBrush(ParseColor("#FF1A1A2E"));
        border.Background = new SolidColorBrush(ParseColor("#FF16162A"));
        icon.Foreground = inactive;
        label.Foreground = inactive;
    }

    // ?? Summon builder events ??

    private void BossToggle_Toggled(object sender, RoutedEventArgs e)
    {
        UpdateBossNameVisibility();
    }

    private void UpdateBossNameVisibility()
    {
        BossNamePanel.Visibility = BossToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
    }

    private void EntityTypeBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;

        var filter = sender.Text?.ToLowerInvariant() ?? "";
        if (string.IsNullOrWhiteSpace(filter))
        {
            sender.ItemsSource = AllEntities.Take(20).ToList();
        }
        else
        {
            var filtered = AllEntities
                .Where(e => e.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                            e.Replace("minecraft:", "").Contains(filter, StringComparison.OrdinalIgnoreCase))
                .Take(20)
                .ToList();
            sender.ItemsSource = filtered;
        }

        UpdateSummonPreview();
    }

    private void EntityTypeBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is string entity)
        {
            sender.Text = entity;
            UpdateSummonPreview();
        }
    }

    private void PositionPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string pos)
        {
            PositionBox.Text = pos;
            UpdateSummonPreview();
        }
    }

    private void StatPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string preset)
        {
            var parts = preset.Split(',');
            if (parts.Length == 2 &&
                float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var hp) &&
                float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var atk))
            {
                CustomHealthBox.Value = hp;
                CustomAttackBox.Value = atk;
                UpdateSummonPreview();
            }
        }
    }

    private void CustomStatBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        UpdateSummonPreview();
    }

    private void UpdateSummonPreview()
    {
        if (SummonPreviewText == null) return;

        var entity = EntityTypeBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(entity))
            entity = "minecraft:zombie";

        var pos = PositionBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(pos))
            pos = "~ ~ ~";

        var hp = SafeFloat(CustomHealthBox);
        var atk = SafeFloat(CustomAttackBox);

        var preview = $"summon {entity} {pos}";
        var notes = "";
        if (hp > 0) notes += $" HP:{hp:F0}";
        if (atk > 0) notes += $" ATK:{atk:F0}";
        if (TrackCreatureToggle.IsOn) notes += " +viewer tag";
        if (UseNicknameToggle.IsOn) notes += " +tiktok name";

        SummonPreviewText.Text = preview + (notes.Length > 0 ? $"  ({notes.Trim()})" : "");
    }

    // ?? Buff preset events ??

    private void HealPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string val &&
            float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
        {
            BuffHealAmountBox.Value = amount;
            BuffHealCheck.IsChecked = true;
        }
    }

    private void DamagePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string val &&
            float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
        {
            BuffDamageAmountBox.Value = amount;
            BuffDamageCheck.IsChecked = true;
        }
    }

    // ?? Continuous toggle ??

    private void ContinuousToggle_Toggled(object sender, RoutedEventArgs e)
    {
        IntervalPanel.Visibility = ContinuousToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
    }

    // ?? Commands list ??

    private void AddCommand_Click(object sender, RoutedEventArgs e)
    {
        var cmd = NewCommandBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(cmd))
        {
            _commands.Add(cmd);
            NewCommandBox.Text = string.Empty;
        }
    }

    private void RemoveCommand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string cmd)
            _commands.Remove(cmd);
    }

    // Keyboard Shortcut Recording

    private void ShortcutDisplay_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        StartRecording();
    }

    private void RecordShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (_isRecordingShortcut)
            StopRecording();
        else
            StartRecording();
    }

    private void ClearShortcut_Click(object sender, RoutedEventArgs e)
    {
        StopRecording();
        _recordedShortcut = string.Empty;
        ShortcutText.Text = "Click to record shortcut...";
        ShortcutText.Foreground = new SolidColorBrush(ParseColor("#FF8888AA"));
        ShortcutDisplay.BorderBrush = new SolidColorBrush(ParseColor("#FF1A1A2E"));
    }

    private void StartRecording()
    {
        _isRecordingShortcut = true;
        ShortcutText.Text = "Press a key combination...";
        ShortcutText.Foreground = new SolidColorBrush(ParseColor("#FF00FF88"));
        ShortcutDisplay.BorderBrush = new SolidColorBrush(ParseColor("#FF00FF88"));
        RecordBtnText.Text = "Stop";
        this.Focus(FocusState.Programmatic);
    }

    private void StopRecording()
    {
        _isRecordingShortcut = false;
        ShortcutDisplay.BorderBrush = new SolidColorBrush(ParseColor("#FF1A1A2E"));
        RecordBtnText.Text = "Record";

        if (string.IsNullOrEmpty(_recordedShortcut))
        {
            ShortcutText.Text = "Click to record shortcut...";
            ShortcutText.Foreground = new SolidColorBrush(ParseColor("#FF8888AA"));
        }
    }

    private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_isRecordingShortcut) return;

        // Ignore standalone modifier presses
        if (e.Key is Windows.System.VirtualKey.Control or
            Windows.System.VirtualKey.Shift or
            Windows.System.VirtualKey.Menu or
            Windows.System.VirtualKey.LeftControl or
            Windows.System.VirtualKey.RightControl or
            Windows.System.VirtualKey.LeftShift or
            Windows.System.VirtualKey.RightShift or
            Windows.System.VirtualKey.LeftMenu or
            Windows.System.VirtualKey.RightMenu or
            Windows.System.VirtualKey.LeftWindows or
            Windows.System.VirtualKey.RightWindows)
            return;

        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        var shortcut = KeyboardShortcutService.FormatShortcut(ctrl, shift, alt, e.Key);

        if (!string.IsNullOrEmpty(shortcut))
        {
            _recordedShortcut = shortcut;
            ShortcutText.Text = shortcut;
            ShortcutText.Foreground = new SolidColorBrush(ParseColor("#FFFF9500"));
            StopRecording();
        }
        else
        {
            ShortcutText.Text = "Need modifier (Ctrl/Shift/Alt) + key";
            ShortcutText.Foreground = new SolidColorBrush(ParseColor("#FFFF3278"));
        }

        e.Handled = true;
    }

    // Save / Cancel

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            NameBox.BorderBrush = new SolidColorBrush(ParseColor("#FFFF3278"));
            return;
        }

        if (_selectedType == CommandButtonType.General)
        {
            if (_commands.Count == 0) return;

            Result = new CommandButton
            {
                Name = name,
                ButtonType = CommandButtonType.General,
                Commands = _commands.ToList(),
                UseNickname = false,
                KeyboardShortcut = _recordedShortcut,
            };
        }
        else if (_selectedType == CommandButtonType.Summon)
        {
            var entityType = EntityTypeBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(entityType))
            {
                EntityTypeBox.Focus(FocusState.Programmatic);
                return;
            }

            Result = new CommandButton
            {
                Name = name,
                ButtonType = CommandButtonType.Summon,
                Commands = _commands.ToList(),
                UseNickname = UseNicknameToggle.IsOn,
                KeyboardShortcut = _recordedShortcut,
                SummonEntityType = entityType,
                SummonPosition = PositionBox.Text?.Trim() ?? "~ ~ ~",
                SummonCustomHealth = SafeFloat(CustomHealthBox),
                SummonCustomAttack = SafeFloat(CustomAttackBox),
                SummonTrackCreature = TrackCreatureToggle.IsOn,
                SummonIsBoss = BossToggle.IsOn,
                SummonBossName = BossToggle.IsOn ? (BossNameBox.Text?.Trim() ?? string.Empty) : string.Empty,
            };
        }
        else
        {
            var applyHeal = BuffHealCheck.IsChecked == true;
            var applyDamage = BuffDamageCheck.IsChecked == true;
            var healAmount = SafeFloat(BuffHealAmountBox);
            var damageAmount = SafeFloat(BuffDamageAmountBox);

            // Must have at least one buff option checked or commands
            if (!applyHeal && !applyDamage && _commands.Count == 0) return;

            Result = new CommandButton
            {
                Name = name,
                ButtonType = CommandButtonType.Buff,
                Commands = _commands.ToList(),
                UseNickname = BuffUseNicknameToggle.IsOn,
                KeyboardShortcut = _recordedShortcut,
                BuffApplyHeal = applyHeal,
                BuffHealAmount = healAmount,
                BuffApplyDamage = applyDamage,
                BuffDamageAmount = damageAmount,
                RunContinuously = ContinuousToggle.IsOn,
                IntervalSeconds = (int)Math.Max(1, IntervalBox.Value)
            };
        }

        Saved?.Invoke(Result);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Cancelled?.Invoke();
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
