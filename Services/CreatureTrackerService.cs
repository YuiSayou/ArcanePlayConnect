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
/// Tracks summoned creatures in the Minecraft world via RCON.
/// Uses the ArcanePlayConnect PaperMC plugin for exact combat stats
/// (damage dealt, kills, killed-by, HP) queried via the /apcstats command.
/// Enforces a one-creature-per-viewer limit.
/// </summary>
public class CreatureTrackerService
{
    private readonly RconService _rcon;
    private readonly LoggingService _logger;

    private readonly ConcurrentDictionary<string, SummonedCreature> _activeCreatures = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SummonedCreature> _allCreatures = new();
    private readonly object _allLock = new();

    private CancellationTokenSource? _pollCts;

    /// <summary>Fired on the UI thread whenever creature data is updated.</summary>
    public event Action? CreaturesUpdated;

    /// <summary>Polling interval in seconds.</summary>
    public int PollIntervalSeconds { get; set; } = 3;

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
        return _activeCreatures.TryGetValue(ownerUsername, out var c) && c.IsAlive;
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

        // Enforce one-creature-per-person (bosses bypass this)
        if (!isBoss && HasActiveCreature(ownerUsername))
        {
            _logger.LogInfo($"[Arena] {ownerNickname}'s creature is still alive. Summon blocked.", LogCategory.System);
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

        // ?? Step 1: Summon with Tags, CustomName, and basic NBT ??
        var nbtParts = new List<string>
        {
            "CustomName:'\"" + displayName + "\"'",
            "CustomNameVisible:1b",
            "PersistenceRequired:1b",
            $"Tags:[\"{tag}\",\"{ViewerTag}\"]"
        };

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
            trimmed = trimmed.Trim(',').Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
                nbtParts.Add(trimmed);
        }

        var nbt = "{" + string.Join(",", nbtParts) + "}";
        var cmd = $"summon {entityType} {position} {nbt}";

        _logger.LogInfo($"[Arena] Summon cmd: {cmd}", LogCategory.System);
        var response = await _rcon.SendCommand(cmd);
        _logger.LogInfo($"[Arena] Summon response: '{response}'", LogCategory.System);

        // Check for failure — only fail if the response explicitly indicates an error
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
        }

        // Small delay to let entity spawn
        await Task.Delay(250);

        // ?? Step 2: Apply custom health via health_boost effect ??
        if (customHealth > 0)
        {
            // Query the entity's base health
            var baseHealthResp = await _rcon.SendCommand(
                $"data get entity @e[tag={tag},limit=1] Health");
            var baseMaxHealth = ParseFloatFromDataResponse(baseHealthResp, 20f);

            if (customHealth > baseMaxHealth)
            {
                var extraHp = customHealth - baseMaxHealth;
                var amplifier = (int)Math.Max(0, Math.Ceiling(extraHp / 4.0) - 1);
                await _rcon.SendCommand(
                    $"effect give @e[tag={tag},limit=1] minecraft:health_boost {EffectDuration} {amplifier} true");

                await Task.Delay(150);
                await _rcon.SendCommand(
                    $"effect give @e[tag={tag},limit=1] minecraft:instant_health 1 100 true");

                await Task.Delay(150);
                var actualHealthResp = await _rcon.SendCommand(
                    $"data get entity @e[tag={tag},limit=1] Health");
                var actualHealth = ParseFloatFromDataResponse(actualHealthResp, customHealth);
                creature.MaxHealth = actualHealth;
                creature.CurrentHealth = actualHealth;
            }
            else
            {
                creature.MaxHealth = baseMaxHealth;
                creature.CurrentHealth = baseMaxHealth;
            }

            _logger.LogInfo($"[Arena] Applied HP buff: target={customHealth:F0}, actual={creature.MaxHealth:F0}", LogCategory.System);
        }
        else
        {
            var healthResp = await _rcon.SendCommand($"data get entity @e[tag={tag},limit=1] Health");
            creature.MaxHealth = ParseFloatFromDataResponse(healthResp, 20f);
            creature.CurrentHealth = creature.MaxHealth;
        }

        // ?? Step 4: Apply custom attack damage via strength effect ??
        if (customAttackDamage > 0)
        {
            var baseAtkResp = await _rcon.SendCommand(
                $"data get entity @e[tag={tag},limit=1] Attributes[{{Name:\"minecraft:generic.attack_damage\"}}].Base");
            var baseAtk = ParseFloatFromDataResponse(baseAtkResp, 2f);

            if (customAttackDamage > baseAtk)
            {
                var extraDmg = customAttackDamage - baseAtk;
                var amplifier = (int)Math.Max(0, Math.Ceiling(extraDmg / 3.0) - 1);
                await _rcon.SendCommand(
                    $"effect give @e[tag={tag},limit=1] minecraft:strength {EffectDuration} {amplifier} true");
                _logger.LogInfo($"[Arena] Applied strength buff: target={customAttackDamage:F0}, base={baseAtk:F0}, amp={amplifier}", LogCategory.System);
            }
            else
            {
                _logger.LogInfo($"[Arena] Target ATK {customAttackDamage:F0} <= base {baseAtk:F0}, no buff needed.", LogCategory.System);
            }
        }

        creature.PreviousHealth = creature.CurrentHealth;

        // For bosses, use tracking ID as key (allows multiple per viewer)
        // For normal creatures, use ownerUsername (enforces one-per-viewer)
        var activeKey = isBoss ? creature.TrackingId : ownerUsername;
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
        lock (_allLock) { _allCreatures.Clear(); }
        // Also reset plugin-side stats
        if (_rcon.IsConnected)
            _ = _rcon.SendCommand("apcreset");
        CreaturesUpdated?.Invoke();
        _logger.LogInfo("[Arena] Session reset. All creature data cleared.", LogCategory.System);
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

        // Parse the plugin response — semicolon-separated creature records
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
                // Plugin doesn't know about this creature — it might not have been in combat yet.
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

            // Update max HP if the plugin reports a higher value (effects applied)
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
    /// Queries HP via "data get entity" — no damage/kill tracking.
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

    // ?? Private: Helpers ????????????????????????????????????????????????????

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

        var killerInfo = killedBy == "none" || string.IsNullOrWhiteSpace(killedBy)
            ? "" : $" by {killedBy}";

        _logger.LogEvent($"[Arena] {creature.OwnerNickname}'s {creature.EntityDisplayName} has died{killerInfo}! " +
            $"(Survived {creature.SurvivalTime:mm\\:ss}, Damage: {creature.DamageDealt}, Kills: {creature.KillCount})", LogCategory.System);
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
