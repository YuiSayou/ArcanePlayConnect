using System;
using System.Linq;
using System.Threading.Tasks;
using ArcanePlayConnect.Core.Models;
using ArcanePlayConnect.Services;

namespace ArcanePlayConnect.Core;

public class EventProcessor
{
    private readonly RconService _rcon;
    private readonly LoggingService _logger;
    private readonly CommandButtonExecutor _buttonExecutor;

    public EventProcessor(RconService rcon, LoggingService logger, CommandButtonExecutor buttonExecutor)
    {
        _rcon = rcon;
        _logger = logger;
        _buttonExecutor = buttonExecutor;
    }

    /// <summary>
    /// Escapes a player nickname so it is safe to embed inside a Minecraft JSON text
    /// component (e.g. CustomName NBT).  Only backslash and double-quote need escaping.
    /// </summary>
    public static string EscapeJson(string input) =>
        input.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    /// Prepares a stored command template for sending to RCON:
    ///   1. Replaces {nickname} and {safe} with the NBT-escaped display name.
    ///   2. Replaces {username} with the raw TikTok username (safe for selector use).
    ///   3. Collapses {{ -> { and }} -> }.
    /// </summary>
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

    public async Task ProcessEvent(WebhookEvent evt, Profile? activeProfile)
    {
        if (activeProfile == null)
        {
            _logger.LogWarning("No active profile. Ignoring event.");
            return;
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
                    string.Equals(mapping.TriggerKey, evt.GiftName, StringComparison.OrdinalIgnoreCase),

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

                _ => false
            };

            if (!matches) continue;

            if (!_rcon.IsConnected)
            {
                _logger.LogWarning("RCON not connected. Cannot execute command.", LogCategory.System);
                return;
            }

            fired = true;

            // Check if this mapping triggers a button
            if (!string.IsNullOrEmpty(mapping.TargetButtonId))
            {
                var button = activeProfile.CommandButtons
                    .FirstOrDefault(b => b.Id == mapping.TargetButtonId);

                if (button != null)
                {
                    _logger.LogInfo($"Firing [{mapping.TriggerType}:{mapping.TriggerKey}] \u2192 Button '{button.Name}'", LogCategory.System);
                    await _buttonExecutor.ExecuteAsync(button, evt.Nickname, evt.Username);
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
                await _rcon.SendCommand(command);
            }
        }

        if (!fired)
        {
            _logger.LogInfo(
                $"No mapping matched: {evt.EventType}" +
                (evt.EventType == WebhookEventType.Gift   ? $"/{evt.GiftName}" : string.Empty) +
                (evt.EventType == WebhookEventType.Chat   ? $"/\"{evt.Comment}\"" : string.Empty) +
                (evt.EventType == WebhookEventType.Like   ? $" \u00d7{evt.LikeCount}" : string.Empty),
                LogCategory.System);
        }
    }
}
