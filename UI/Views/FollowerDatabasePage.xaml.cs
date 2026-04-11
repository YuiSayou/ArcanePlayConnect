using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ArcanePlayConnect.Core.Models;
using ArcanePlayConnect.UI.ViewModels;

namespace ArcanePlayConnect.UI.Views;

public sealed partial class FollowerDatabasePage : Page
{
    public MainViewModel ViewModel { get; } = MainViewModel.Instance;

    private Follower? _editingFollower;
    private bool _isEditMode;

    public FollowerDatabasePage()
    {
        InitializeComponent();
        RefreshList();
    }

    private void RefreshList()
    {
        var all = ViewModel.FollowerService.GetAll();
        var query = SearchBox.Text?.Trim() ?? string.Empty;

        List<Follower> filtered;
        if (string.IsNullOrWhiteSpace(query))
        {
            filtered = all;
        }
        else
        {
            filtered = all.Where(f =>
                f.Username.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                f.Nickname.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        FollowerList.ItemsSource = filtered;
        FollowerCountText.Text = all.Count.ToString();

        EmptyState.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FollowerList.Visibility = filtered.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshList();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshList();
    }

    // ?? Add ?????????????????????????????????????????????????????????????????

    private void AddFollower_Click(object sender, RoutedEventArgs e)
    {
        _isEditMode = false;
        _editingFollower = null;
        EditPanelTitle.Text = "ADD FOLLOWER";
        EditUsernameBox.Text = string.Empty;
        EditUsernameBox.IsEnabled = true;
        EditNicknameBox.Text = string.Empty;
        EditNotesBox.Text = string.Empty;
        EditPanel.Visibility = Visibility.Visible;
    }

    // ?? Edit ????????????????????????????????????????????????????????????????

    private void EditFollower_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Follower follower)
        {
            _isEditMode = true;
            _editingFollower = follower;
            EditPanelTitle.Text = "EDIT FOLLOWER";
            EditUsernameBox.Text = follower.Username;
            EditUsernameBox.IsEnabled = false; // Username is the key, can't change
            EditNicknameBox.Text = follower.Nickname;
            EditNotesBox.Text = follower.Notes;
            EditPanel.Visibility = Visibility.Visible;
        }
    }

    // ?? Save ????????????????????????????????????????????????????????????????

    private void SaveEdit_Click(object sender, RoutedEventArgs e)
    {
        var username = EditUsernameBox.Text?.Trim() ?? string.Empty;
        var nickname = EditNicknameBox.Text?.Trim() ?? string.Empty;
        var notes = EditNotesBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(username)) return;
        if (string.IsNullOrWhiteSpace(nickname)) nickname = username;

        if (_isEditMode && _editingFollower != null)
        {
            _editingFollower.Nickname = nickname;
            _editingFollower.Notes = notes;
            ViewModel.FollowerService.Update(_editingFollower);
        }
        else
        {
            var follower = new Follower
            {
                Username = username,
                Nickname = nickname,
                Notes = notes,
                FollowedAt = DateTime.Now
            };
            ViewModel.FollowerService.Add(follower);
        }

        EditPanel.Visibility = Visibility.Collapsed;
        RefreshList();
    }

    private void CancelEdit_Click(object sender, RoutedEventArgs e)
    {
        EditPanel.Visibility = Visibility.Collapsed;
    }

    // ?? Delete ??????????????????????????????????????????????????????????????

    private void DeleteFollower_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Follower follower)
        {
            ViewModel.FollowerService.Remove(follower.Username);
            RefreshList();
        }
    }

    private async void DeleteAll_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Delete All Followers",
            Content = "Are you sure you want to remove all followers from the database? This cannot be undone.",
            PrimaryButtonText = "Delete All",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            ViewModel.FollowerService.Clear();
            RefreshList();
        }
    }
}
