using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ArcanePlayConnect.Core.Models;

namespace ArcanePlayConnect.Services;

/// <summary>
/// Controls how many active creatures a single viewer can have at once.
/// </summary>
public enum SummonLimitMode
{
    /// <summary>Each viewer can only have one active creature at a time.</summary>
    OnePerPlayer,

    /// <summary>Viewers can summon any number of creatures without restriction.</summary>
    Unlimited
}

/// <summary>
/// Tracks summoned creatures in the Minecraft world via RCON.
/// Uses the ArcanePlayConnect PaperMC plugin for exact combat stats
/// (damage dealt, kills, killed-by, HP) queried via the /apcstats command.
/// Supports one-creature-per-viewer or unlimited summon modes.
/// </summary>
public class CreatureTrackerService
{
    private readonly RconService _rcon;
    private readonly LoggingService _logger;

    private readonly ConcurrentDictionary<string, SummonedCreature> _activeCreatures = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SummonedCreature> _allCreatures = new();
    private readonly object _allLock = new();

    /// <summary>Stores accumulated buff stats per viewer username, persisted across respawns until session reset.</summary>
    private readonly ConcurrentDictionary<string, ViewerBuffStats> _viewerBuffStats = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _pollCts;

    /// <summary>Fired on the UI thread whenever creature data is updated.</summary>
    public event Action? CreaturesUpdated;

    /// <summary>Fired when a creature dies. Provides the dead creature for respawn logic.</summary>
    public event Action<SummonedCreature>? CreatureDied;

    /// <summary>Polling interval in seconds.</summary>
    public int PollIntervalSeconds { get; set; } = 3;

    /// <summary>Controls whether viewers can summon one creature or unlimited creatures.</summary>
    public SummonLimitMode SummonLimit { get; set; } = SummonLimitMode.OnePerPlayer;

    // ?? Auto-Respawn settings ??
    /// <summary>When true, dead creatures are automatically respawned.</summary>
    public bool AutoRespawnEnabled { get; set; }

    /// <summary>Delay in seconds before respawning a dead creature.</summary>
    public int AutoRespawnDelaySeconds { get; set; } = 5;

    /// <summary>CommandButton ID to use when respawning a follower's creature.</summary>
    public string AutoRespawnFollowerButtonId { get; set; } = string.Empty;

    /// <summary>CommandButton ID to use when respawning a non-follower's creature.</summary>
    public string AutoRespawnNonFollowerButtonId { get; set; } = string.Empty;

    /// <summary>Tag applied to ALL viewer-summoned creatures for easy bulk targeting.</summary>
    public const string ViewerTag = "viewer";
    public const string TagPrefix = "apc_";

    /// <summary>Duration in seconds for HP/attack buff effects.</summary>
    private const int EffectDuration = 999999;

    public CreatureTrackerService(RconService rcon, LoggingService logger)
    {
        _rcon = rcon;
        _logger = logger;
    }

    /// <summary>Returns a snapshot of all currently active (alive) creatures.</summary>
    public List<SummonedCreature> GetActiveCreatures()
    {
        return _activeCreatures.Values
            .Where(c => c.IsAlive)
            .OrderByDescending(c => c.DamageDealt)
            .ThenByDescending(c => c.CurrentHealth)
            .ToList();
    }

    /// <summary>Returns the full leaderboard (alive + dead) for this session, aggregated by owner.</summary>
    public List<AggregatedLeaderboardEntry> GetLeaderboard()
    {
        lock (_allLock)
        {
            // Group all non-boss creatures by owner username and aggregate scores
            var grouped = _allCreatures
                .Where(c => !c.IsBoss)
                .GroupBy(c => c.OwnerUsername, StringComparer.OrdinalIgnoreCase)
                .Select(g => new AggregatedLeaderboardEntry
                {
                    OwnerNickname = g.First().OwnerNickname,
                    OwnerUsername = g.Key,
                    OwnerProfilePictureUrl = g.Select(c => c.OwnerProfilePictureUrl)
                                              .FirstOrDefault(url => !string.IsNullOrEmpty(url)) ?? string.Empty,
                    TotalDamageDealt = g.Sum(c => c.DamageDealt),
                    TotalKills = g.Sum(c => c.KillCount),
                    CreatureCount = g.Count(),
                    HasAlive = g.Any(c => c.IsAlive),
                    BestSurvivalTime = g.Max(c => c.SurvivalTime),
                    LastEntityDisplayName = g.OrderByDescending(c => c.SummonedAt).First().EntityDisplayName,
                    KilledBy = g.Where(c => !c.IsAlive && !string.IsNullOrWhiteSpace(c.KilledBy))
                                .Select(c => c.KilledBy).LastOrDefault() ?? string.Empty,
                })
                .OrderByDescending(e => e.TotalDamageDealt)
                .ThenByDescending(e => e.BestSurvivalTime)
                .ThenByDescending(e => e.HasAlive)
                .ToList();

            for (int i = 0; i < grouped.Count; i++)
                grouped[i].Rank = i + 1;

            return grouped;
        }
    }

    /// <summary>Checks whether a viewer already has an active creature.</summary>
    public bool HasActiveCreature(string ownerUsername)
    {
        // In OnePerPlayer mode the key is the username directly
        if (SummonLimit == SummonLimitMode.OnePerPlayer)
            return _activeCreatures.TryGetValue(ownerUsername, out var c) && c.IsAlive;

        // In Unlimited mode creatures are keyed by tracking ID - scan values
        return _activeCreatures.Values.Any(c =>
            c.IsAlive && !c.IsBoss &&
            string.Equals(c.OwnerUsername, ownerUsername, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets the active creature for a viewer (non-boss), or null if none exists.
    /// In unlimited mode, returns the most recently summoned alive creature.
    /// </summary>
    public SummonedCreature? GetActiveCreature(string ownerUsername)
    {
        if (SummonLimit == SummonLimitMode.OnePerPlayer)
            return _activeCreatures.TryGetValue(ownerUsername, out var c) && c.IsAlive ? c : null;

        // In Unlimited mode, return the most recently summoned alive creature for this viewer
        return _activeCreatures.Values
            .Where(c => c.IsAlive && !c.IsBoss &&
                        string.Equals(c.OwnerUsername, ownerUsername, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.SummonedAt)
            .FirstOrDefault();
    }

    /// <summary>
    /// Gets all active (alive, non-boss) creatures for a viewer.
    /// </summary>
    public List<SummonedCreature> GetActiveCreatures(string ownerUsername)
    {
        return _activeCreatures.Values
            .Where(c => c.IsAlive && !c.IsBoss &&
                        string.Equals(c.OwnerUsername, ownerUsername, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Applies a heal and/or damage buff to a viewer's active creature.
    /// Returns true if any buff was applied.
    /// </summary>
    public async Task<bool> BuffCreatureAsync(string ownerUsername, float healAmount, float damageAmount)
    {
        // Sanitize NaN values
        if (float.IsNaN(healAmount)) healAmount = 0;
        if (float.IsNaN(damageAmount)) damageAmount = 0;

        var creature = GetActiveCreature(ownerUsername);
        if (creature == null)
        {
            _logger.LogInfo($"[Arena] No active creature for {ownerUsername} to buff.", LogCategory.System);
            return false;
        }

        if (!_rcon.IsConnected)
        {
            _logger.LogWarning("RCON not connected. Cannot buff creature.", LogCategory.System);
            return false;
        }

        var tag = creature.TrackingId;
        var applied = false;

        // Apply healing via data modify to set Health directly
        if (healAmount > 0)
        {
            var currentHpResp = await _rcon.SendCommand(
                $"data get entity @e[tag={tag},limit=1] Health");
            var currentHp = ParseFloatFromDataResponse(currentHpResp, creature.CurrentHealth);

            var newHp = Math.Min(currentHp + healAmount, creature.MaxHealth);
            var setHpCmd = $"data modify entity @e[tag={tag},limit=1] Health set value {newHp:F1}f";
            await _rcon.SendCommand(setHpCmd);

            creature.CurrentHealth = newHp;
            creature.AccumulatedBuffHeal += healAmount;
            applied = true;
            _logger.LogInfo($"[Arena] Healed {creature.OwnerNickname}'s {creature.EntityDisplayName} +{healAmount:F0} HP (now {newHp:F0}/{creature.MaxHealth:F0})", LogCategory.System);
        }

        // Apply damage buff via strength effect
        if (damageAmount > 0)
        {
            // Accumulate total damage buff for this creature
            creature.AccumulatedBuffDamage += damageAmount;

            // Re-apply the total accumulated damage as a single strength level
            var totalDmg = creature.AccumulatedBuffDamage;
            var amplifier = (int)Math.Max(0, Math.Ceiling(totalDmg / 3.0) - 1);
            await _rcon.SendCommand(
                $"effect give @e[tag={tag},limit=1] minecraft:strength {EffectDuration} {amplifier} true");
            applied = true;
            _logger.LogInfo($"[Arena] Buffed {creature.OwnerNickname}'s {creature.EntityDisplayName} +{damageAmount:F0} ATK (total buff={totalDmg:F0}, strength amp={amplifier})", LogCategory.System);
        }

        // Persist buff stats for this viewer (survives respawns)
        if (applied)
        {
            _viewerBuffStats.AddOrUpdate(ownerUsername,
                _ => new ViewerBuffStats { AccumulatedHeal = creature.AccumulatedBuffHeal, AccumulatedDamage = creature.AccumulatedBuffDamage },
                (_, existing) => { existing.AccumulatedHeal = creature.AccumulatedBuffHeal; existing.AccumulatedDamage = creature.AccumulatedBuffDamage; return existing; });

            CreaturesUpdated?.Invoke();
        }

        return applied;
    }

    /// <summary>
    /// Gets the accumulated buff stats for a viewer. Used to carry buffs across respawns.
    /// </summary>
    public ViewerBuffStats GetViewerBuffStats(string ownerUsername)
    {
        return _viewerBuffStats.TryGetValue(ownerUsername, out var stats) ? stats : new ViewerBuffStats();
    }

    /// <summary>
    /// Silently removes a viewer's active creature from tracking and kills it in-game.
    /// Does NOT record death stats or kill counts - used when upgrading a mob (e.g. Join ? Follow).
    /// </summary>
    public void SilentlyRemoveCreature(string ownerUsername)
    {
        // In OnePerPlayer mode, the key is the username
        if (SummonLimit == SummonLimitMode.OnePerPlayer)
        {
            if (!_activeCreatures.TryRemove(ownerUsername, out var creature))
                return;

            if (_rcon.IsConnected)
                _ = _rcon.SendCommand($"kill @e[tag={creature.TrackingId}]");

            lock (_allLock) { _allCreatures.Remove(creature); }

            _logger.LogInfo($"[Arena] Silently removed {creature.OwnerNickname}'s {creature.EntityDisplayName} (upgrade).", LogCategory.System);
            CreaturesUpdated?.Invoke();
            return;
        }

        // In Unlimited mode, remove ALL active creatures for this viewer
        var toRemove = _activeCreatures
            .Where(kvp => !kvp.Value.IsBoss && kvp.Value.IsAlive &&
                          string.Equals(kvp.Value.OwnerUsername, ownerUsername, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in toRemove)
        {
            if (_activeCreatures.TryRemove(key, out var c))
            {
                if (_rcon.IsConnected)
                    _ = _rcon.SendCommand($"kill @e[tag={c.TrackingId}]");

                lock (_allLock) { _allCreatures.Remove(c); }

                _logger.LogInfo($"[Arena] Silently removed {c.OwnerNickname}'s {c.EntityDisplayName} (upgrade).", LogCategory.System);
            }
        }

        if (toRemove.Count > 0)
            CreaturesUpdated?.Invoke();
    }

    /// <summary>
    /// Summons a creature for a viewer. Returns null if blocked (already has one alive, RCON down, etc.).
    /// Supports custom HP via health_boost effect and attack damage via strength effect.
    /// </summary>
    public async Task<SummonedCreature?> SummonCreatureAsync(
        string ownerNickname,
        string ownerUsername,
        string entityType,
        string position,
        string extraNbt = "",
        float customHealth = 0,
        float customAttackDamage = 0,
        bool isBoss = false,
        string ownerProfilePictureUrl = "",
        string bossName = "")
    {
        if (!_rcon.IsConnected)
        {
            _logger.LogWarning("RCON not connected. Cannot summon creature.", LogCategory.System);
            return null;
        }

        // Sanitize NaN values from NumberBox (they come through as float.NaN when empty)
        if (float.IsNaN(customHealth)) customHealth = 0;
        if (float.IsNaN(customAttackDamage)) customAttackDamage = 0;

        // Enforce one-creature-per-person when in OnePerPlayer mode (bosses always bypass)
        if (!isBoss && SummonLimit == SummonLimitMode.OnePerPlayer && HasActiveCreature(ownerUsername))
        {
            _logger.LogInfo($"[Arena] {ownerNickname}'s creature is still alive. Summon blocked (OnePerPlayer mode).", LogCategory.System);
            return null;
        }

        var creature = new SummonedCreature
        {
            OwnerNickname = ownerNickname,
            OwnerUsername = ownerUsername,
            OwnerProfilePictureUrl = ownerProfilePictureUrl,
            EntityType = entityType,
            EntityDisplayName = FormatEntityName(entityType),
            IsBoss = isBoss,
        };

        var tag = creature.TrackingId;

        // Build the display name: use boss name if provided, otherwise viewer nickname
        var safeNick = ownerNickname
            .Replace("\\", "")
            .Replace("\"", "")
            .Replace("'", "");
        var displayName = isBoss && !string.IsNullOrWhiteSpace(bossName)
            ? bossName.Replace("\\", "").Replace("\"", "").Replace("'", "")
            : safeNick;

        // ?? Step 1: Build the summon NBT ??
        var nbtParts = new List<string>
        {
            "CustomName:'\"" + displayName + "\"'",
            "CustomNameVisible:1b",
            "PersistenceRequired:1b",
            $"Tags:[\"{tag}\",\"{ViewerTag}\"]",
            "Invul:0"
        };

        // Custom HP and ATK are applied AFTER spawning via attribute/data merge
        // commands. Minecraft clamps Health in the summon NBT to the entity's
        // default max_health (e.g. Wither caps at 300), so we must set
        // generic.max_health first, then set Health to the desired value.
        if (customHealth > 0)
        {
            creature.MaxHealth = customHealth;
            creature.CurrentHealth = customHealth;
            creature.CustomHealth = customHealth;
        }

        if (customAttackDamage > 0)
        {
            creature.CustomAttackDamage = customAttackDamage;
        }

        // Merge user-provided extra NBT
        if (!string.IsNullOrWhiteSpace(extraNbt))
        {
            var trimmed = extraNbt.Trim();
            if (trimmed.StartsWith('{')) trimmed = trimmed[1..];
            if (trimmed.EndsWith('}')) trimmed = trimmed[..^1];
            trimmed = trimmed.Trim();
            trimmed = Regex.Replace(trimmed, @"Tags:\[.*?\],?", "", RegexOptions.IgnoreCase).Trim();
            trimmed = Regex.Replace(trimmed, @"CustomName:.*?(?=,\w|$)", "", RegexOptions.IgnoreCase).Trim();
            trimmed = Regex.Replace(trimmed, @"CustomNameVisible:\d+b,?", "", RegexOptions.IgnoreCase).Trim();
            trimmed = Regex.Replace(trimmed, @"Attributes:\[.*?\],?", "", RegexOptions.IgnoreCase).Trim();
            trimmed = Regex.Replace(trimmed, @"Health:\d+\.?\d*f?,?", "", RegexOptions.IgnoreCase).Trim();
            trimmed = Regex.Replace(trimmed, @"Invul:\d+,?", "", RegexOptions.IgnoreCase).Trim();
            trimmed = trimmed.Trim(',').Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
                nbtParts.Add(trimmed);
        }

        var nbt = "{" + string.Join(",", nbtParts) + "}";
        var cmd = $"summon {entityType} {position} {nbt}";

        _logger.LogInfo($"[Arena] Summon cmd: {cmd}", LogCategory.System);
        var response = await _rcon.SendCommand(cmd);
        _logger.LogInfo($"[Arena] Summon response: '{response}'", LogCategory.System);

        // Check for failure - only fail if the response explicitly indicates an error
        if (!string.IsNullOrEmpty(response))
        {
            var lower = response.ToLowerInvariant();
            if (lower.Contains("invalid") || lower.Contains("incorrect argument") ||
                lower.Contains("expected") || lower.Contains("could not") ||
                lower.Contains("unable") || lower.Contains("unknown command"))
            {
                _logger.LogWarning($"[Arena] Summon failed for {ownerNickname}: {response}", LogCategory.System);
                return null;
            }

            // If we got a response that clearly isn't from the summon command
            // (e.g. a stale keep-alive response), log a warning but continue -
            // the entity may have spawned and we'll verify via data query below.
            if (!lower.Contains("summoned") && !string.IsNullOrWhiteSpace(response))
            {
                _logger.LogWarning($"[Arena] Unexpected summon response for {ownerNickname} (may be stale): {response}", LogCategory.System);
            }
        }

        // Small delay to let entity spawn
        await Task.Delay(300);

        // ?? Step 2: Verify the entity actually exists and read back health ??
        var verifyResp = await _rcon.SendCommand($"data get entity @e[tag={tag},limit=1] Health");
        if (IsEntityNotFoundResponse(verifyResp))
        {
            _logger.LogWarning($"[Arena] Entity not found after summon for {ownerNickname}. Summon may have failed.", LogCategory.System);
            return null;
        }

        // Apply custom attributes AFTER spawn via data merge. Minecraft clamps
        // Health to the mob's default max_health at summon time (e.g. Wither = 300),
        // so we set Attributes + Health post-spawn via data merge.
        //
        // Minecraft 1.21+ changed the Attributes NBT format:
        //   Pre-1.21:  {Name:"generic.max_health", Base:500.0}
        //   1.21+:     {id:"minecraft:max_health", Base:500.0}
        // We try the modern format first, verify it worked, then fall back to legacy.
        if (customHealth > 0 || customAttackDamage > 0)
        {
            await ApplyCustomAttributesAsync(tag, customHealth, customAttackDamage);

            if (customHealth > 0)
            {
                creature.MaxHealth = customHealth;
                creature.CurrentHealth = customHealth;
            }
        }
        else
        {
            creature.MaxHealth = ParseFloatFromDataResponse(verifyResp, 20f);
            creature.CurrentHealth = creature.MaxHealth;
        }

        if (customHealth > 0)
            _logger.LogInfo($"[Arena] Spawned with HP: {customHealth:F0}", LogCategory.System);
        if (customAttackDamage > 0)
            _logger.LogInfo($"[Arena] Spawned with ATK: {customAttackDamage:F0}", LogCategory.System);

        creature.PreviousHealth = creature.CurrentHealth;

        // For bosses or unlimited mode, use tracking ID as key (allows multiple per viewer)
        // For OnePerPlayer normal creatures, use ownerUsername (enforces one-per-viewer)
        var activeKey = (isBoss || SummonLimit == SummonLimitMode.Unlimited)
            ? creature.TrackingId
            : ownerUsername;
        _activeCreatures[activeKey] = creature;
        lock (_allLock)
        {
            _allCreatures.Add(creature);
        }

        var bossLabel = isBoss ? " [BOSS]" : "";
        _logger.LogEvent($"[Arena] {ownerNickname} summoned {creature.EntityDisplayName}{bossLabel}! " +
            $"(HP: {creature.MaxHealth:F0}, Tag: {tag})", LogCategory.System);
        CreaturesUpdated?.Invoke();

        return creature;
    }

    /// <summary>Starts the background polling loop that queries the plugin for stats.</summary>
    public void StartPolling()
    {
        StopPolling();
        _pollCts = new CancellationTokenSource();
        _ = PollLoopAsync(_pollCts.Token);
        _logger.LogInfo("[Arena] Creature tracking started (plugin mode).", LogCategory.System);
    }

    /// <summary>Stops background polling.</summary>
    public void StopPolling()
    {
        if (_pollCts != null)
        {
            _pollCts.Cancel();
            _pollCts.Dispose();
            _pollCts = null;
        }
    }

    public bool IsPolling => _pollCts != null && !_pollCts.IsCancellationRequested;

    /// <summary>Clears all tracked creatures and resets the session.</summary>
    public void ResetSession()
    {
        StopPolling();
        _activeCreatures.Clear();
        _viewerBuffStats.Clear();
        lock (_allLock) { _allCreatures.Clear(); }
        // Also reset plugin-side stats
        if (_rcon.IsConnected)
            _ = _rcon.SendCommand("apcreset");
        CreaturesUpdated?.Invoke();
        _logger.LogInfo("[Arena] Session reset. All creature data and buff stats cleared.", LogCategory.System);
    }

    /// <summary>Kills all non-player entities using: kill @e[type=!minecraft:player]</summary>
    public async Task KillAllCreaturesAsync()
    {
        if (!_rcon.IsConnected) return;

        await _rcon.SendCommand("kill @e[type=!minecraft:player]");

        foreach (var creature in _activeCreatures.Values.Where(c => c.IsAlive))
        {
            creature.IsAlive = false;
            creature.CurrentHealth = 0;
            creature.DiedAt = DateTime.Now;
        }

        CreaturesUpdated?.Invoke();
        _logger.LogInfo("[Arena] All non-player entities killed.", LogCategory.System);
    }

    // ?? Private: Polling ????????????????????????????????????????????????????

    private async Task PollLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, PollIntervalSeconds)), token);

                if (!_rcon.IsConnected) continue;

                var aliveCreatures = _activeCreatures.Values.Where(c => c.IsAlive).ToList();
                if (aliveCreatures.Count == 0) continue;

                try
                {
                    await PollViaPluginAsync(aliveCreatures);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"[Arena] Plugin poll failed, falling back to data query: {ex.Message}", LogCategory.System);
                    await PollViaDataQueryAsync(aliveCreatures, token);
                }

                CreaturesUpdated?.Invoke();
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex)
        {
            _logger.LogError($"[Arena] Polling error: {ex.Message}");
        }
    }

    /// <summary>
    /// Primary polling: query the ArcanePlayConnect PaperMC plugin via /apcstats RCON command.
    /// Response format: tag|damageDealt|kills|killedBy|dead|currentHp|maxHp;...
    /// </summary>
    private async Task PollViaPluginAsync(List<SummonedCreature> aliveCreatures)
    {
        var response = await _rcon.SendCommand("apcstats");

        if (string.IsNullOrWhiteSpace(response) || response == "EMPTY")
            return;

        // Parse the plugin response - semicolon-separated creature records
        var records = response.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var statsByTag = new Dictionary<string, PluginCreatureData>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in records)
        {
            var data = ParsePluginRecord(record);
            if (data != null)
                statsByTag[data.Tag] = data;
        }

        // Update each tracked creature from plugin data
        foreach (var creature in aliveCreatures)
        {
            if (!statsByTag.TryGetValue(creature.TrackingId, out var data))
            {
                // Plugin doesn't know about this creature - it might not have been in combat yet.
                // Check if entity still exists via a lightweight data query
                var healthResp = await _rcon.SendCommand(
                    $"data get entity @e[tag={creature.TrackingId},limit=1] Health");
                if (IsEntityNotFoundResponse(healthResp))
                {
                    MarkDead(creature, "unknown");
                }
                else
                {
                    var hp = ParseFloatFromDataResponse(healthResp, creature.CurrentHealth);
                    creature.CurrentHealth = hp;
                    if (hp <= 0)
                        MarkDead(creature, "unknown");
                }
                continue;
            }

            // Apply plugin stats
            creature.DamageDealt = (int)Math.Round(data.DamageDealt);
            creature.KillCount = data.Kills;
            creature.CurrentHealth = (float)data.CurrentHp;

            // Update max HP if the plugin reports a valid value
            if (data.MaxHp > 0 && data.MaxHp > creature.MaxHealth * 0.5f)
                creature.MaxHealth = (float)data.MaxHp;

            creature.DamageTaken = (int)Math.Max(0, creature.MaxHealth - creature.CurrentHealth);

            if (data.IsDead || data.CurrentHp <= 0)
            {
                MarkDead(creature, data.KilledBy);
            }
        }
    }

    /// <summary>
    /// Fallback polling: used when the plugin is not installed.
    /// Queries HP via "data get entity" - no damage/kill tracking.
    /// </summary>
    private async Task PollViaDataQueryAsync(List<SummonedCreature> aliveCreatures, CancellationToken token)
    {
        foreach (var creature in aliveCreatures)
        {
            if (token.IsCancellationRequested) break;

            try
            {
                var healthResp = await _rcon.SendCommand(
                    $"data get entity @e[tag={creature.TrackingId},limit=1] Health");

                if (IsEntityNotFoundResponse(healthResp))
                {
                    MarkDead(creature, "unknown");
                    continue;
                }

                var hp = ParseFloatFromDataResponse(healthResp, creature.CurrentHealth);
                creature.CurrentHealth = hp;
                creature.DamageTaken = (int)Math.Max(0, creature.MaxHealth - hp);

                if (hp <= 0)
                    MarkDead(creature, "unknown");
            }
            catch
            {
                // Ignore per-creature errors
            }
        }
    }

    // ?? Private: Plugin response parsing ????????????????????????????????????

    /// <summary>
    /// Parsed data from a single plugin record: tag|damageDealt|kills|killedBy|dead|currentHp|maxHp
    /// </summary>
    private sealed class PluginCreatureData
    {
        public string Tag { get; init; } = "";
        public double DamageDealt { get; init; }
        public int Kills { get; init; }
        public string KilledBy { get; init; } = "none";
        public bool IsDead { get; init; }
        public double CurrentHp { get; init; }
        public double MaxHp { get; init; }
    }

    /// <summary>
    /// Parses a single pipe-delimited record from the plugin response.
    /// Format: tag|damageDealt|kills|killedBy|dead|currentHp|maxHp
    /// </summary>
    private static PluginCreatureData? ParsePluginRecord(string record)
    {
        var parts = record.Split('|');
        if (parts.Length < 7) return null;

        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var dmg))
            dmg = 0;
        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kills))
            kills = 0;
        if (!bool.TryParse(parts[4], out var dead))
            dead = false;
        if (!double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var currentHp))
            currentHp = 0;
        if (!double.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var maxHp))
            maxHp = 0;

        return new PluginCreatureData
        {
            Tag = parts[0],
            DamageDealt = dmg,
            Kills = kills,
            KilledBy = parts[3],
            IsDead = dead,
            CurrentHp = currentHp,
            MaxHp = maxHp
        };
    }

    /// <summary>
    /// Applies custom max_health and attack_damage attributes to an entity post-spawn.
    /// Uses targeted "data modify" to update the specific attribute entry in-place
    /// within the entity's existing Attributes array.
    ///
    /// Minecraft 1.21+ stores attributes as:
    ///   Attributes[{id:"minecraft:max_health"}].base
    /// Older versions use:
    ///   Attributes[{Name:"generic.max_health"}].Base
    ///
    /// We try the modern format first, verify it worked, then fall back to legacy.
    /// </summary>
    private async Task ApplyCustomAttributesAsync(string tag, float customHealth, float customAttackDamage)
    {
        var selector = $"@e[tag={tag},limit=1]";

        // -- Try modern format (MC 1.21+): id:"minecraft:max_health", lowercase "base" --
        if (customHealth > 0)
        {
            var hp = customHealth.ToString("F1", CultureInfo.InvariantCulture);
            await _rcon.SendCommand(
                $"data modify entity {selector} Attributes[{{id:\"minecraft:max_health\"}}].base set value {hp}");
        }
        if (customAttackDamage > 0)
        {
            var atk = customAttackDamage.ToString("F1", CultureInfo.InvariantCulture);
            await _rcon.SendCommand(
                $"data modify entity {selector} Attributes[{{id:\"minecraft:attack_damage\"}}].base set value {atk}");
        }

        // Set Health after max_health has (hopefully) been raised
        if (customHealth > 0)
        {
            var hp = customHealth.ToString("F1", CultureInfo.InvariantCulture);
            await _rcon.SendCommand(
                $"data modify entity {selector} Health set value {hp}f");

            // Verify if max_health actually took effect by reading Health back
            var checkResp = await _rcon.SendCommand($"data get entity {selector} Health");
            var actualHp = ParseFloatFromDataResponse(checkResp, 0f);

            // If Health is still clamped to default, the modern format didn't work
            if (actualHp < customHealth * 0.95f)
            {
                _logger.LogInfo($"[Arena] Modern attribute format did not apply (HP={actualHp:F0}), trying legacy format.", LogCategory.System);

                // -- Try legacy format (pre-1.21): Name:"generic.max_health", uppercase "Base" --
                await _rcon.SendCommand(
                    $"data modify entity {selector} Attributes[{{Name:\"generic.max_health\"}}].Base set value {hp}");
                if (customAttackDamage > 0)
                {
                    var atk = customAttackDamage.ToString("F1", CultureInfo.InvariantCulture);
                    await _rcon.SendCommand(
                        $"data modify entity {selector} Attributes[{{Name:\"generic.attack_damage\"}}].Base set value {atk}");
                }

                // Set Health again after legacy max_health change
                await _rcon.SendCommand(
                    $"data modify entity {selector} Health set value {hp}f");
            }
        }
    }

    private static bool IsEntityNotFoundResponse(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return true;
        if (response.Contains("No entity was found", StringComparison.OrdinalIgnoreCase)) return true;
        if (response.Contains("no entity", StringComparison.OrdinalIgnoreCase)) return true;
        if (response.Contains("Couldn't", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private void MarkDead(SummonedCreature creature, string killedBy)
    {
        if (!creature.IsAlive) return;
        creature.IsAlive = false;
        creature.CurrentHealth = 0;
        creature.DamageTaken = (int)creature.MaxHealth;
        creature.DiedAt = DateTime.Now;

        // Persist buff stats from the dying creature into the viewer's buff bank
        if (!creature.IsBoss && (creature.AccumulatedBuffHeal > 0 || creature.AccumulatedBuffDamage > 0))
        {
            _viewerBuffStats.AddOrUpdate(creature.OwnerUsername,
                _ => new ViewerBuffStats { AccumulatedHeal = creature.AccumulatedBuffHeal, AccumulatedDamage = creature.AccumulatedBuffDamage },
                (_, existing) =>
                {
                    // Take the max of existing and creature values (buffs stack across the session)
                    existing.AccumulatedHeal = Math.Max(existing.AccumulatedHeal, creature.AccumulatedBuffHeal);
                    existing.AccumulatedDamage = Math.Max(existing.AccumulatedDamage, creature.AccumulatedBuffDamage);
                    return existing;
                });
        }

        var killerInfo = killedBy == "none" || string.IsNullOrWhiteSpace(killedBy)
            ? "" : $" by {killedBy}";

        _logger.LogEvent($"[Arena] {creature.OwnerNickname}'s {creature.EntityDisplayName} has died{killerInfo}! " +
            $"(Survived {creature.SurvivalTime:mm\\:ss}, Damage: {creature.DamageDealt}, Kills: {creature.KillCount})", LogCategory.System);

        // Fire the death event for auto-respawn logic
        CreatureDied?.Invoke(creature);
    }

    /// <summary>
    /// Parses a float from RCON "data get entity" responses.
    /// </summary>
    private static float ParseFloatFromDataResponse(string response, float fallback)
    {
        if (string.IsNullOrWhiteSpace(response)) return fallback;

        var dataMatch = Regex.Match(response, @"data:\s*(-?\d+\.?\d*)\s*f?", RegexOptions.IgnoreCase);
        if (dataMatch.Success && float.TryParse(dataMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v1))
            return v1;

        var matches = Regex.Matches(response, @"(-?\d+\.?\d*)\s*f?\b");
        if (matches.Count > 0)
        {
            var lastMatch = matches[^1];
            if (float.TryParse(lastMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v2))
                return v2;
        }

        return fallback;
    }

    private static string FormatEntityName(string entityType)
    {
        var name = entityType;
        if (name.StartsWith("minecraft:"))
            name = name["minecraft:".Length..];
        return string.Join(' ', name.Split('_')
            .Select(w => w.Length > 0 ? char.ToUpper(w[0]) + w[1..] : w));
    }
}

/// <summary>
/// Stores accumulated buff stats for a viewer across respawns within a session.
/// </summary>
public class ViewerBuffStats
{
    /// <summary>Total accumulated heal amount from buff effects.</summary>
    public float AccumulatedHeal { get; set; }

    /// <summary>Total accumulated damage buff amount from buff effects.</summary>
    public float AccumulatedDamage { get; set; }
}
