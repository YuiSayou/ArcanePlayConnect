namespace ArcanePlayConnect.Core.Models;

public enum WebhookEventType
{
    Unknown,
    Gift,
    Follow,
    Chat,
    Like,
    Join,
    Share,
    Subscribe
}

public class WebhookEvent
{
    public WebhookEventType EventType { get; set; } = WebhookEventType.Unknown;

    // Common fields for all events
    public string Nickname { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string ProfilePictureUrl { get; set; } = string.Empty;
    public int FollowStatus { get; set; }
    public bool IsSubscriber { get; set; }

    // Gift-specific fields
    public string GiftName { get; set; } = string.Empty;
    public string GiftPictureUrl { get; set; } = string.Empty;
    public int GiftId { get; set; }
    public int GiftDiamondCost { get; set; }
    public int GiftRepeatCount { get; set; }
    public bool GiftStreakEnd { get; set; }

    // Chat-specific field
    public string Comment { get; set; } = string.Empty;

    // Like-specific fields
    public int LikeCount { get; set; }
    public int TotalLikeCount { get; set; }

    // Join-specific fields
    public int ViewerCount { get; set; }
}