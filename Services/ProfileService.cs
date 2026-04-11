using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ArcanePlayConnect.Core.Models;

namespace ArcanePlayConnect.Services;

public class ProfileService
{
    private readonly string _profileDirectory;
    private readonly string _settingsFilePath;
    private readonly string _savedCommandsFilePath;
    private readonly LoggingService _logger;

    public ProfileService(LoggingService logger)
    {
        _logger = logger;
        var appDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArcanePlayConnect");
        _profileDirectory = Path.Combine(appDir, "Profiles");
        _settingsFilePath = Path.Combine(appDir, "settings.json");
        _savedCommandsFilePath = Path.Combine(appDir, "saved_commands.json");

        if (!Directory.Exists(_profileDirectory))
        {
            Directory.CreateDirectory(_profileDirectory);
        }
    }

    public List<Profile> LoadAll()
    {
        var profiles = new List<Profile>();
        try
        {
            foreach (var file in Directory.GetFiles(_profileDirectory, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var profile = JsonSerializer.Deserialize<Profile>(json);
                    if (profile != null)
                    {
                        var migrated = MigrateProfile(profile, json);
                        // Persist any migration immediately
                        if (migrated)
                            Save(profile);
                        profiles.Add(profile);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to load profile {Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to enumerate profiles: {ex.Message}");
        }

        return profiles;
    }

    /// <summary>
    /// Fixes legacy profile data in-place.  Returns true if any change was made.
    /// Migrations performed:
    ///   1. Removes duplicate mappings that have identical TriggerType+TriggerKey+Command.
    ///   2. Removes mappings whose Command contains a stray "},CustomName" pattern left
    ///      by a previous bad migration (CustomName:"{nickname}"},CustomNameVisible:1b}).
    ///   3. Old JSON-component CustomName pattern CustomName:'{"text":"{nickname}"}' ?
    ///      plain NBT string CustomName:"{nickname}".
    ///   4. Old GiftMappings dictionary format: recovered from raw JSON if ActionMappings is empty.
    /// </summary>
    private bool MigrateProfile(Profile profile, string rawJson)
    {
        var changed = false;

        // ?? Migration 1: remove exact-duplicate mappings ??????????????????????
        var seen    = new HashSet<string>();
        var deduped = new List<ActionMapping>();
        foreach (var m in profile.ActionMappings)
        {
            var key = $"{(int)m.TriggerType}|{m.TriggerKey}|{m.Command}";
            if (seen.Add(key))
                deduped.Add(m);
            else
                changed = true;
        }
        if (changed)
        {
            profile.ActionMappings.Clear();
            foreach (var m in deduped) profile.ActionMappings.Add(m);
        }

        // ?? Migration 2: remove commands with stray } from bad prior migration ?
        // Pattern: CustomName:"{nickname}"},CustomNameVisible  - the } after the
        // closing quote is wrong; it closes the NBT compound too early.
        var toRemove = new List<ActionMapping>();
        foreach (var m in profile.ActionMappings)
        {
            if (m.Command.Contains("\"},CustomNameVisible", StringComparison.OrdinalIgnoreCase))
                toRemove.Add(m);
        }
        if (toRemove.Count > 0)
        {
            foreach (var m in toRemove) profile.ActionMappings.Remove(m);
            changed = true;
            _logger.LogInfo($"Removed {toRemove.Count} broken mapping(s) from profile '{profile.ProfileName}'.");
        }

        // ?? Migration 3: fix JSON-component CustomName in commands ????????????
        // Old pattern:  CustomName:'{"text":"{nickname}"}'
        // Correct:      CustomName:"{nickname}"
        foreach (var m in profile.ActionMappings)
        {
            if (string.IsNullOrEmpty(m.Command)) continue;
            var old1 = "CustomName:'{\"text\":\"{nickname}\"}'";
            var fix  = "CustomName:\"{nickname}\"";
            if (m.Command.Contains(old1, StringComparison.OrdinalIgnoreCase))
            {
                m.Command = m.Command.Replace(old1, fix, StringComparison.OrdinalIgnoreCase);
                changed = true;
            }
        }

        // ?? Migration 4: recover GiftMappings from old profile format ?????????
        if (profile.ActionMappings.Count == 0 && rawJson.Contains("\"GiftMappings\""))
        {
            try
            {
                using var doc = JsonDocument.Parse(rawJson);
                if (doc.RootElement.TryGetProperty("GiftMappings", out var giftMappings))
                {
                    foreach (var prop in giftMappings.EnumerateObject())
                    {
                        profile.ActionMappings.Add(new ActionMapping
                        {
                            TriggerType = ActionTriggerType.Gift,
                            TriggerKey  = prop.Name,
                            Command     = prop.Value.GetString() ?? string.Empty
                        });
                    }
                    changed = true;
                    _logger.LogInfo($"Migrated legacy GiftMappings in profile '{profile.ProfileName}'.");
                }
            }
            catch { /* ignore migration errors */ }
        }

        return changed;
    }

    public void Save(Profile profile)
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(profile, options);
            var filePath = Path.Combine(_profileDirectory, $"{profile.Id}.json");
            File.WriteAllText(filePath, json);
            _logger.LogInfo($"Profile '{profile.ProfileName}' saved.");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to save profile: {ex.Message}");
        }
    }

    public void Delete(Profile profile)
    {
        try
        {
            var filePath = Path.Combine(_profileDirectory, $"{profile.Id}.json");
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                _logger.LogInfo($"Profile '{profile.ProfileName}' deleted.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to delete profile: {ex.Message}");
        }
    }

    public string? LoadLastSelectedProfileId()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                return settings?.LastSelectedProfileId;
            }
        }
        catch { }
        return null;
    }

    public void SaveLastSelectedProfileId(string? profileId)
    {
        try
        {
            var settings = new AppSettings { LastSelectedProfileId = profileId ?? string.Empty };
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(settings, options);
            File.WriteAllText(_settingsFilePath, json);
        }
        catch { }
    }

    public List<SavedCommand> LoadSavedCommands()
    {
        try
        {
            if (File.Exists(_savedCommandsFilePath))
            {
                var json = File.ReadAllText(_savedCommandsFilePath);
                var list = JsonSerializer.Deserialize<List<SavedCommand>>(json);
                if (list != null) return list;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to load saved commands: {ex.Message}");
        }
        return new List<SavedCommand>();
    }

    public void SaveSavedCommands(IEnumerable<SavedCommand> commands)
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(commands, options);
            File.WriteAllText(_savedCommandsFilePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to save commands: {ex.Message}");
        }
    }

    private class AppSettings
    {
        public string LastSelectedProfileId { get; set; } = string.Empty;
    }
}
