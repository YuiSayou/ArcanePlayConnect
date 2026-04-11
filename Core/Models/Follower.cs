using System;

namespace ArcanePlayConnect.Core.Models;

/// <summary>
/// Represents a TikTok viewer who has followed the streamer.
/// Stored in the follower database so future Join events can be
/// automatically upgraded to Follow-tier actions.
/// </summary>
public class Follower
{
    /// <summary>TikTok username (login handle, unique key).</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>TikTok display name (nickname).</summary>
    public string Nickname { get; set; } = string.Empty;

    /// <summary>Profile picture URL from the last webhook event.</summary>
    public string ProfilePictureUrl { get; set; } = string.Empty;

    /// <summary>When this follower was first recorded.</summary>
    public DateTime FollowedAt { get; set; } = DateTime.Now;

    /// <summary>Optional notes (editable by the user).</summary>
    public string Notes { get; set; } = string.Empty;
}
