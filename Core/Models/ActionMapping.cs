namespace ArcanePlayConnect.Core.Models;

public enum ActionTriggerType
{
    Gift,
    Follow,
    Chat,
    Like,
    Join,
    Share,
    Subscribe
}

public class ActionMapping
{
    public ActionTriggerType TriggerType { get; set; } = ActionTriggerType.Gift;
    public string TriggerKey { get; set; } = string.Empty;

    /// <summary>Raw RCON command template. Used when TargetButtonId is null/empty.</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>When set, this mapping triggers the CommandButton with this ID instead of sending Command.</summary>
    public string TargetButtonId { get; set; } = string.Empty;

    /// <summary>
    /// Follow-only option. When true and the viewer already has an active creature
    /// from a Join event, that creature is silently killed (without counting stats)
    /// before executing the Follow action. This lets the Follow mapping "upgrade"
    /// the viewer's mob.
    /// </summary>
    public bool ReplaceJoinMob { get; set; }
}
