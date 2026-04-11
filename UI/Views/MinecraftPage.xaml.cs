using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ArcanePlayConnect.Core.Models;
using ArcanePlayConnect.Services;
using ArcanePlayConnect.UI.ViewModels;

namespace ArcanePlayConnect.UI.Views;

public sealed partial class MinecraftPage : Page
{
    public MainViewModel ViewModel { get; } = MainViewModel.Instance;

    private readonly ObservableCollection<CreatureDisplayItem> _activeItems = new();
    private readonly ObservableCollection<LeaderboardDisplayItem> _leaderboardItems = new();
    private List<CommandButton> _summonButtons = new();

    public MinecraftPage()
    {
        InitializeComponent();
        ActiveCreaturesList.ItemsSource = _activeItems;
        LeaderboardList.ItemsSource = _leaderboardItems;

        ViewModel.CreatureTracker.CreaturesUpdated += OnCreaturesUpdated;
        ViewModel.CommandButtons.CollectionChanged += (_, _) => RefreshButtonCombos();

        UpdateUI();
        UpdateTrackingButton();
        RefreshButtonCombos();
        RestoreAutoRespawnSettings();
        RestoreSummonLimitSetting();
    }

    private void OnCreaturesUpdated()
    {
        DispatcherQueue.TryEnqueue(UpdateUI);
    }

    private void UpdateUI()
    {
        var active = ViewModel.CreatureTracker.GetActiveCreatures();
        var leaderboard = ViewModel.CreatureTracker.GetLeaderboard();

        for (int i = 0; i < active.Count; i++)
            active[i].Rank = i + 1;

        _activeItems.Clear();
        foreach (var c in active)
            _activeItems.Add(CreatureDisplayItem.From(c));

        _leaderboardItems.Clear();
        foreach (var entry in leaderboard)
            _leaderboardItems.Add(LeaderboardDisplayItem.From(entry));

        var aliveCount = active.Count;
        var deadCount = leaderboard.Count(c => !c.HasAlive);

        AliveCountText.Text = aliveCount.ToString();
        DeadCountText.Text = deadCount.ToString();
        TotalCountText.Text = leaderboard.Count.ToString();

        var isPolling = ViewModel.CreatureTracker.IsPolling;
        TrackingStatusText.Text = isPolling ? "ON" : "OFF";
        TrackingStatusText.Foreground = new SolidColorBrush(
            isPolling ? Windows.UI.Color.FromArgb(255, 0, 255, 136) : Windows.UI.Color.FromArgb(255, 136, 136, 170));

        EmptyState.Visibility = aliveCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        ActiveCreaturesList.Visibility = aliveCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ToggleTracking_Click(object sender, RoutedEventArgs e)
    {
        var tracker = ViewModel.CreatureTracker;
        if (tracker.IsPolling)
            tracker.StopPolling();
        else
            tracker.StartPolling();

        UpdateTrackingButton();
        UpdateUI();
    }

    private void UpdateTrackingButton()
    {
        var isPolling = ViewModel.CreatureTracker.IsPolling;
        ToggleTrackingText.Text = isPolling ? "Stop Tracking" : "Start Tracking";
        ToggleTrackingIcon.Glyph = isPolling ? "\uE71A" : "\uE768";
    }

    private async void KillAll_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.CreatureTracker.KillAllCreaturesAsync();
    }

    private void ResetSession_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CreatureTracker.ResetSession();
        UpdateUI();
        UpdateTrackingButton();
    }

    private void RestoreSummonLimitSetting()
    {
        var mode = ViewModel.CreatureTracker.SummonLimit;
        SummonLimitCombo.SelectedIndex = mode == SummonLimitMode.Unlimited ? 1 : 0;
        UpdateSummonLimitDescription(mode);
    }

    private void SummonLimitCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SummonLimitCombo.SelectedItem is not ComboBoxItem item) return;

        var mode = item.Tag?.ToString() == "Unlimited"
            ? SummonLimitMode.Unlimited
            : SummonLimitMode.OnePerPlayer;

        ViewModel.CreatureTracker.SummonLimit = mode;
        UpdateSummonLimitDescription(mode);
    }

    private void UpdateSummonLimitDescription(SummonLimitMode mode)
    {
        SummonLimitDescription.Text = mode switch
        {
            SummonLimitMode.Unlimited => "Viewers can freely summon any number of creatures",
            _ => "Each viewer can only have 1 active creature"
        };
    }

    private void RefreshButtonCombos()
    {
        _summonButtons = ViewModel.CommandButtons
            .Where(b => b.ButtonType == CommandButtonType.Summon && !string.IsNullOrEmpty(b.SummonEntityType))
            .ToList();

        var tracker = ViewModel.CreatureTracker;
        var followerSavedId = tracker.AutoRespawnFollowerButtonId;
        var nonFollowerSavedId = tracker.AutoRespawnNonFollowerButtonId;

        var displayNames = _summonButtons.Select(b => b.Name).ToList();

        FollowerButtonCombo.ItemsSource = displayNames;
        NonFollowerButtonCombo.ItemsSource = displayNames;

        var followerIdx = _summonButtons.FindIndex(b => b.Id == followerSavedId);
        var nonFollowerIdx = _summonButtons.FindIndex(b => b.Id == nonFollowerSavedId);

        FollowerButtonCombo.SelectedIndex = followerIdx;
        NonFollowerButtonCombo.SelectedIndex = nonFollowerIdx;
    }

    private void RestoreAutoRespawnSettings()
    {
        var tracker = ViewModel.CreatureTracker;
        AutoRespawnToggle.IsOn = tracker.AutoRespawnEnabled;
        RespawnDelayBox.Value = tracker.AutoRespawnDelaySeconds;
        AutoRespawnOptions.Visibility = tracker.AutoRespawnEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AutoRespawnToggle_Toggled(object sender, RoutedEventArgs e)
    {
        var tracker = ViewModel.CreatureTracker;
        tracker.AutoRespawnEnabled = AutoRespawnToggle.IsOn;
        AutoRespawnOptions.Visibility = AutoRespawnToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;

        var delay = RespawnDelayBox.Value;
        tracker.AutoRespawnDelaySeconds = double.IsNaN(delay) ? 5 : (int)Math.Max(1, delay);
    }

    private void FollowerButtonCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var idx = FollowerButtonCombo.SelectedIndex;
        ViewModel.CreatureTracker.AutoRespawnFollowerButtonId =
            idx >= 0 && idx < _summonButtons.Count ? _summonButtons[idx].Id : string.Empty;
        SyncRespawnDelay();
    }

    private void NonFollowerButtonCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var idx = NonFollowerButtonCombo.SelectedIndex;
        ViewModel.CreatureTracker.AutoRespawnNonFollowerButtonId =
            idx >= 0 && idx < _summonButtons.Count ? _summonButtons[idx].Id : string.Empty;
        SyncRespawnDelay();
    }

    private void SyncRespawnDelay()
    {
        var delay = RespawnDelayBox.Value;
        ViewModel.CreatureTracker.AutoRespawnDelaySeconds = double.IsNaN(delay) ? 5 : (int)Math.Max(1, delay);
    }

    private void RespawnDelayBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        SyncRespawnDelay();
    }
}
