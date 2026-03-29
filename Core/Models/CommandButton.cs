using System;
using System.Collections.Generic;

namespace ArcanePlayConnect.Core.Models;

public enum CommandButtonType
{
    Summon,
    HealthCheck
}

/// <summary>
/// A user-created button that executes a sequence of RCON commands.
/// Stored in the profile JSON alongside ActionMappings.
/// </summary>
public class CommandButton
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "New Button";
    public CommandButtonType ButtonType { get; set; } = CommandButtonType.Summon;

    /// <summary>Ordered list of RCON command templates. Executed sequentially.</summary>
    public List<string> Commands { get; set; } = new();

    // ?? Summon options ??
    /// <summary>When true, {nickname} and {username} are substituted from the triggering event.</summary>
    public bool UseNickname { get; set; } = true;

    /// <summary>Entity type to summon, e.g. "minecraft:zombie".</summary>
    public string SummonEntityType { get; set; } = string.Empty;

    /// <summary>Position for the summon command (e.g. "~ ~ ~").</summary>
    public string SummonPosition { get; set; } = "~ ~ ~";

    /// <summary>Custom max health for the summoned creature. 0 = use entity default.</summary>
    public float SummonCustomHealth { get; set; }

    /// <summary>Custom attack damage for the summoned creature. 0 = use entity default.</summary>
    public float SummonCustomAttack { get; set; }

    /// <summary>When true, the creature is tracked in the Creature Arena with a viewer tag.</summary>
    public bool SummonTrackCreature { get; set; } = true;

    /// <summary>When true, this is a boss creature that can be summoned infinitely (no one-per-viewer limit).</summary>
    public bool SummonIsBoss { get; set; }

    /// <summary>Custom display name for boss creatures. Empty = use viewer nickname.</summary>
    public string SummonBossName { get; set; } = string.Empty;

    // ?? HealthCheck options ??
    /// <summary>When true the command sequence runs repeatedly at IntervalSeconds.</summary>
    public bool RunContinuously { get; set; }

    /// <summary>Interval in seconds between repeated executions (only when RunContinuously is true).</summary>
    public int IntervalSeconds { get; set; } = 30;
}
