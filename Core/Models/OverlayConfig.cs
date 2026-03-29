using System;
using System.Collections.Generic;

namespace ArcanePlayConnect.Core.Models;

public enum OverlayType
{
    RankingVertical,
    RankingHorizontal,
    GiftWall
}

public enum OverlayTheme
{
    Cyberpunk,
    NeonFire,
    ArcticFrost,
    DragonForge,
    SakuraBloom,
    VoidShadow
}

public enum OverlayStat
{
    Damage,
    Kills,
    HP
}

public class OverlayConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "New Overlay";
    public OverlayType Type { get; set; } = OverlayType.RankingVertical;
    public OverlayTheme Theme { get; set; } = OverlayTheme.Cyberpunk;
    public bool ShowHP { get; set; } = true;
    public bool ShowDamage { get; set; } = true;
    public bool ShowKills { get; set; } = true;
    public int MaxPlayers { get; set; } = 5;
    public int RefreshIntervalMs { get; set; } = 2000;

    /// <summary>The port the overlay server listens on.</summary>
    public int Port { get; set; } = 7700;

    /// <summary>Gift names selected for the GiftWall overlay.</summary>
    public List<string> SelectedGiftNames { get; set; } = new();
}
