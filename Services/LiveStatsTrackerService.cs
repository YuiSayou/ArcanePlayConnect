using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using ArcanePlayConnect.Core.Models;

namespace ArcanePlayConnect.Services;

/// <summary>
/// Tracks live session statistics for TikTok viewers:
/// - Total likes per viewer
/// - Total coins spent (gifts) per viewer, with gift breakdown
/// Data is kept in-memory for the current session and served to OBS overlay endpoints.
/// </summary>
public class LiveStatsTrackerService
{
    private readonly ConcurrentDictionary<string, ViewerLikeStats> _likeStats = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ViewerGiftStats> _giftStats = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Records likes from a viewer.</summary>
    public void RecordLikes(string username, string nickname, string profilePictureUrl, int likeCount)
    {
        if (string.IsNullOrWhiteSpace(username)) return;

        _likeStats.AddOrUpdate(username,
            _ => new ViewerLikeStats
            {
                Username = username,
                Nickname = nickname,
                ProfilePictureUrl = profilePictureUrl,
                TotalLikes = likeCount
            },
            (_, existing) =>
            {
                existing.TotalLikes += likeCount;
                if (!string.IsNullOrEmpty(nickname)) existing.Nickname = nickname;
                if (!string.IsNullOrEmpty(profilePictureUrl)) existing.ProfilePictureUrl = profilePictureUrl;
                return existing;
            });
    }

    /// <summary>Records a gift from a viewer.</summary>
    public void RecordGift(string username, string nickname, string profilePictureUrl, string giftName, int coinCost, int repeatCount)
    {
        if (string.IsNullOrWhiteSpace(username)) return;

        var totalCoins = coinCost * Math.Max(1, repeatCount);

        _giftStats.AddOrUpdate(username,
            _ => new ViewerGiftStats
            {
                Username = username,
                Nickname = nickname,
                ProfilePictureUrl = profilePictureUrl,
                TotalCoinsSpent = totalCoins,
                GiftCount = repeatCount
            },
            (_, existing) =>
            {
                existing.TotalCoinsSpent += totalCoins;
                existing.GiftCount += repeatCount;
                if (!string.IsNullOrEmpty(nickname)) existing.Nickname = nickname;
                if (!string.IsNullOrEmpty(profilePictureUrl)) existing.ProfilePictureUrl = profilePictureUrl;
                return existing;
            });
    }

    /// <summary>Returns the top N viewers by total likes.</summary>
    public List<ViewerLikeStats> GetLikesLeaderboard(int maxEntries = 10)
    {
        return _likeStats.Values
            .OrderByDescending(v => v.TotalLikes)
            .Take(maxEntries)
            .ToList();
    }

    /// <summary>Returns the top N viewers by total coins spent on gifts.</summary>
    public List<ViewerGiftStats> GetGiftLeaderboard(int maxEntries = 10)
    {
        return _giftStats.Values
            .OrderByDescending(v => v.TotalCoinsSpent)
            .Take(maxEntries)
            .ToList();
    }

    /// <summary>Clears all tracked stats for a fresh session.</summary>
    public void Reset()
    {
        _likeStats.Clear();
        _giftStats.Clear();
    }
}

/// <summary>Tracks likes for a single viewer.</summary>
public class ViewerLikeStats
{
    public string Username { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public string ProfilePictureUrl { get; set; } = string.Empty;
    public long TotalLikes { get; set; }
}

/// <summary>Tracks gift spending for a single viewer.</summary>
public class ViewerGiftStats
{
    public string Username { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public string ProfilePictureUrl { get; set; } = string.Empty;
    public long TotalCoinsSpent { get; set; }
    public int GiftCount { get; set; }
}
