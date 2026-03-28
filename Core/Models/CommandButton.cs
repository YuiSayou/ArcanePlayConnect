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

    // ?? HealthCheck options ??
    /// <summary>When true the command sequence runs repeatedly at IntervalSeconds.</summary>
    public bool RunContinuously { get; set; }

    /// <summary>Interval in seconds between repeated executions (only when RunContinuously is true).</summary>
    public int IntervalSeconds { get; set; } = 30;
}
