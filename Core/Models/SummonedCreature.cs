using System;

namespace ArcanePlayConnect.Core.Models;

/// <summary>
/// Represents a creature summoned by a viewer. Tracked in-game via entity tags.
/// Each viewer can only have one active creature at a time.
/// Combat stats are provided by the ArcanePlayConnect PaperMC plugin.
/// </summary>
public class SummonedCreature
{
    /// <summary>Unique tracking ID used as the entity tag (apc_XXXX).</summary>
    public string TrackingId { get; set; } = $"apc_{Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>TikTok display name of the person who triggered the summon.</summary>
    public string OwnerNickname { get; set; } = string.Empty;

    /// <summary>TikTok username (login) of the owner.</summary>
    public string OwnerUsername { get; set; } = string.Empty;

    /// <summary>Minecraft entity type, e.g. "minecraft:zombie".</summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>Short display name of the entity type (e.g. "Zombie").</summary>
    public string EntityDisplayName { get; set; } = string.Empty;

    /// <summary>Current health of the creature. Updated by polling.</summary>
    public float CurrentHealth { get; set; }

    /// <summary>Maximum health of the creature at spawn time.</summary>
    public float MaxHealth { get; set; } = 20f;

    /// <summary>HP from the previous poll cycle.</summary>
    public float PreviousHealth { get; set; }

    /// <summary>Total damage dealt by this creature (exact, from plugin).</summary>
    public int DamageDealt { get; set; }

    /// <summary>Total damage this creature has taken (MaxHealth − CurrentHealth).</summary>
    public int DamageTaken { get; set; }

    /// <summary>Number of entities this creature has killed.</summary>
    public int KillCount { get; set; }

    /// <summary>What killed this creature (apc_ tag, player:Name, mob type, or empty).</summary>
    public string KilledBy { get; set; } = string.Empty;

    /// <summary>Whether the creature is still alive in the world.</summary>
    public bool IsAlive { get; set; } = true;

    /// <summary>When the creature was summoned.</summary>
    public DateTime SummonedAt { get; set; } = DateTime.Now;

    /// <summary>When the creature died (null if still alive).</summary>
    public DateTime? DiedAt { get; set; }

    /// <summary>How long the creature survived.</summary>
    public TimeSpan SurvivalTime => (DiedAt ?? DateTime.Now) - SummonedAt;

    /// <summary>Ranking position (1 = top). Computed by the tracker.</summary>
    public int Rank { get; set; }

    /// <summary>Custom HP set by the builder. 0 = use entity default.</summary>
    public float CustomHealth { get; set; }

    /// <summary>Custom attack damage set by the builder. 0 = use entity default.</summary>
    public float CustomAttackDamage { get; set; }

    /// <summary>When true, this creature is a boss and can be summoned infinitely by the same viewer.</summary>
    public bool IsBoss { get; set; }
}

/// <summary>
/// Aggregated leaderboard entry — one per viewer, scores stacked across all their creatures.
/// </summary>
public class AggregatedLeaderboardEntry
{
    public string OwnerNickname { get; set; } = string.Empty;
    public string OwnerUsername { get; set; } = string.Empty;
    public int TotalDamageDealt { get; set; }
    public int TotalKills { get; set; }
    public int CreatureCount { get; set; }
    public bool HasAlive { get; set; }
    public TimeSpan BestSurvivalTime { get; set; }
    public string LastEntityDisplayName { get; set; } = string.Empty;
    public string KilledBy { get; set; } = string.Empty;
    public int Rank { get; set; }
}
