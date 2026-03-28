using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ArcanePlayConnect.Core.Models;
using ArcanePlayConnect.UI.ViewModels;

namespace ArcanePlayConnect.UI.Views;

public sealed partial class CreatureArenaPage : Page
{
    public MainViewModel ViewModel { get; } = MainViewModel.Instance;

    private readonly ObservableCollection<CreatureDisplayItem> _activeItems = new();
    private readonly ObservableCollection<LeaderboardDisplayItem> _leaderboardItems = new();

    public CreatureArenaPage()
    {
        InitializeComponent();
        ActiveCreaturesList.ItemsSource = _activeItems;
        LeaderboardList.ItemsSource = _leaderboardItems;

        ViewModel.CreatureTracker.CreaturesUpdated += OnCreaturesUpdated;
        UpdateUI();
        UpdateTrackingButton();
    }

    private void OnCreaturesUpdated()
    {
        DispatcherQueue.TryEnqueue(UpdateUI);
    }

    private void UpdateUI()
    {
        var active = ViewModel.CreatureTracker.GetActiveCreatures();
        var leaderboard = ViewModel.CreatureTracker.GetLeaderboard();

        // Assign ranks to active creatures
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

        // Show/hide empty state
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
}

/// <summary>Display wrapper for binding in XAML DataTemplates.</summary>
public class CreatureDisplayItem
{
    public string OwnerNickname { get; set; } = string.Empty;
    public string EntityDisplayName { get; set; } = string.Empty;
    public float CurrentHealth { get; set; }
    public float MaxHealth { get; set; } = 20f;
    public int DamageDealt { get; set; }
    public int KillCount { get; set; }
    public string KilledBy { get; set; } = string.Empty;
    public bool IsAlive { get; set; }
    public int Rank { get; set; }
    public string SurvivalTimeDisplay { get; set; } = string.Empty;

    public string RankDisplay => Rank > 0 ? $"#{Rank}" : "#";
    public string HealthDisplay => $"{CurrentHealth:F0} / {MaxHealth:F0}";
    public double HpPercent => MaxHealth > 0 ? Math.Round((CurrentHealth / MaxHealth) * 100, 1) : 0;
    public string StatusEmoji => IsAlive ? "??" : "??";

    public string KilledByDisplay => string.IsNullOrWhiteSpace(KilledBy) || KilledBy == "none" || KilledBy == "unknown"
        ? "" : $"? {KilledBy}";

    public SolidColorBrush NameColor => new(IsAlive
        ? Windows.UI.Color.FromArgb(255, 224, 224, 255)
        : Windows.UI.Color.FromArgb(255, 136, 136, 170));

    public SolidColorBrush RankColor => new(Rank switch
    {
        1 => Windows.UI.Color.FromArgb(255, 255, 215, 0),   // Gold
        2 => Windows.UI.Color.FromArgb(255, 192, 192, 192), // Silver
        3 => Windows.UI.Color.FromArgb(255, 205, 127, 50),  // Bronze
        _ => Windows.UI.Color.FromArgb(255, 255, 149, 0)    // Orange
    });

    public SolidColorBrush RowBackground => new(IsAlive
        ? Windows.UI.Color.FromArgb(0, 0, 0, 0)
        : Windows.UI.Color.FromArgb(15, 255, 50, 120));

    public static CreatureDisplayItem From(SummonedCreature c) => new()
    {
        OwnerNickname = c.OwnerNickname,
        EntityDisplayName = c.EntityDisplayName,
        CurrentHealth = (float)Math.Round(c.CurrentHealth, 1),
        MaxHealth = (float)Math.Round(c.MaxHealth, 1),
        DamageDealt = c.DamageDealt,
        KillCount = c.KillCount,
        KilledBy = c.KilledBy,
        IsAlive = c.IsAlive,
        Rank = c.Rank,
        SurvivalTimeDisplay = FormatSurvivalTime(c.SurvivalTime, c.IsAlive)
    };

    private static string FormatSurvivalTime(TimeSpan t, bool alive)
    {
        var prefix = alive ? "?" : "?";
        if (t.TotalHours >= 1)
            return $"{prefix} {t:h\\:mm\\:ss}";
        return $"{prefix} {t:mm\\:ss}";
    }
}

/// <summary>Display wrapper for aggregated leaderboard entries (one per viewer, stacked scores).</summary>
public class LeaderboardDisplayItem
{
    public string OwnerNickname { get; set; } = string.Empty;
    public string EntityDisplayName { get; set; } = string.Empty;
    public int DamageDealt { get; set; }
    public int KillCount { get; set; }
    public int CreatureCount { get; set; }
    public string KilledBy { get; set; } = string.Empty;
    public bool HasAlive { get; set; }
    public int Rank { get; set; }
    public string SurvivalTimeDisplay { get; set; } = string.Empty;

    public string RankDisplay => Rank > 0 ? $"#{Rank}" : "#";
    public string StatusEmoji => HasAlive ? "??" : "??";
    public string CreatureCountDisplay => CreatureCount > 1 ? $"×{CreatureCount}" : "";

    public string KilledByDisplay => string.IsNullOrWhiteSpace(KilledBy) || KilledBy == "none" || KilledBy == "unknown"
        ? "" : $"? {KilledBy}";

    public SolidColorBrush NameColor => new(HasAlive
        ? Windows.UI.Color.FromArgb(255, 224, 224, 255)
        : Windows.UI.Color.FromArgb(255, 136, 136, 170));

    public SolidColorBrush RankColor => new(Rank switch
    {
        1 => Windows.UI.Color.FromArgb(255, 255, 215, 0),
        2 => Windows.UI.Color.FromArgb(255, 192, 192, 192),
        3 => Windows.UI.Color.FromArgb(255, 205, 127, 50),
        _ => Windows.UI.Color.FromArgb(255, 255, 149, 0)
    });

    public SolidColorBrush RowBackground => new(HasAlive
        ? Windows.UI.Color.FromArgb(0, 0, 0, 0)
        : Windows.UI.Color.FromArgb(15, 255, 50, 120));

    public static LeaderboardDisplayItem From(AggregatedLeaderboardEntry e) => new()
    {
        OwnerNickname = e.OwnerNickname,
        EntityDisplayName = e.LastEntityDisplayName,
        DamageDealt = e.TotalDamageDealt,
        KillCount = e.TotalKills,
        CreatureCount = e.CreatureCount,
        KilledBy = e.KilledBy,
        HasAlive = e.HasAlive,
        Rank = e.Rank,
        SurvivalTimeDisplay = FormatSurvivalTime(e.BestSurvivalTime, e.HasAlive)
    };

    private static string FormatSurvivalTime(TimeSpan t, bool alive)
    {
        var prefix = alive ? "?" : "?";
        if (t.TotalHours >= 1)
            return $"{prefix} {t:h\\:mm\\:ss}";
        return $"{prefix} {t:mm\\:ss}";
    }
}
