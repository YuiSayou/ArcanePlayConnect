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

namespace ArcanePlayConnect.UI.Views;

public sealed partial class ButtonEditorPage : Page
{
    private CommandButtonType _selectedType = CommandButtonType.Summon;
    private readonly ObservableCollection<string> _commands = new();

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

    public ButtonEditorPage(CommandButton? existing)
    {
        InitializeComponent();
        CommandsList.ItemsSource = _commands;

        if (existing != null)
        {
            HeaderText.Text = "EDIT COMMAND BUTTON";
            NameBox.Text = existing.Name;
            _selectedType = existing.ButtonType;
            UseNicknameToggle.IsOn = existing.UseNickname;
            TrackCreatureToggle.IsOn = existing.SummonTrackCreature;
            BossToggle.IsOn = existing.SummonIsBoss;
            ContinuousToggle.IsOn = existing.RunContinuously;
            IntervalBox.Value = existing.IntervalSeconds;

            // Summon settings
            if (!string.IsNullOrEmpty(existing.SummonEntityType))
                EntityTypeBox.Text = existing.SummonEntityType;
            if (!string.IsNullOrEmpty(existing.SummonPosition))
                PositionBox.Text = existing.SummonPosition;
            CustomHealthBox.Value = existing.SummonCustomHealth;
            CustomAttackBox.Value = existing.SummonCustomAttack;

            foreach (var cmd in existing.Commands)
                _commands.Add(cmd);
        }

        ApplyTypeSelection(_selectedType);
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

        SetTypeInactive(SummonTypeBtn, SummonIcon, SummonLabel);
        SetTypeInactive(HealthCheckTypeBtn, HealthIcon, HealthLabel);

        switch (type)
        {
            case CommandButtonType.Summon:
                SetTypeActive(SummonTypeBtn, SummonIcon, SummonLabel, "#FFFF9500");
                SummonBuilderPanel.Visibility = Visibility.Visible;
                HealthCheckPanel.Visibility = Visibility.Collapsed;
                CommandsLabel.Text = "ADDITIONAL COMMANDS (optional, run after summon)";
                TagHint.Visibility = Visibility.Visible;
                break;
            case CommandButtonType.HealthCheck:
                SetTypeActive(HealthCheckTypeBtn, HealthIcon, HealthLabel, "#FF00C8FF");
                SummonBuilderPanel.Visibility = Visibility.Collapsed;
                HealthCheckPanel.Visibility = Visibility.Visible;
                IntervalPanel.Visibility = ContinuousToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
                CommandsLabel.Text = "COMMANDS (executed in order)";
                TagHint.Visibility = Visibility.Collapsed;
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

    // ?? Summon builder events ???????????????????????????????????????????????

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
            }
        }
    }

    private void UpdateSummonPreview()
    {
        var entity = EntityTypeBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(entity))
            entity = "minecraft:zombie";

        var pos = PositionBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(pos))
            pos = "~ ~ ~";

        var hp = (float)CustomHealthBox.Value;
        var atk = (float)CustomAttackBox.Value;

        var preview = $"summon {entity} {pos}";
        var notes = "";
        if (hp > 0) notes += $" HP:{hp:F0}";
        if (atk > 0) notes += $" ATK:{atk:F0}";
        if (TrackCreatureToggle.IsOn) notes += " +viewer tag";
        if (UseNicknameToggle.IsOn) notes += " +tiktok name";

        SummonPreviewText.Text = preview + (notes.Length > 0 ? $"  ({notes.Trim()})" : "");
    }

    // ?? HealthCheck events ??????????????????????????????????????????????????

    private void ContinuousToggle_Toggled(object sender, RoutedEventArgs e)
    {
        IntervalPanel.Visibility = ContinuousToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
    }

    // ?? Commands list ???????????????????????????????????????????????????????

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

    // ?? Save / Cancel ???????????????????????????????????????????????????????

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            NameBox.BorderBrush = new SolidColorBrush(ParseColor("#FFFF3278"));
            return;
        }

        if (_selectedType == CommandButtonType.Summon)
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
                SummonEntityType = entityType,
                SummonPosition = PositionBox.Text?.Trim() ?? "~ ~ ~",
                SummonCustomHealth = (float)CustomHealthBox.Value,
                SummonCustomAttack = (float)CustomAttackBox.Value,
                SummonTrackCreature = TrackCreatureToggle.IsOn,
                SummonIsBoss = BossToggle.IsOn,
            };
        }
        else
        {
            if (_commands.Count == 0) return;

            Result = new CommandButton
            {
                Name = name,
                ButtonType = CommandButtonType.HealthCheck,
                Commands = _commands.ToList(),
                RunContinuously = ContinuousToggle.IsOn,
                IntervalSeconds = (int)IntervalBox.Value
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
