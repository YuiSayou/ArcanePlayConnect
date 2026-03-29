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

    public string DisplayText => $"{Name} ({CoinPrice} coins)";
}
