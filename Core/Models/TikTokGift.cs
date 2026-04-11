namespace ArcanePlayConnect.Core.Models;

/// <summary>
/// Represents a TikTok gift with its name, coin price, and image URL.
/// </summary>
public class TikTokGift
{
    public string Name { get; set; } = string.Empty;
    public int CoinPrice { get; set; }
    public string ImageUrl { get; set; } = string.Empty;

    /// <summary>Local cached image file path (set after download).</summary>
    public string? LocalImagePath { get; set; }

    /// <summary>Whether this is a free interaction (Like, Follow) rather than a paid gift.</summary>
    public bool IsFreeInteraction => CoinPrice == 0;

    /// <summary>
    /// URL suitable for display in WinUI Image controls.
    /// Returns the local cached file path for built-in icons (data: URIs are not supported by BitmapImage),
    /// or the original ImageUrl for regular gifts.
    /// </summary>
    public string DisplayImageUrl =>
        !string.IsNullOrEmpty(LocalImagePath) ? LocalImagePath : ImageUrl;

    public string DisplayText => IsFreeInteraction ? $"{Name} (Free)" : $"{Name} ({CoinPrice} coins)";
}
