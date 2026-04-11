using System;
using System.Collections.Generic;

namespace ArcanePlayConnect.Core.Models;

public enum OverlayType
{
    RankingVertical,
    RankingHorizontal,
    GiftWall,
    GiftWallVertical,
    LikesRankingVertical,
    LikesRankingHorizontal,
    GiftRankingVertical,
    GiftRankingHorizontal
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

    /// <summary>Custom text labels for each gift (keyed by gift name). Displayed beside the gift in the overlay.</summary>
    public Dictionary<string, string> GiftTextLabels { get; set; } = new();

    /// <summary>
    /// Unique streamer channel ID for per-streamer overlay isolation on Cloudflare Pages.
    /// Auto-generated once per overlay. Alphanumeric + hyphens, 3-64 chars.
    /// </summary>
    public string StreamerId { get; set; } = GenerateStreamerId();

    /// <summary>
    /// Base URL of the Cloudflare Pages deployment.
    /// Defaults to the official ArcanePlayConnect Cloudflare Pages site.
    /// </summary>
    public string CloudflareBaseUrl { get; set; } = DefaultCloudflareUrl;

    /// <summary>
    /// Whether to enable cloud relay - pushes data to Cloudflare Pages Functions
    /// so overlays work without port forwarding.
    /// </summary>
    public bool CloudPushEnabled { get; set; } = true;

    /// <summary>
    /// The default Cloudflare Pages URL for ArcanePlayConnect.
    /// </summary>
    public const string DefaultCloudflareUrl = "https://arcaneplayconnect.pages.dev";

    private static string GenerateStreamerId()
    {
        return $"s-{Guid.NewGuid().ToString("N")[..12]}";
    }
}
