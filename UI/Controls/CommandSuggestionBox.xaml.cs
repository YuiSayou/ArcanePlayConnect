using System;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ArcanePlayConnect.Core;

namespace ArcanePlayConnect.UI.Controls;

public sealed partial class CommandSuggestionBox : UserControl
{
    private readonly ObservableCollection<CommandSuggestion> _suggestions = new();
    private bool _suppressTextChanged;
    private bool _hasFocus;
    private bool _isBuilderMode;

    /// <summary>Fired whenever the text changes (from user typing or suggestion pick).</summary>
    public event EventHandler<string>? TextChanged;

    /// <summary>Gets or sets the current text in the input box.</summary>
    public string Text
    {
        get => InputBox.Text ?? string.Empty;
        set
        {
            _suppressTextChanged = true;
            InputBox.Text = value;
            _suppressTextChanged = false;
        }
    }

    /// <summary>Gets or sets the placeholder text.</summary>
    public string PlaceholderText
    {
        get => InputBox.PlaceholderText;
        set => InputBox.PlaceholderText = value;
    }

    public new Brush BorderBrush
    {
        get => InputBox.BorderBrush;
        set => InputBox.BorderBrush = value;
    }

    public CommandSuggestionBox()
    {
        InitializeComponent();
        SuggestionList.ItemsSource = _suggestions;
    }

    /// <summary>Programmatically focuses the inner text input.</summary>
    public void FocusInput()
    {
        InputBox.Focus(FocusState.Programmatic);
    }

    // ?? Mode toggle ?????????????????????????????????????????????????????????

    private void TypeMode_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        SetMode(false);
    }

    private void BuilderMode_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        SetMode(true);
    }

    private void SetMode(bool builderMode)
    {
        _isBuilderMode = builderMode;

        if (builderMode)
        {
            // Activate builder, deactivate type
            TypeModePanel.Visibility = Visibility.Collapsed;
            SyntaxHintBar.Visibility = Visibility.Collapsed;
            SuggestionPopup.Visibility = Visibility.Collapsed;
            BuilderModePanel.Visibility = Visibility.Visible;
            BuilderControl.Reset();

            SetToggleActive(BuilderModeBtn, BuilderModeLabel, "#FFFF9500");
            SetToggleInactive(TypeModeBtn, TypeModeLabel);
        }
        else
        {
            // Activate type, deactivate builder
            TypeModePanel.Visibility = Visibility.Visible;
            BuilderModePanel.Visibility = Visibility.Collapsed;

            SetToggleActive(TypeModeBtn, TypeModeLabel, "#FF00FF88");
            SetToggleInactive(BuilderModeBtn, BuilderModeLabel);

            InputBox.Focus(FocusState.Programmatic);
        }
    }

    private static void SetToggleActive(Border border, TextBlock label, string colorHex)
    {
        var color = ParseColor(colorHex);
        var brush = new SolidColorBrush(color);
        border.BorderBrush = brush;
        border.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, color.R, color.G, color.B));
        label.Foreground = brush;
        if (border.Child is StackPanel sp && sp.Children.Count > 0 && sp.Children[0] is FontIcon icon)
            icon.Foreground = brush;
    }

    private static void SetToggleInactive(Border border, TextBlock label)
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

    // ?? Builder event ???????????????????????????????????????????????????????

    private void BuilderControl_CommandBuilt(object? sender, string command)
    {
        // Put the built command into the text box and switch to type mode
        _suppressTextChanged = true;
        InputBox.Text = command;
        _suppressTextChanged = false;
        SetMode(false);
        TextChanged?.Invoke(this, command);
    }

    // ?? Text input handling ?????????????????????????????????????????????????

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextChanged) return;

        var text = InputBox.Text ?? string.Empty;
        TextChanged?.Invoke(this, text);
        UpdateSuggestions(text);
    }

    private void UpdateSuggestions(string text)
    {
        _suggestions.Clear();

        if (string.IsNullOrWhiteSpace(text))
        {
            SuggestionPopup.Visibility = Visibility.Collapsed;
            SyntaxHintBar.Visibility = Visibility.Collapsed;
            return;
        }

        // Update syntax hint
        var hint = MinecraftCommandEngine.GetSyntaxHint(text);
        if (hint != null)
        {
            SyntaxHintText.Text = hint;
            SyntaxHintBar.Visibility = Visibility.Visible;
        }
        else
        {
            SyntaxHintBar.Visibility = Visibility.Collapsed;
        }

        // Get suggestions
        var suggestions = MinecraftCommandEngine.GetSuggestions(text, 20);
        if (suggestions.Count == 0)
        {
            SuggestionPopup.Visibility = Visibility.Collapsed;
            return;
        }

        // Don't show popup if the only suggestion exactly matches what's already typed
        if (suggestions.Count == 1)
        {
            var trimmed = text.TrimStart('/');
            if (suggestions[0].InsertText.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                SuggestionPopup.Visibility = Visibility.Collapsed;
                return;
            }
        }

        foreach (var s in suggestions)
            _suggestions.Add(s);

        SuggestionPopup.Visibility = _hasFocus ? Visibility.Visible : Visibility.Collapsed;
    }

    // ?? Keyboard navigation ?????????????????????????????????????????????????

    private void InputBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (SuggestionPopup.Visibility != Visibility.Visible || _suggestions.Count == 0)
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                SuggestionPopup.Visibility = Visibility.Collapsed;
                e.Handled = true;
            }
            return;
        }

        switch (e.Key)
        {
            case Windows.System.VirtualKey.Down:
            {
                var idx = SuggestionList.SelectedIndex;
                if (idx < _suggestions.Count - 1)
                    SuggestionList.SelectedIndex = idx + 1;
                else
                    SuggestionList.SelectedIndex = 0;
                SuggestionList.ScrollIntoView(SuggestionList.SelectedItem);
                e.Handled = true;
                break;
            }
            case Windows.System.VirtualKey.Up:
            {
                var idx = SuggestionList.SelectedIndex;
                if (idx > 0)
                    SuggestionList.SelectedIndex = idx - 1;
                else
                    SuggestionList.SelectedIndex = _suggestions.Count - 1;
                SuggestionList.ScrollIntoView(SuggestionList.SelectedItem);
                e.Handled = true;
                break;
            }
            case Windows.System.VirtualKey.Tab:
            case Windows.System.VirtualKey.Enter:
            {
                var selected = SuggestionList.SelectedItem as CommandSuggestion
                               ?? _suggestions.FirstOrDefault();
                if (selected != null)
                {
                    ApplySuggestion(selected);
                    e.Handled = true;
                }
                break;
            }
            case Windows.System.VirtualKey.Escape:
                SuggestionPopup.Visibility = Visibility.Collapsed;
                e.Handled = true;
                break;
        }
    }

    // ?? Suggestion selection ????????????????????????????????????????????????

    private void SuggestionList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CommandSuggestion suggestion)
            ApplySuggestion(suggestion);
    }

    private void ApplySuggestion(CommandSuggestion suggestion)
    {
        var text = InputBox.Text ?? string.Empty;
        var trimmed = text.TrimStart('/');
        var hadSlash = text.StartsWith('/');

        var parts = trimmed.Split(' ');
        var hasTrailingSpace = trimmed.EndsWith(' ');

        string newText;

        if (parts.Length <= 1 && !hasTrailingSpace)
        {
            // Replacing the command name
            newText = suggestion.InsertText + " ";
        }
        else
        {
            // Replacing the last token
            var prefix = hasTrailingSpace
                ? trimmed
                : string.Join(' ', parts.Take(parts.Length - 1)) + " ";

            newText = prefix + suggestion.InsertText + " ";
        }

        if (hadSlash)
            newText = "/" + newText;

        _suppressTextChanged = true;
        InputBox.Text = newText;
        InputBox.SelectionStart = newText.Length;
        _suppressTextChanged = false;

        SuggestionPopup.Visibility = Visibility.Collapsed;
        TextChanged?.Invoke(this, newText);

        // Immediately fetch next suggestions
        UpdateSuggestions(newText);

        InputBox.Focus(FocusState.Programmatic);
    }

    // ?? Focus management ????????????????????????????????????????????????????

    private void InputBox_GotFocus(object sender, RoutedEventArgs e)
    {
        _hasFocus = true;
        if (_suggestions.Count > 0)
            SuggestionPopup.Visibility = Visibility.Visible;
    }

    private void InputBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // Delay hiding so click on suggestion list can complete
        _hasFocus = false;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!_hasFocus)
                SuggestionPopup.Visibility = Visibility.Collapsed;
        });
    }
}
