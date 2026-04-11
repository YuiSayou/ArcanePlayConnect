using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ArcanePlayConnect.Core.Models;

namespace ArcanePlayConnect.Services;

/// <summary>
/// Manages a persistent database of TikTok followers.
/// Followers are stored in a JSON file so that Join events can check
/// whether a viewer is already a follower and upgrade them accordingly.
/// </summary>
public class FollowerService
{
    private readonly string _filePath;
    private readonly LoggingService _logger;
    private readonly ConcurrentDictionary<string, Follower> _followers = new(StringComparer.OrdinalIgnoreCase);

    public FollowerService(LoggingService logger)
    {
        _logger = logger;
        var appDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArcanePlayConnect");
        if (!Directory.Exists(appDir))
            Directory.CreateDirectory(appDir);
        _filePath = Path.Combine(appDir, "followers.json");
        Load();
    }

    /// <summary>Returns all followers as a list, sorted by follow date descending.</summary>
    public List<Follower> GetAll()
    {
        return _followers.Values
            .OrderByDescending(f => f.FollowedAt)
            .ToList();
    }

    /// <summary>Returns the number of followers.</summary>
    public int Count => _followers.Count;

    /// <summary>Checks whether a username is in the follower database.</summary>
    public bool IsFollower(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return false;
        return _followers.ContainsKey(username);
    }

    /// <summary>
    /// Adds or updates a follower. Returns true if the follower was newly added.
    /// </summary>
    public bool AddOrUpdate(string username, string nickname, string profilePictureUrl = "")
    {
        if (string.IsNullOrWhiteSpace(username)) return false;

        var isNew = false;
        _followers.AddOrUpdate(
            username,
            _ =>
            {
                isNew = true;
                return new Follower
                {
                    Username = username,
                    Nickname = nickname,
                    ProfilePictureUrl = profilePictureUrl,
                    FollowedAt = DateTime.Now
                };
            },
            (_, existing) =>
            {
                // Update nickname and picture but keep original follow date
                if (!string.IsNullOrWhiteSpace(nickname))
                    existing.Nickname = nickname;
                if (!string.IsNullOrWhiteSpace(profilePictureUrl))
                    existing.ProfilePictureUrl = profilePictureUrl;
                return existing;
            });

        Save();
        return isNew;
    }

    /// <summary>Adds a follower from a Follower model (used by UI add).</summary>
    public bool Add(Follower follower)
    {
        if (string.IsNullOrWhiteSpace(follower.Username)) return false;
        var added = _followers.TryAdd(follower.Username, follower);
        if (added) Save();
        return added;
    }

    /// <summary>Updates an existing follower entry.</summary>
    public void Update(Follower follower)
    {
        if (string.IsNullOrWhiteSpace(follower.Username)) return;
        _followers[follower.Username] = follower;
        Save();
    }

    /// <summary>Removes a follower by username.</summary>
    public bool Remove(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return false;
        var removed = _followers.TryRemove(username, out _);
        if (removed) Save();
        return removed;
    }

    /// <summary>Clears all followers.</summary>
    public void Clear()
    {
        _followers.Clear();
        Save();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var list = JsonSerializer.Deserialize<List<Follower>>(json);
                if (list != null)
                {
                    foreach (var f in list)
                    {
                        if (!string.IsNullOrWhiteSpace(f.Username))
                            _followers[f.Username] = f;
                    }
                }
                _logger.LogInfo($"[Followers] Loaded {_followers.Count} followers from database.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"[Followers] Failed to load followers: {ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(_followers.Values.ToList(), options);
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"[Followers] Failed to save followers: {ex.Message}");
        }
    }
}
