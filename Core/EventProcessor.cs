using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ArcanePlayConnect.Core.Models;
using ArcanePlayConnect.Services;

namespace ArcanePlayConnect.Core;

public class EventProcessor
{
    private readonly RconService _rcon;
    private readonly LoggingService _logger;
    private readonly CommandButtonExecutor _buttonExecutor;
    private readonly CreatureTrackerService _creatureTracker;
    private readonly FollowerService _followerService;

    public EventProcessor(RconService rcon, LoggingService logger, CommandButtonExecutor buttonExecutor,
        CreatureTrackerService creatureTracker, FollowerService followerService)
    {
        _rcon = rcon;
        _logger = logger;
        _buttonExecutor = buttonExecutor;
        _creatureTracker = creatureTracker;
        _followerService = followerService;
    }

    public static string EscapeJson(string input) =>
        input.Replace("\\", "\\\\").Replace("\"", "\\\"");

    public static string BuildCommand(string template, string nickname, string username = "")
    {
        var safeNickname = nickname
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"");

        var safeUsername = string.IsNullOrWhiteSpace(username) ? safeNickname : username;

        var cmd = template
            .Replace("{nickname}", safeNickname)
            .Replace("{safe}",     safeNickname)
            .Replace("{username}", safeUsername);

        cmd = cmd.Replace("{{", "{").Replace("}}", "}");

        return cmd;
    }

    /// <summary>
    /// Checks whether a command is a summon command and extracts its components.
    /// </summary>
    public static bool TryParseSummonCommand(string command, out string entityType, out string position, out string extraNbt)
    {
        entityType = "";
        position = "";
        extraNbt = "";

        // Strip leading slash
        var cmd = command.TrimStart('/').Trim();

        // Handle "execute ... run summon ..." - extract from "run summon" onward
        var runIdx = cmd.IndexOf("run summon", StringComparison.OrdinalIgnoreCase);
        if (runIdx >= 0)
            cmd = cmd[(runIdx + 4)..].Trim(); // skip "run "

        if (!cmd.StartsWith("summon ", StringComparison.OrdinalIgnoreCase))
            return false;

        // Remove "summon "
        cmd = cmd[7..].Trim();

        // Extract entity type (first word)
        var spaceIdx = cmd.IndexOf(' ');
        if (spaceIdx < 0)
        {
            entityType = cmd;
            position = "~ ~ ~";
            return true;
        }

        entityType = cmd[..spaceIdx];
        var rest = cmd[(spaceIdx + 1)..].Trim();

        // Try to find position (3 coordinate tokens) followed by optional NBT
        var posMatch = Regex.Match(rest,
            @"^([~^]?-?\d*\.?\d*)\s+([~^]?-?\d*\.?\d*)\s+([~^]?-?\d*\.?\d*)(?:\s+(\{.*))?$");

        if (posMatch.Success)
        {
            position = $"{posMatch.Groups[1].Value} {posMatch.Groups[2].Value} {posMatch.Groups[3].Value}";
            if (posMatch.Groups[4].Success)
                extraNbt = posMatch.Groups[4].Value;
            return true;
        }

        // Check if rest starts with NBT directly (no position)
        if (rest.StartsWith('{'))
        {
            position = "~ ~ ~";
            extraNbt = rest;
            return true;
        }

        // Couldn't parse position, treat all as position
        position = rest;
        return true;
    }

    public async Task ProcessEvent(WebhookEvent evt, Profile? activeProfile)
    {
        if (activeProfile == null)
        {
            _logger.LogWarning("No active profile. Ignoring event.");
            return;
        }

        // ?? Join?Follow promotion: if a Join event arrives for a known follower,
        //    re-route it to be processed as a Follow event instead.
        if (evt.EventType == WebhookEventType.Join &&
            _followerService.IsFollower(evt.Username))
        {
            _logger.LogInfo($"[Followers] {evt.Nickname} is a known follower - promoting Join to Follow.", LogCategory.Join);
            evt.EventType = WebhookEventType.Follow;
        }

        // ?? On Follow events, auto-register the viewer in the follower DB
        if (evt.EventType == WebhookEventType.Follow)
        {
            var isNew = _followerService.AddOrUpdate(evt.Username, evt.Nickname, evt.ProfilePictureUrl);
            if (isNew)
                _logger.LogInfo($"[Followers] {evt.Nickname} (@{evt.Username}) added to follower database.", LogCategory.Follow);
        }

        // Log the incoming event
        switch (evt.EventType)
        {
            case WebhookEventType.Gift:
                _logger.LogEvent($"Gift: {evt.GiftName} from {evt.Nickname}", LogCategory.Gift);
                break;
            case WebhookEventType.Follow:
                _logger.LogEvent($"Follow from {evt.Nickname}", LogCategory.Follow);
                break;
            case WebhookEventType.Chat:
                _logger.LogEvent($"Chat from {evt.Nickname}: {evt.Comment}", LogCategory.Chat);
                break;
            case WebhookEventType.Like:
                _logger.LogEvent($"Like from {evt.Nickname} \u00d7{evt.LikeCount} (total {evt.TotalLikeCount})", LogCategory.Like);
                break;
            case WebhookEventType.Join:
                _logger.LogEvent($"Join from {evt.Nickname}", LogCategory.Join);
                break;
            case WebhookEventType.Share:
                _logger.LogEvent($"Share from {evt.Nickname}", LogCategory.Share);
                break;
            case WebhookEventType.Subscribe:
                _logger.LogEvent($"Subscribe from {evt.Nickname}", LogCategory.Subscribe);
                break;
            default:
                return;
        }

        if (activeProfile.ActionMappings.Count == 0)
        {
            _logger.LogWarning("No action mappings saved in this profile. Add mappings and click Save Changes.", LogCategory.System);
            return;
        }

        var fired = false;
        foreach (var mapping in activeProfile.ActionMappings)
        {
            bool matches = mapping.TriggerType switch
            {
                ActionTriggerType.Gift =>
                    evt.EventType == WebhookEventType.Gift &&
                    mapping.TriggerKey.Split('|', StringSplitOptions.RemoveEmptyEntries)
                        .Any(k => string.Equals(k.Trim(), evt.GiftName, StringComparison.OrdinalIgnoreCase)),

                ActionTriggerType.Follow =>
                    evt.EventType == WebhookEventType.Follow,

                ActionTriggerType.Chat =>
                    evt.EventType == WebhookEventType.Chat &&
                    (string.IsNullOrWhiteSpace(mapping.TriggerKey) ||
                     evt.Comment.Contains(mapping.TriggerKey, StringComparison.OrdinalIgnoreCase)),

                ActionTriggerType.Like =>
                    evt.EventType == WebhookEventType.Like &&
                    (string.IsNullOrWhiteSpace(mapping.TriggerKey) ||
                     int.TryParse(mapping.TriggerKey, out var minLikes) && evt.LikeCount >= minLikes),

                ActionTriggerType.Join =>
                    evt.EventType == WebhookEventType.Join,

                ActionTriggerType.Share =>
                    evt.EventType == WebhookEventType.Share,

                ActionTriggerType.Subscribe =>
                    evt.EventType == WebhookEventType.Subscribe,

                _ => false
            };

            if (!matches) continue;

            if (!_rcon.IsConnected)
            {
                _logger.LogWarning("RCON not connected. Cannot execute command.", LogCategory.System);
                return;
            }

            fired = true;

            // ?? Follow + ReplaceJoinMob: silently kill the viewer's existing Join creature
            if (mapping.TriggerType == ActionTriggerType.Follow && mapping.ReplaceJoinMob)
            {
                await SilentlyKillExistingCreature(evt.Username, evt.Nickname);
            }

            if (!string.IsNullOrEmpty(mapping.TargetButtonId))
            {
                var button = activeProfile.CommandButtons
                    .FirstOrDefault(b => b.Id == mapping.TargetButtonId);

                if (button != null)
                {
                    _logger.LogInfo($"Firing [{mapping.TriggerType}:{mapping.TriggerKey}] \u2192 Button '{button.Name}'", LogCategory.System);

                    if (button.ButtonType == CommandButtonType.Summon && button.SummonTrackCreature &&
                        !string.IsNullOrEmpty(button.SummonEntityType))
                    {
                        // Use the structured summon path - routes through creature tracker
                        await ExecuteSummonButton(button, evt.Nickname, evt.Username, evt.ProfilePictureUrl);
                    }
                    else if (button.ButtonType == CommandButtonType.Summon)
                    {
                        // Legacy summon button - scan commands for summon patterns
                        await ExecuteButtonWithCreatureTracking(button, evt.Nickname, evt.Username, evt.ProfilePictureUrl);
                    }
                    else if (button.ButtonType == CommandButtonType.Buff)
                    {
                        // Buff button - apply heal/damage to the viewer's active creature
                        await ExecuteBuffButton(button, evt.Nickname, evt.Username);
                    }
                    else
                    {
                        await _buttonExecutor.ExecuteAsync(button, evt.Nickname, evt.Username);
                    }
                }
                else
                {
                    _logger.LogWarning($"Button '{mapping.TargetButtonId}' not found in profile.", LogCategory.System);
                }
            }
            else
            {
                var command = BuildCommand(mapping.Command, evt.Nickname, evt.Username);
                _logger.LogInfo($"Firing [{mapping.TriggerType}:{mapping.TriggerKey}] \u2192 {command}", LogCategory.System);

                if (TryParseSummonCommand(command, out var entityType, out var position, out var nbt))
                {
                    await _creatureTracker.SummonCreatureAsync(evt.Nickname, evt.Username, entityType, position, nbt,
                        ownerProfilePictureUrl: evt.ProfilePictureUrl);
                }
                else
                {
                    await _rcon.SendCommand(command);
                }
            }
        }

        if (!fired)
        {
            _logger.LogInfo(
                $"No mapping matched: {evt.EventType}" +
                (evt.EventType == WebhookEventType.Gift   ? $"/{evt.GiftName}" : string.Empty) +
                (evt.EventType == WebhookEventType.Chat   ? $"/\"{evt.Comment}\"" : string.Empty) +
                (evt.EventType == WebhookEventType.Like   ? $" \u00d7{evt.LikeCount}" : string.Empty) +
                (evt.EventType == WebhookEventType.Join   ? $" ({evt.Nickname})" : string.Empty) +
                (evt.EventType == WebhookEventType.Share  ? $" ({evt.Nickname})" : string.Empty),
                LogCategory.System);
        }
    }

    /// <summary>
    /// Silently kills a viewer's existing creature without counting the death/kill
    /// in the arena rankings. Used when upgrading a Join mob to a Follow mob.
    /// </summary>
    private async Task SilentlyKillExistingCreature(string ownerUsername, string ownerNickname)
    {
        if (!_creatureTracker.HasActiveCreature(ownerUsername))
            return;

        _logger.LogInfo($"[Arena] Silently removing {ownerNickname}'s Join creature for Follow upgrade.", LogCategory.System);

        // Remove the creature from tracking WITHOUT recording stats
        _creatureTracker.SilentlyRemoveCreature(ownerUsername);

        // Kill the entity in-game by tag
        // We don't know the exact tag, so kill all viewer-tagged entities owned by this user
        // The tracker uses ownerUsername as the key for non-boss creatures
        await Task.Delay(100);
    }

    /// <summary>
    /// Executes a Buff button: applies heal and/or damage buff to the viewer's active creature,
    /// then runs any additional commands in the button's command list.
    /// </summary>
    private async Task ExecuteBuffButton(CommandButton button, string nickname, string username)
    {
        if (!_rcon.IsConnected) return;

        var healAmount = button.BuffApplyHeal ? button.BuffHealAmount : 0;
        var damageAmount = button.BuffApplyDamage ? button.BuffDamageAmount : 0;

        if (healAmount > 0 || damageAmount > 0)
        {
            var applied = await _creatureTracker.BuffCreatureAsync(username, healAmount, damageAmount);
            if (!applied)
            {
                _logger.LogInfo($"[Buff] No active creature for {nickname} to buff.", LogCategory.System);
            }
        }

        // Execute any additional commands in the button
        var creature = _creatureTracker.GetActiveCreature(username);
        foreach (var template in button.Commands)
        {
            if (string.IsNullOrWhiteSpace(template)) continue;

            var cmd = template;
            if (button.UseNickname)
                cmd = BuildCommand(template, nickname, username);

            // Replace {tag} with the creature's tracking tag if available
            if (creature != null)
                cmd = cmd.Replace("{tag}", creature.TrackingId);

            await _rcon.SendCommand(cmd);
        }
    }

    /// <summary>
    /// Executes a Summon button that has structured summon settings (entity type, position, HP, attack).
    /// Routes through CreatureTrackerService for proper tracking.
    /// Also runs any additional commands in the button's command list.
    /// </summary>
    private async Task ExecuteSummonButton(CommandButton button, string nickname, string username, string profilePictureUrl)
    {
        if (!_rcon.IsConnected) return;

        // Summon the creature via tracker with custom HP/attack
        var creature = await _creatureTracker.SummonCreatureAsync(
            nickname,
            username,
            button.SummonEntityType,
            button.SummonPosition,
            extraNbt: "",
            customHealth: button.SummonCustomHealth,
            customAttackDamage: button.SummonCustomAttack,
            isBoss: button.SummonIsBoss,
            ownerProfilePictureUrl: profilePictureUrl,
            bossName: button.SummonBossName);

        if (creature == null) return; // blocked or failed

        // Track which button spawned this creature for auto-respawn
        creature.LastButtonId = button.Id;

        // Execute any additional commands in the button
        foreach (var template in button.Commands)
        {
            if (string.IsNullOrWhiteSpace(template)) continue;

            var cmd = template;
            if (button.UseNickname)
                cmd = BuildCommand(template, nickname, username);

            // Replace {tag} with the creature's tracking tag for command chaining
            cmd = cmd.Replace("{tag}", creature.TrackingId);

            await _rcon.SendCommand(cmd);
        }
    }

    /// <summary>
    /// Fallback: Executes a Summon-type button's commands, scanning for summon patterns.
    /// </summary>
    private async Task ExecuteButtonWithCreatureTracking(CommandButton button, string nickname, string username, string profilePictureUrl)
    {
        if (!_rcon.IsConnected) return;

        foreach (var template in button.Commands)
        {
            if (string.IsNullOrWhiteSpace(template)) continue;

            var cmd = template;
            if (button.UseNickname)
                cmd = BuildCommand(template, nickname, username);

            if (TryParseSummonCommand(cmd, out var entityType, out var position, out var nbt))
            {
                await _creatureTracker.SummonCreatureAsync(nickname, username, entityType, position, nbt,
                    ownerProfilePictureUrl: profilePictureUrl);
            }
            else
            {
                await _rcon.SendCommand(cmd);
            }
        }
    }
}
