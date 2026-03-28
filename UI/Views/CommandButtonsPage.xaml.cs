using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using ArcanePlayConnect.Core.Models;
using ArcanePlayConnect.UI.ViewModels;
using Windows.Graphics;

namespace ArcanePlayConnect.UI.Views;

public sealed partial class CommandButtonsPage : Page
{
    public MainViewModel ViewModel { get; } = MainViewModel.Instance;

    public CommandButtonsPage()
    {
        InitializeComponent();
    }

    // Static helpers for x:Bind in DataTemplate
    public static string GetTypeGlyph(CommandButtonType type) => type switch
    {
        CommandButtonType.Summon      => "\uEA18",
        CommandButtonType.HealthCheck => "\uE95E",
        _ => "\uE946"
    };

    public static string GetTypeLabel(CommandButtonType type) => type switch
    {
        CommandButtonType.Summon      => "Summon",
        CommandButtonType.HealthCheck => "Health Check",
        _ => "Unknown"
    };

    public static Visibility BoolVis(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    private void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        OpenEditor(null);
    }

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is CommandButton cb)
            OpenEditor(cb);
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is CommandButton cb)
        {
            var dialog = new ContentDialog
            {
                Title = "Delete Button",
                Content = $"Delete '{cb.Name}'? Any action mappings linked to it will also be removed.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                XamlRoot = this.XamlRoot,
                RequestedTheme = ElementTheme.Dark
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                ViewModel.RemoveCommandButton(cb);
        }
    }

    private void RunButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is CommandButton cb)
            _ = ViewModel.ExecuteCommandButton(cb);
    }

    private void OpenEditor(CommandButton? existing)
    {
        var editorPage = new ButtonEditorPage(existing);

        var window = new Window
        {
            Title = existing == null ? "Create Command Button" : "Edit Command Button",
            ExtendsContentIntoTitleBar = true,
            Content = editorPage
        };

        var appWindow = window.AppWindow;
        appWindow.Resize(new SizeInt32(620, 820));

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        editorPage.Saved += result =>
        {
            if (existing == null)
            {
                ViewModel.AddCommandButton(result);
            }
            else
            {
                existing.Name = result.Name;
                existing.ButtonType = result.ButtonType;
                existing.Commands = result.Commands;
                existing.UseNickname = result.UseNickname;
                existing.RunContinuously = result.RunContinuously;
                existing.IntervalSeconds = result.IntervalSeconds;
                ViewModel.UpdateCommandButton(existing);
            }
            window.Close();
        };

        editorPage.Cancelled += () => window.Close();
        window.Activate();
    }
}
