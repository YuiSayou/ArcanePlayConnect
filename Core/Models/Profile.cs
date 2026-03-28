using System.Collections.Generic;

namespace ArcanePlayConnect.Core.Models;

public class Profile
{
    public string Id { get; set; } = System.Guid.NewGuid().ToString();
    public string ProfileName { get; set; } = "New Profile";
    public string GameType { get; set; } = "Minecraft";
    public int WebhookPort { get; set; } = 5000;
    public string RconIP { get; set; } = "127.0.0.1";
    public int RconPort { get; set; } = 25575;
    public string RconPassword { get; set; } = string.Empty;
    public List<ActionMapping> ActionMappings { get; set; } = new();
    public List<CommandButton> CommandButtons { get; set; } = new();
}
