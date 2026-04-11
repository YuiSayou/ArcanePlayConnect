using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ArcanePlayConnect.Core;

namespace ArcanePlayConnect.UI.Controls;

public sealed partial class CommandBuilderPanel : UserControl
{
    private string _selectedCategory = "";
    private string _selectedCommand = "";
    private List<MinecraftCommandEngine.BuilderStep> _steps = new();
    private int _currentStepIndex;
    private readonly List<string> _collectedValues = new();
    private string[] _currentOptions = Array.Empty<string>();

    /// <summary>Fired when the builder produces a complete command string.</summary>
    public event EventHandler<string>? CommandBuilt;

    public CommandBuilderPanel()
    {
        InitializeComponent();
        ShowCategoryPanel();
    }

    /// <summary>Resets the builder to the initial state.</summary>
    public void Reset()
    {
        _selectedCategory = "";
        _selectedCommand = "";
        _steps.Clear();
        _currentStepIndex = 0;
        _collectedValues.Clear();
        ShowCategoryPanel();
    }

    // ?? Panel visibility helpers ????????????????????????????????????????????

    private void ShowCategoryPanel()
    {
        CategoryPanel.Visibility = Visibility.Visible;
        CommandPanel.Visibility = Visibility.Collapsed;
        StepsPanel.Visibility = Visibility.Collapsed;
        PreviewBorder.Visibility = Visibility.Collapsed;

        CategoryList.ItemsSource = MinecraftCommandEngine.BuilderCategories.Select(c => c.Category).ToList();
    }

    private void ShowCommandPanel(string category)
    {
        _selectedCategory = category;
        CategoryPanel.Visibility = Visibility.Collapsed;
        CommandPanel.Visibility = Visibility.Visible;
        StepsPanel.Visibility = Visibility.Collapsed;
        PreviewBorder.Visibility = Visibility.Collapsed;

        CommandPanelHeader.Text = category.ToUpperInvariant();

        var commands = MinecraftCommandEngine.BuilderCategories
            .FirstOrDefault(c => c.Category == category).Commands ?? Array.Empty<string>();
        CommandList.ItemsSource = commands.ToList();
    }

    private void ShowStepsPanel(string commandName)
    {
        _selectedCommand = commandName;
        _steps = MinecraftCommandEngine.GetBuilderSteps(commandName);
        _currentStepIndex = 0;
        _collectedValues.Clear();

        CategoryPanel.Visibility = Visibility.Collapsed;
        CommandPanel.Visibility = Visibility.Collapsed;
        StepsPanel.Visibility = Visibility.Visible;

        StepsPanelHeader.Text = $"/{commandName}".ToUpperInvariant();

        if (_steps.Count == 0)
        {
            // No args needed, emit command directly
            CommandBuilt?.Invoke(this, commandName);
            Reset();
            return;
        }

        ShowCurrentStep();
        UpdatePreview();
    }

    private void ShowCurrentStep()
    {
        if (_currentStepIndex >= _steps.Count) return;

        var step = _steps[_currentStepIndex];
        StepLabel.Text = step.Label;
        StepHint.Text = step.Hint;
        StepCounter.Text = $"Step {_currentStepIndex + 1} / {_steps.Count}";

        bool isLast = _currentStepIndex == _steps.Count - 1;
        NextBtnText.Text = isLast ? "Build" : "Next";

        SkipBtn.Visibility = step.IsOptional ? Visibility.Visible : Visibility.Collapsed;

        _currentOptions = step.Options;

        if (step.IsFreeText)
        {
            // Show free text + optional presets
            OptionsPanel.Visibility = step.Options.Length > 10 ? Visibility.Visible : Visibility.Collapsed;
            FreeTextPanel.Visibility = Visibility.Visible;
            FreeTextBox.Text = "";
            FreeTextBox.PlaceholderText = step.Hint;

            if (step.Options.Length > 0 && step.Options.Length <= 10)
            {
                PresetsList.ItemsSource = step.Options.ToList();
                PresetsList.Visibility = Visibility.Visible;
            }
            else
            {
                PresetsList.Visibility = Visibility.Collapsed;
            }

            if (step.Options.Length > 10)
            {
                // Show a searchable list alongside the text box
                OptionFilter.Text = "";
                OptionsList.ItemsSource = step.Options.Take(30).ToList();
            }
        }
        else if (step.Options.Length > 0)
        {
            // Show searchable option list
            OptionsPanel.Visibility = Visibility.Visible;
            FreeTextPanel.Visibility = Visibility.Collapsed;
            OptionFilter.Text = "";
            OptionsList.ItemsSource = step.Options.Take(30).ToList();
        }
        else
        {
            OptionsPanel.Visibility = Visibility.Collapsed;
            FreeTextPanel.Visibility = Visibility.Visible;
            FreeTextBox.Text = "";
            FreeTextBox.PlaceholderText = step.Hint;
            PresetsList.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdatePreview()
    {
        var parts = new List<string> { _selectedCommand };
        parts.AddRange(_collectedValues);
        PreviewText.Text = string.Join(" ", parts);
        PreviewBorder.Visibility = Visibility.Visible;
    }

    private string GetCurrentValue()
    {
        if (_currentStepIndex >= _steps.Count) return "";
        var step = _steps[_currentStepIndex];

        if (step.IsFreeText || (step.Options.Length == 0))
        {
            return FreeTextBox.Text?.Trim() ?? "";
        }

        if (OptionsList.SelectedItem is string selected)
            return selected;

        return "";
    }

    private void AdvanceStep(string value)
    {
        _collectedValues.Add(value);
        _currentStepIndex++;
        UpdatePreview();

        if (_currentStepIndex >= _steps.Count)
        {
            EmitCommand();
        }
        else
        {
            ShowCurrentStep();
        }
    }

    private void EmitCommand()
    {
        var parts = new List<string> { _selectedCommand };
        parts.AddRange(_collectedValues);
        var command = string.Join(" ", parts);
        CommandBuilt?.Invoke(this, command);
    }

    // ?? Event handlers ??????????????????????????????????????????????????????

    private void Category_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border && border.Tag is string category)
            ShowCommandPanel(category);
    }

    private void Command_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border && border.Tag is string cmd)
            ShowStepsPanel(cmd);
    }

    private void BackToCategory_Click(object sender, RoutedEventArgs e) => ShowCategoryPanel();

    private void BackToCommand_Click(object sender, RoutedEventArgs e)
    {
        _collectedValues.Clear();
        _currentStepIndex = 0;
        ShowCommandPanel(_selectedCategory);
    }

    private void OptionFilter_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        var filter = sender.Text?.ToLowerInvariant() ?? "";
        var filtered = _currentOptions
            .Where(o => string.IsNullOrEmpty(filter) || o.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Take(30)
            .ToList();
        OptionsList.ItemsSource = filtered;
    }

    private void Option_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is string value)
        {
            var step = _steps[_currentStepIndex];
            if (step.IsFreeText)
            {
                // Put it in the text box so user can edit
                FreeTextBox.Text = value;
            }
            else
            {
                AdvanceStep(value);
            }
        }
    }

    private void Preset_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border && border.Tag is string preset)
            FreeTextBox.Text = preset;
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        var value = GetCurrentValue();
        var step = _steps[_currentStepIndex];

        if (string.IsNullOrWhiteSpace(value) && !step.IsOptional)
        {
            // Highlight missing input
            if (FreeTextPanel.Visibility == Visibility.Visible)
                FreeTextBox.BorderBrush = new SolidColorBrush(ParseColor("#FFFF3278"));
            return;
        }

        AdvanceStep(value);
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        // Skip optional step - don't add to collected values, just advance
        _currentStepIndex++;
        UpdatePreview();

        if (_currentStepIndex >= _steps.Count)
            EmitCommand();
        else
            ShowCurrentStep();
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
