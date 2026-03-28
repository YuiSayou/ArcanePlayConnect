using System;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ArcanePlayConnect.Core.Models;

namespace ArcanePlayConnect.UI.Views;

public sealed partial class ButtonEditorPage : Page
{
    private CommandButtonType _selectedType = CommandButtonType.Summon;
    private readonly ObservableCollection<string> _commands = new();

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
            ContinuousToggle.IsOn = existing.RunContinuously;
            IntervalBox.Value = existing.IntervalSeconds;

            foreach (var cmd in existing.Commands)
                _commands.Add(cmd);
        }

        ApplyTypeSelection(_selectedType);
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

        // Reset both
        SetTypeInactive(SummonTypeBtn, SummonIcon, SummonLabel);
        SetTypeInactive(HealthCheckTypeBtn, HealthIcon, HealthLabel);

        switch (type)
        {
            case CommandButtonType.Summon:
                SetTypeActive(SummonTypeBtn, SummonIcon, SummonLabel, "#FFFF9500");
                NicknameOption.Visibility = Visibility.Visible;
                ContinuousOption.Visibility = Visibility.Collapsed;
                break;
            case CommandButtonType.HealthCheck:
                SetTypeActive(HealthCheckTypeBtn, HealthIcon, HealthLabel, "#FF00C8FF");
                NicknameOption.Visibility = Visibility.Collapsed;
                ContinuousOption.Visibility = Visibility.Visible;
                IntervalPanel.Visibility = ContinuousToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
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

    private void ContinuousToggle_Toggled(object sender, RoutedEventArgs e)
    {
        IntervalPanel.Visibility = ContinuousToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
    }

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

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            NameBox.BorderBrush = new SolidColorBrush(ParseColor("#FFFF3278"));
            return;
        }

        if (_commands.Count == 0)
        {
            NewCommandBox.BorderBrush = new SolidColorBrush(ParseColor("#FFFF3278"));
            return;
        }

        Result = new CommandButton
        {
            Name             = name,
            ButtonType       = _selectedType,
            Commands         = _commands.ToList(),
            UseNickname      = _selectedType == CommandButtonType.Summon && UseNicknameToggle.IsOn,
            RunContinuously  = _selectedType == CommandButtonType.HealthCheck && ContinuousToggle.IsOn,
            IntervalSeconds  = (int)IntervalBox.Value
        };

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
