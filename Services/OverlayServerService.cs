using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArcanePlayConnect.Core.Models;

namespace ArcanePlayConnect.Services;

/// <summary>
/// Hosts a local HTTP server that serves OBS Browser Source overlay pages.
/// Each overlay gets a unique URL path: http://localhost:{port}/overlay/{id}
/// A JSON data endpoint at /overlay/{id}/data returns live leaderboard data.
/// </summary>
public class OverlayServerService
{
    private readonly LoggingService _logger;
    private readonly CreatureTrackerService _tracker;
    private readonly LiveStatsTrackerService _liveStats;

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _port;

    private readonly ConcurrentDictionary<string, OverlayConfig> _overlays = new();

    public bool IsRunning => _listener?.IsListening == true;
    public int Port => _port;
    public event Action? StatusChanged;

    public OverlayServerService(LoggingService logger, CreatureTrackerService tracker, LiveStatsTrackerService liveStats)
    {
        _logger = logger;
        _tracker = tracker;
        _liveStats = liveStats;
    }

    public void RegisterOverlay(OverlayConfig config)
    {
        _overlays[config.Id] = config;
    }

    public void UnregisterOverlay(string id)
    {
        _overlays.TryRemove(id, out _);
    }

    public string GetOverlayUrl(string overlayId)
    {
        return $"http://localhost:{_port}/overlay/{overlayId}";
    }

    /// <summary>
    /// Builds the cloud overlay URL for Cloudflare Pages with per-streamer isolation.
    /// The overlay reads data from the same Cloudflare origin via Pages Functions relay.
    /// </summary>
    public static string GetCloudOverlayUrl(OverlayConfig cfg)
    {
        if (string.IsNullOrEmpty(cfg.CloudflareBaseUrl))
            return string.Empty;

        var baseUrl = cfg.CloudflareBaseUrl.TrimEnd('/');
        var path = cfg.Type switch
        {
            OverlayType.RankingVertical => "ranking-vertical",
            OverlayType.RankingHorizontal => "ranking-horizontal",
            OverlayType.LikesRankingVertical => "likes-vertical",
            OverlayType.LikesRankingHorizontal => "likes-horizontal",
            OverlayType.GiftRankingVertical => "gift-ranking-vertical",
            OverlayType.GiftRankingHorizontal => "gift-ranking-horizontal",
            OverlayType.GiftWall => "gift-wall",
            OverlayType.GiftWallVertical => "gift-wall-vertical",
            _ => "ranking-vertical"
        };

        var statParts = new List<string>();
        if (cfg.ShowHP) statParts.Add("HP");
        if (cfg.ShowDamage) statParts.Add("DMG");
        if (cfg.ShowKills) statParts.Add("KILLS");
        var stats = statParts.Count > 0 ? string.Join(",", statParts) : "HP,DMG,KILLS";
        var theme = cfg.Theme.ToString().ToLowerInvariant();

        // Build gift wall data for static gift overlays
        var giftParam = "";
        if (cfg.Type is OverlayType.GiftWall or OverlayType.GiftWallVertical && cfg.SelectedGiftNames.Count > 0)
        {
            giftParam = $"&gifts={Uri.EscapeDataString(BuildGiftItemsJson(cfg))}";
        }

        var url = $"{baseUrl}/{path}/?streamer={Uri.EscapeDataString(cfg.StreamerId)}" +
                  $"&overlay={Uri.EscapeDataString(cfg.Id)}" +
                  $"&theme={Uri.EscapeDataString(theme)}" +
                  $"&max={cfg.MaxPlayers}" +
                  $"&refresh={cfg.RefreshIntervalMs}" +
                  $"&stats={Uri.EscapeDataString(stats)}" +
                  giftParam;

        return url;
    }

    public void Start(int port)
    {
        if (IsRunning) Stop();

        try
        {
            _port = port;
            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
            _logger.LogInfo($"[Overlay] Server started on port {port}", LogCategory.System);
            StatusChanged?.Invoke();
            _ = ListenLoopAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError($"[Overlay] Failed to start server: {ex.Message}");
            Stop();
        }
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
            _listener?.Close();
        }
        catch { }
        finally
        {
            _listener = null;
            _cts = null;
            _logger.LogInfo("[Overlay] Server stopped.", LogCategory.System);
            StatusChanged?.Invoke();
        }
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener?.IsListening == true)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(ctx), token);
            }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning($"[Overlay] Listener error: {ex.Message}", LogCategory.System);
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            var method = ctx.Request.HttpMethod;

            // Handle CORS preflight for cloud overlay requests
            if (method == "OPTIONS")
            {
                AddCorsHeaders(ctx);
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
                return;
            }

            // Route: /giftimage/{name}  ? serve cached gift image
            if (path.StartsWith("/giftimage/"))
            {
                var giftName = Uri.UnescapeDataString(path.Replace("/giftimage/", "").TrimEnd('/'));
                if (GiftImageService.TryGetImageBytes(giftName, out var imgData, out var contentType) && imgData != null)
                {
                    AddCorsHeaders(ctx);
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = contentType;
                    ctx.Response.Headers.Add("Cache-Control", "public, max-age=86400");
                    await ctx.Response.OutputStream.WriteAsync(imgData);
                    ctx.Response.Close();
                    return;
                }
            }

            // Route: /api/streamer/{streamerId}/overlay/{overlayId}/data  ? per-streamer JSON data (for cloud overlays)
            if (path.StartsWith("/api/streamer/") && path.EndsWith("/data"))
            {
                var handled = await HandleStreamerApiAsync(ctx, path);
                if (handled) return;
            }

            // Route: /overlay/{id}/data  ? JSON data (local)
            if (path.StartsWith("/overlay/") && path.EndsWith("/data"))
            {
                var id = path.Replace("/overlay/", "").Replace("/data", "");
                if (_overlays.TryGetValue(id, out var cfg))
                {
                    await ServeJsonDataAsync(ctx, cfg);
                    return;
                }
            }
            // Route: /overlay/{id}  ? HTML page (local)
            else if (path.StartsWith("/overlay/"))
            {
                var id = path.Replace("/overlay/", "").TrimEnd('/');
                if (_overlays.TryGetValue(id, out var cfg))
                {
                    await ServeOverlayHtmlAsync(ctx, cfg);
                    return;
                }
            }

            ctx.Response.StatusCode = 404;
            var notFound = Encoding.UTF8.GetBytes("Not Found");
            await ctx.Response.OutputStream.WriteAsync(notFound);
            ctx.Response.Close();
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"[Overlay] Request error: {ex.Message}", LogCategory.System);
            try { ctx.Response.Close(); } catch { }
        }
    }

    /// <summary>
    /// Handles the per-streamer API route: /api/streamer/{streamerId}/overlay/{overlayId}/data
    /// Validates the streamer ID against registered overlays to ensure isolation.
    /// </summary>
    private async Task<bool> HandleStreamerApiAsync(HttpListenerContext ctx, string path)
    {
        // Parse: /api/streamer/{streamerId}/overlay/{overlayId}/data
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Expected: ["api", "streamer", "{streamerId}", "overlay", "{overlayId}", "data"]
        if (segments.Length != 6 ||
            segments[0] != "api" || segments[1] != "streamer" ||
            segments[3] != "overlay" || segments[5] != "data")
            return false;

        var streamerId = Uri.UnescapeDataString(segments[2]);
        var overlayId = Uri.UnescapeDataString(segments[4]);

        // Validate streamer ID format (alphanumeric + hyphens/underscores, 3-64 chars)
        if (string.IsNullOrEmpty(streamerId) || streamerId.Length > 64 ||
            !System.Text.RegularExpressions.Regex.IsMatch(streamerId, @"^[a-zA-Z0-9_-]{3,64}$"))
        {
            AddCorsHeaders(ctx);
            ctx.Response.StatusCode = 400;
            var bad = Encoding.UTF8.GetBytes("{\"error\":\"Invalid streamer ID\"}");
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bad);
            ctx.Response.Close();
            return true;
        }

        // Find the overlay - it must exist AND its StreamerId must match
        if (!_overlays.TryGetValue(overlayId, out var cfg) ||
            !string.Equals(cfg.StreamerId, streamerId, StringComparison.Ordinal))
        {
            AddCorsHeaders(ctx);
            ctx.Response.StatusCode = 403;
            var forbidden = Encoding.UTF8.GetBytes("{\"error\":\"Access denied or overlay not found\"}");
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(forbidden);
            ctx.Response.Close();
            return true;
        }

        // Serve the data with CORS headers
        AddCorsHeaders(ctx);
        await ServeJsonDataAsync(ctx, cfg);
        return true;
    }

    /// <summary>
    /// Adds CORS headers to allow requests from Cloudflare Pages (and any origin for overlay use).
    /// </summary>
    private static void AddCorsHeaders(HttpListenerContext ctx)
    {
        ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
        ctx.Response.Headers.Add("Access-Control-Allow-Methods", "GET, OPTIONS");
        ctx.Response.Headers.Add("Access-Control-Allow-Headers", "Accept, Content-Type");
        ctx.Response.Headers.Add("Access-Control-Max-Age", "86400");
    }

    /// <summary>
    /// Builds the JSON data for an overlay config. Used by both the local server
    /// and the cloud push service.
    /// </summary>
    public string BuildOverlayJson(OverlayConfig cfg)
    {
        if (cfg.Type == OverlayType.LikesRankingVertical || cfg.Type == OverlayType.LikesRankingHorizontal)
            return BuildLikesRankingJson(cfg);
        if (cfg.Type == OverlayType.GiftRankingVertical || cfg.Type == OverlayType.GiftRankingHorizontal)
            return BuildGiftRankingJson(cfg);
        return BuildCreatureRankingJson(cfg);
    }

    private async Task ServeJsonDataAsync(HttpListenerContext ctx, OverlayConfig cfg)
    {
        string json = BuildOverlayJson(cfg);

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
        ctx.Response.Headers.Add("Cache-Control", "no-cache");
        var bytes = Encoding.UTF8.GetBytes(json);
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private string BuildCreatureRankingJson(OverlayConfig cfg)
    {
        var leaderboard = _tracker.GetLeaderboard();
        var active = _tracker.GetActiveCreatures();

        var entries = leaderboard.Take(cfg.MaxPlayers).Select(e =>
        {
            // Try to find the matching active creature to get profile picture URL from owner
            var activeMatch = active.FirstOrDefault(a =>
                string.Equals(a.OwnerUsername, e.OwnerUsername, StringComparison.OrdinalIgnoreCase));

            // Use the TikTok avatar_url from the webhook if available, fall back to Minecraft skin
            var profilePic = !string.IsNullOrEmpty(e.OwnerProfilePictureUrl)
                ? e.OwnerProfilePictureUrl
                : !string.IsNullOrEmpty(activeMatch?.OwnerProfilePictureUrl)
                    ? activeMatch.OwnerProfilePictureUrl
                    : $"https://minotar.net/helm/{e.OwnerNickname}/64";

            return new
            {
                rank = e.Rank,
                nickname = e.OwnerNickname,
                username = e.OwnerUsername,
                creature = e.LastEntityDisplayName,
                damage = e.TotalDamageDealt,
                kills = e.TotalKills,
                alive = e.HasAlive,
                hp = activeMatch?.CurrentHealth ?? 0,
                maxHp = activeMatch?.MaxHealth ?? 0,
                survivalTime = FormatSurvivalTime(e.BestSurvivalTime),
                profilePicture = profilePic
            };
        });

        var json = JsonSerializer.Serialize(new { players = entries, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        return json;
    }

    private string BuildLikesRankingJson(OverlayConfig cfg)
    {
        var board = _liveStats.GetLikesLeaderboard(cfg.MaxPlayers);
        var rank = 0;
        var entries = board.Select(e =>
        {
            rank++;
            var profilePic = !string.IsNullOrEmpty(e.ProfilePictureUrl)
                ? e.ProfilePictureUrl
                : $"https://minotar.net/helm/{e.Nickname}/64";
            return new
            {
                rank,
                nickname = e.Nickname,
                username = e.Username,
                totalLikes = e.TotalLikes,
                profilePicture = profilePic
            };
        });
        return JsonSerializer.Serialize(new { players = entries, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
    }

    private string BuildGiftRankingJson(OverlayConfig cfg)
    {
        var board = _liveStats.GetGiftLeaderboard(cfg.MaxPlayers);
        var rank = 0;
        var entries = board.Select(e =>
        {
            rank++;
            var profilePic = !string.IsNullOrEmpty(e.ProfilePictureUrl)
                ? e.ProfilePictureUrl
                : $"https://minotar.net/helm/{e.Nickname}/64";
            return new
            {
                rank,
                nickname = e.Nickname,
                username = e.Username,
                totalCoins = e.TotalCoinsSpent,
                giftCount = e.GiftCount,
                profilePicture = profilePic
            };
        });
        return JsonSerializer.Serialize(new { players = entries, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
    }

    private async Task ServeOverlayHtmlAsync(HttpListenerContext ctx, OverlayConfig cfg)
    {
        var html = cfg.Type switch
        {
            OverlayType.RankingVertical => GenerateVerticalRankingHtml(cfg),
            OverlayType.RankingHorizontal => GenerateHorizontalRankingHtml(cfg),
            OverlayType.GiftWall => GenerateGiftWallHtml(cfg),
            OverlayType.GiftWallVertical => GenerateGiftWallVerticalHtml(cfg),
            OverlayType.LikesRankingVertical => GenerateLikesRankingVerticalHtml(cfg),
            OverlayType.LikesRankingHorizontal => GenerateLikesRankingHorizontalHtml(cfg),
            OverlayType.GiftRankingVertical => GenerateGiftRankingVerticalHtml(cfg),
            OverlayType.GiftRankingHorizontal => GenerateGiftRankingHorizontalHtml(cfg),
            _ => GenerateVerticalRankingHtml(cfg)
        };

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers.Add("Cache-Control", "no-cache");
        var bytes = Encoding.UTF8.GetBytes(html);
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private static string FormatSurvivalTime(TimeSpan t)
    {
        if (t.TotalHours >= 1)
            return $"{t:h\\:mm\\:ss}";
        return $"{t:mm\\:ss}";
    }

    // ---------------------------------------------------------------
    //  THEME / STAT HELPERS
    // ---------------------------------------------------------------

    private static string GetThemeColors(OverlayTheme theme) => theme switch
    {
        OverlayTheme.Cyberpunk => @"
            --accent: #00c8ff; --accent2: #b400ff; --accent3: #ff3278;
            --bg: rgba(10,10,20,0.92); --card: rgba(22,22,42,0.95);
            --card-border: rgba(0,200,255,0.25); --text: #e0e0ff; --text2: #8888aa;
            --rank1: #ffd700; --rank2: #c0c0c0; --rank3: #cd7f32;
            --hp-bar: linear-gradient(90deg, #00ff88, #00c8ff);
            --hp-bar-low: linear-gradient(90deg, #ff3278, #ff9500);
            --gradient: linear-gradient(135deg, #00c8ff, #b400ff, #ff3278);
            --glow: 0 0 20px rgba(0,200,255,0.3);
            --title-font: 'Orbitron', sans-serif;",

        OverlayTheme.NeonFire => @"
            --accent: #ff6b00; --accent2: #ff0044; --accent3: #ffcc00;
            --bg: rgba(15,5,5,0.92); --card: rgba(40,12,12,0.95);
            --card-border: rgba(255,107,0,0.3); --text: #ffe0d0; --text2: #aa7766;
            --rank1: #ffcc00; --rank2: #ff8844; --rank3: #cc4400;
            --hp-bar: linear-gradient(90deg, #ffcc00, #ff6b00);
            --hp-bar-low: linear-gradient(90deg, #ff0044, #880022);
            --gradient: linear-gradient(135deg, #ff6b00, #ff0044, #ffcc00);
            --glow: 0 0 20px rgba(255,107,0,0.4);
            --title-font: 'Orbitron', sans-serif;",

        OverlayTheme.ArcticFrost => @"
            --accent: #88ddff; --accent2: #44aaff; --accent3: #ffffff;
            --bg: rgba(8,15,25,0.92); --card: rgba(15,30,50,0.95);
            --card-border: rgba(136,221,255,0.2); --text: #d0e8ff; --text2: #6699bb;
            --rank1: #ffffff; --rank2: #88ddff; --rank3: #44aaff;
            --hp-bar: linear-gradient(90deg, #88ddff, #44aaff);
            --hp-bar-low: linear-gradient(90deg, #ff6688, #cc2244);
            --gradient: linear-gradient(135deg, #88ddff, #44aaff, #ffffff);
            --glow: 0 0 20px rgba(136,221,255,0.3);
            --title-font: 'Rajdhani', sans-serif;",

        OverlayTheme.DragonForge => @"
            --accent: #ff4400; --accent2: #884400; --accent3: #ffaa00;
            --bg: rgba(12,8,4,0.92); --card: rgba(30,18,8,0.95);
            --card-border: rgba(255,68,0,0.3); --text: #ffd8b0; --text2: #996644;
            --rank1: #ffaa00; --rank2: #ff6600; --rank3: #884400;
            --hp-bar: linear-gradient(90deg, #ffaa00, #ff4400);
            --hp-bar-low: linear-gradient(90deg, #880000, #440000);
            --gradient: linear-gradient(135deg, #ff4400, #884400, #ffaa00);
            --glow: 0 0 20px rgba(255,68,0,0.4);
            --title-font: 'Cinzel', serif;",

        OverlayTheme.SakuraBloom => @"
            --accent: #ff88b4; --accent2: #cc44aa; --accent3: #ffccdd;
            --bg: rgba(15,8,12,0.92); --card: rgba(30,15,25,0.95);
            --card-border: rgba(255,136,180,0.25); --text: #ffe0f0; --text2: #aa6688;
            --rank1: #ffccdd; --rank2: #ff88b4; --rank3: #cc44aa;
            --hp-bar: linear-gradient(90deg, #ff88b4, #cc44aa);
            --hp-bar-low: linear-gradient(90deg, #884466, #442233);
            --gradient: linear-gradient(135deg, #ff88b4, #cc44aa, #ffccdd);
            --glow: 0 0 20px rgba(255,136,180,0.3);
            --title-font: 'Rajdhani', sans-serif;",

        OverlayTheme.VoidShadow => @"
            --accent: #aa44ff; --accent2: #4400aa; --accent3: #dd88ff;
            --bg: rgba(5,2,12,0.92); --card: rgba(15,8,30,0.95);
            --card-border: rgba(170,68,255,0.25); --text: #e0d0ff; --text2: #7755aa;
            --rank1: #dd88ff; --rank2: #aa44ff; --rank3: #6622cc;
            --hp-bar: linear-gradient(90deg, #aa44ff, #dd88ff);
            --hp-bar-low: linear-gradient(90deg, #ff2266, #880033);
            --gradient: linear-gradient(135deg, #aa44ff, #4400aa, #dd88ff);
            --glow: 0 0 20px rgba(170,68,255,0.4);
            --title-font: 'Orbitron', sans-serif;",

        _ => @"
            --accent: #00c8ff; --accent2: #b400ff; --accent3: #ff3278;
            --bg: rgba(10,10,20,0.92); --card: rgba(22,22,42,0.95);
            --card-border: rgba(0,200,255,0.25); --text: #e0e0ff; --text2: #8888aa;
            --rank1: #ffd700; --rank2: #c0c0c0; --rank3: #cd7f32;
            --hp-bar: linear-gradient(90deg, #00ff88, #00c8ff);
            --hp-bar-low: linear-gradient(90deg, #ff3278, #ff9500);
            --gradient: linear-gradient(135deg, #00c8ff, #b400ff, #ff3278);
            --glow: 0 0 20px rgba(0,200,255,0.3);
            --title-font: 'Orbitron', sans-serif;"
    };

    private static string GetStatColumns(OverlayConfig cfg)
    {
        var cols = new List<string>();
        if (cfg.ShowHP) cols.Add("HP");
        if (cfg.ShowDamage) cols.Add("DMG");
        if (cfg.ShowKills) cols.Add("KILLS");
        return JsonSerializer.Serialize(cols);
    }

    // ---------------------------------------------------------------
    //  SHARED JS - flicker-free DOM-diffing update logic
    // ---------------------------------------------------------------

    /// <summary>
    /// Returns the shared JavaScript that updates existing DOM nodes in-place
    /// instead of replacing innerHTML, preventing flicker and animation replay.
    /// </summary>
    private static string GetSharedUpdateScript() => @"
function escHtml(s) {
    const d = document.createElement('div');
    d.textContent = s;
    return d.innerHTML;
}

// Update a text node only if the value changed
function setText(el, selector, text) {
    const t = el.querySelector(selector);
    if (t && t.textContent !== text) t.textContent = text;
}

// Update an attribute only if changed
function setAttr(el, selector, attr, val) {
    const t = el.querySelector(selector);
    if (t && t.getAttribute(attr) !== val) t.setAttribute(attr, val);
}

// Set / remove a class
function setClass(el, cls, on) {
    if (on) el.classList.add(cls);
    else el.classList.remove(cls);
}

// Update rank classes (rank-1, rank-2, rank-3)
function setRankClass(el, rank) {
    el.classList.remove('rank-1','rank-2','rank-3');
    if (rank >= 1 && rank <= 3) el.classList.add('rank-' + rank);
}
";

    // ---------------------------------------------------------------
    //   VERTICAL RANKING
    // ---------------------------------------------------------------

    private string GenerateVerticalRankingHtml(OverlayConfig cfg)
    {
        var themeVars = GetThemeColors(cfg.Theme);
        var statCols = GetStatColumns(cfg);

        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<title>{WebUtility.HtmlEncode(cfg.Name)}</title>
<link href=""https://fonts.googleapis.com/css2?family=Orbitron:wght@400;700;900&family=Rajdhani:wght@400;600;700&family=Cinzel:wght@400;700;900&display=swap"" rel=""stylesheet"">
<style>
* {{ margin: 0; padding: 0; box-sizing: border-box; }}
:root {{ {themeVars} }}
body {{
    background: transparent;
    font-family: 'Rajdhani', 'Segoe UI', sans-serif;
    color: var(--text);
    overflow: hidden;
}}

.overlay-container {{ width: 520px; padding: 16px; }}

/* ?? HEADER ?? */
.header {{
    text-align: center;
    margin-bottom: 18px;
    position: relative;
}}
.header h1 {{
    font-family: var(--title-font);
    font-size: 28px;
    font-weight: 900;
    text-transform: uppercase;
    letter-spacing: 5px;
    background: var(--gradient);
    -webkit-background-clip: text;
    -webkit-text-fill-color: transparent;
    background-clip: text;
    filter: drop-shadow(0 0 10px var(--accent));
}}
.header-line {{
    height: 3px;
    background: var(--gradient);
    border-radius: 2px;
    margin-top: 8px;
    opacity: 0.7;
}}
.header-sub {{
    font-size: 14px;
    color: var(--text2);
    letter-spacing: 4px;
    text-transform: uppercase;
    margin-top: 6px;
}}

/* ?? PLAYER CARD ?? */
.player-card {{
    display: flex;
    align-items: center;
    gap: 14px;
    padding: 12px 14px;
    margin-bottom: 8px;
    background: var(--card);
    border: 1px solid var(--card-border);
    border-radius: 12px;
    position: relative;
    overflow: hidden;
    transition: opacity 0.4s ease, border-color 0.4s ease, box-shadow 0.4s ease;
    opacity: 1;
}}
.player-card.entering {{ animation: slideIn 0.4s ease forwards; }}
.player-card.removing {{ animation: slideOut 0.3s ease forwards; }}
.player-card::before {{
    content: '';
    position: absolute;
    top: 0; left: 0;
    width: 4px; height: 100%;
    background: var(--gradient);
    border-radius: 4px 0 0 4px;
    transition: background 0.4s ease;
}}
.player-card.rank-1 {{ box-shadow: var(--glow), inset 0 0 30px rgba(255,215,0,0.05); border-color: var(--rank1); }}
.player-card.rank-1::before {{ background: var(--rank1); }}
.player-card.rank-2 {{ border-color: var(--rank2); }}
.player-card.rank-2::before {{ background: var(--rank2); }}
.player-card.rank-3 {{ border-color: var(--rank3); }}
.player-card.rank-3::before {{ background: var(--rank3); }}
.player-card.dead {{ opacity: 0.55; }}

/* ?? TEXT LABEL ?? */
.player-card .text-label {{
    position: absolute;
    top: 8px;
    left: 10px;
    right: 10px;
    font-size: 10px;
    color: var(--text);
    text-align: center;
    opacity: 0.9;
}}

@keyframes slideIn {{
    from {{ opacity: 0; transform: translateX(-20px); }}
    to {{ opacity: 1; transform: translateX(0); }}
}}
@keyframes slideOut {{
    from {{ opacity: 1; transform: translateX(0); }}
    to {{ opacity: 0; transform: translateX(20px); }}
}}

/* ?? RANK BADGE ?? */
.rank-badge {{
    width: 42px; height: 42px;
    display: flex; align-items: center; justify-content: center;
    font-family: var(--title-font);
    font-weight: 900;
    font-size: 20px;
    border-radius: 50%;
    flex-shrink: 0;
    background: rgba(255,255,255,0.05);
    border: 2px solid var(--card-border);
    color: var(--text2);
    transition: all 0.4s ease;
}}
.rank-1 .rank-badge {{ background: linear-gradient(135deg, #ffd700, #ffaa00); color: #1a1000; border-color: #ffd700; box-shadow: 0 0 14px rgba(255,215,0,0.5); }}
.rank-2 .rank-badge {{ background: linear-gradient(135deg, #c0c0c0, #999); color: #1a1a1a; border-color: #c0c0c0; }}
.rank-3 .rank-badge {{ background: linear-gradient(135deg, #cd7f32, #a05a20); color: #1a0e00; border-color: #cd7f32; }}

/* ?? PROFILE PIC ?? */
.profile-pic {{
    width: 52px; height: 52px;
    border-radius: 50%;
    border: 3px solid var(--card-border);
    overflow: hidden;
    flex-shrink: 0;
    position: relative;
    transition: border-color 0.4s ease, box-shadow 0.4s ease;
}}
.rank-1 .profile-pic {{ border-color: var(--rank1); box-shadow: 0 0 12px rgba(255,215,0,0.3); }}
.rank-2 .profile-pic {{ border-color: var(--rank2); }}
.rank-3 .profile-pic {{ border-color: var(--rank3); }}
.profile-pic img {{
    width: 100%; height: 100%;
    object-fit: cover;
    display: block;
}}
.status-dot {{
    position: absolute;
    bottom: -1px; right: -1px;
    width: 14px; height: 14px;
    border-radius: 50%;
    border: 2px solid var(--card);
    transition: background 0.4s ease;
}}
.status-dot.alive {{ background: #00ff88; box-shadow: 0 0 8px #00ff88; }}
.status-dot.dead {{ background: #ff3278; box-shadow: none; }}

/* ?? PLAYER INFO ?? */
.player-info {{
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 3px;
}}
.player-name {{
    font-size: 20px;
    font-weight: 700;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    color: var(--text);
    transition: color 0.4s ease;
}}
.rank-1 .player-name {{ color: var(--rank1); }}
.creature-name {{
    font-size: 15px;
    color: var(--text2);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
}}

/* ?? STATS ?? */
.stats-row {{
    display: flex;
    gap: 12px;
    flex-shrink: 0;
}}
.stat {{
    display: flex;
    flex-direction: column;
    align-items: center;
    min-width: 44px;
}}
.stat-value {{
    font-family: var(--title-font);
    font-size: 20px;
    font-weight: 700;
    color: var(--accent);
    transition: color 0.3s ease;
}}
.stat-label {{
    font-size: 12px;
    color: var(--text2);
    letter-spacing: 1px;
    text-transform: uppercase;
}}

/* ?? HP BAR (below card) ?? */
.hp-bar-container {{
    margin: -2px 14px 8px 112px;
    height: 5px;
    background: rgba(255,255,255,0.06);
    border-radius: 3px;
    overflow: hidden;
}}
.hp-bar {{
    height: 100%;
    border-radius: 3px;
    background: var(--hp-bar);
    transition: width 0.8s ease, background 0.4s ease;
}}
.hp-bar.low {{
    background: var(--hp-bar-low);
}}

/* ?? EMPTY ?? */
.empty-state {{
    text-align: center;
    padding: 50px 20px;
    color: var(--text2);
    font-size: 18px;
}}
.empty-icon {{
    font-size: 44px;
    margin-bottom: 10px;
    opacity: 0.4;
}}
</style>
</head>
<body>
<div class=""overlay-container"">
    <div class=""header"">
        <h1>&#x2694;&#xFE0F; ARENA RANKING</h1>
        <div class=""header-line""></div>
        <div class=""header-sub"">CREATURE BATTLE LEADERBOARD</div>
    </div>
    <div id=""playerList""></div>
    <div id=""emptyState"" class=""empty-state"" style=""display:none"">
        <div class=""empty-icon"">&#x2694;&#xFE0F;</div>
        Waiting for combatants...
    </div>
</div>
<script>
{GetSharedUpdateScript()}

const STAT_COLS = {statCols};
const DATA_URL = '/overlay/{cfg.Id}/data';
const REFRESH = {cfg.RefreshIntervalMs};

const cardMap = new Map();

function createCard(p) {{
    const card = document.createElement('div');
    card.className = 'player-card entering';
    card.dataset.username = p.username;

    let statsHtml = '';
    if (STAT_COLS.includes('HP'))    statsHtml += '<div class=""stat""><div class=""stat-value hp-val""></div><div class=""stat-label"">HP</div></div>';
    if (STAT_COLS.includes('DMG'))   statsHtml += '<div class=""stat""><div class=""stat-value dmg-val""></div><div class=""stat-label"">DMG</div></div>';
    if (STAT_COLS.includes('KILLS')) statsHtml += '<div class=""stat""><div class=""stat-value kill-val""></div><div class=""stat-label"">KILLS</div></div>';

    card.innerHTML =
        '<div class=""rank-badge""></div>' +
        '<div class=""profile-pic"">' +
        '  <img src="""" alt="""" onerror=""this.src=\'https://minotar.net/helm/MHF_Steve/64\'"" />' +
        '  <div class=""status-dot""></div>' +
        '</div>' +
        '<div class=""player-info"">' +
        '  <div class=""player-name""></div>' +
        '  <div class=""creature-name""></div>' +
        '</div>' +
        '<div class=""stats-row"">' + statsHtml + '</div>';

    card.addEventListener('animationend', () => card.classList.remove('entering'), {{ once: true }});
    return card;
}}

function createHpBar() {{
    const container = document.createElement('div');
    container.className = 'hp-bar-container';
    container.innerHTML = '<div class=""hp-bar""></div>';
    return container;
}}

function updateCard(card, p) {{
    setRankClass(card, p.rank);
    setClass(card, 'dead', !p.alive);

    const badge = card.querySelector('.rank-badge');
    if (badge && badge.textContent !== String(p.rank)) badge.textContent = p.rank;

    const img = card.querySelector('.profile-pic img');
    if (img && img.getAttribute('src') !== p.profilePicture) img.src = p.profilePicture;

    const dot = card.querySelector('.status-dot');
    if (dot) {{ dot.classList.toggle('alive', p.alive); dot.classList.toggle('dead', !p.alive); }}

    setText(card, '.player-name', p.nickname);
    setText(card, '.creature-name', p.creature);

    const hpEl = card.querySelector('.hp-val');
    if (hpEl) hpEl.textContent = p.alive ? Math.round(p.hp) : '??';
    const dmgEl = card.querySelector('.dmg-val');
    if (dmgEl) dmgEl.textContent = p.damage;
    const killEl = card.querySelector('.kill-val');
    if (killEl) killEl.textContent = p.kills;

    const bar = card.querySelector('.hp-bar');
    if (bar) {{
        const hpPct = p.maxHp > 0 ? Math.round((p.hp / p.maxHp) * 100) : 0;
        bar.style.width = (p.alive ? hpPct : 0) + '%';
        bar.classList.toggle('low', hpPct < 30);
    }}
    const barC = card.querySelector('.hp-bar-container');
    if (barC) barC.style.display = p.alive ? '' : 'none';
}}

function renderPlayers(players) {{
    const list = document.getElementById('playerList');
    const empty = document.getElementById('emptyState');
    if (!players || players.length === 0) {{
        // Fade out remaining cards
        cardMap.forEach((els) => {{
            els.card.classList.add('removing');
            if (els.hpBar) els.hpBar.style.display = 'none';
        }});
        setTimeout(() => {{ list.innerHTML = ''; cardMap.clear(); }}, 350);
        empty.style.display = 'block';
        return;
    }}
    empty.style.display = 'none';

    const newUsernames = new Set(players.map(p => p.username));

    // Remove cards for players no longer in the list
    cardMap.forEach((els, username) => {{
        if (!newUsernames.has(username)) {{
            els.card.classList.add('removing');
            if (els.hpBar) els.hpBar.style.display = 'none';
            setTimeout(() => {{
                els.card.remove();
                if (els.hpBar) els.hpBar.remove();
                cardMap.delete(username);
            }}, 350);
        }}
    }});

    // Update or create cards in order
    let insertBefore = null;
    for (let i = players.length - 1; i >= 0; i--) {{
        const p = players[i];
        let els = cardMap.get(p.username);
        if (!els) {{
            const card = createCard(p);
            const hpBar = (STAT_COLS.includes('HP')) ? createHpBar() : null;
            els = {{ card, hpBar }};
            cardMap.set(p.username, els);
        }}

        updateCard(els.card, p);

        // Update HP bar
        if (els.hpBar) {{
            const hpPct = p.maxHp > 0 ? Math.round((p.hp / p.maxHp) * 100) : 0;
            const bar = els.hpBar.querySelector('.hp-bar');
            if (bar) {{
                bar.style.width = (p.alive ? hpPct : 0) + '%';
                bar.classList.toggle('low', hpPct < 30);
            }}
            els.hpBar.style.display = p.alive ? '' : 'none';
        }}

        // Re-order: insert at correct position
        if (els.hpBar) {{
            list.insertBefore(els.hpBar, insertBefore);
            insertBefore = els.hpBar;
        }}
        list.insertBefore(els.card, insertBefore);
        insertBefore = els.card;
    }}
}}

async function fetchData() {{
    try {{
        const r = await fetch(DATA_URL);
        const d = await r.json();
        renderPlayers(d.players);
    }} catch(e) {{ /* retry next tick */ }}
}}

fetchData();
setInterval(fetchData, REFRESH);
</script>
</body>
</html>";
    }

    // ---------------------------------------------------------------
    //   HORIZONTAL RANKING
    // ---------------------------------------------------------------

    private string GenerateHorizontalRankingHtml(OverlayConfig cfg)
    {
        var themeVars = GetThemeColors(cfg.Theme);
        var statCols = GetStatColumns(cfg);

        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<title>{WebUtility.HtmlEncode(cfg.Name)}</title>
<link href=""https://fonts.googleapis.com/css2?family=Orbitron:wght@400;700;900&family=Rajdhani:wght@400;600;700&display=swap"" rel=""stylesheet"">
<style>
* {{ margin: 0; padding: 0; box-sizing: border-box; }}
:root {{ {themeVars} }}
body {{
    background: transparent;
    font-family: 'Rajdhani', 'Segoe UI', sans-serif;
    color: var(--text);
    overflow: hidden;
}}

.overlay-container {{ display: flex; flex-direction: column; align-items: center; padding: 14px 20px; }}

.header {{
    text-align: center;
    margin-bottom: 14px;
    width: 100%;
}}
.header h1 {{
    font-family: var(--title-font);
    font-size: 24px;
    font-weight: 900;
    text-transform: uppercase;
    letter-spacing: 5px;
    background: var(--gradient);
    -webkit-background-clip: text;
    -webkit-text-fill-color: transparent;
    background-clip: text;
    filter: drop-shadow(0 0 8px var(--accent)); display: inline-block;
}}
.header-line {{
    height: 3px;
    background: var(--gradient);
    border-radius: 2px;
    margin-top: 6px;
    opacity: 0.6;
}}

/* ?? HORIZONTAL PLAYER ROW ?? */
.players-row {{
    display: flex;
    gap: 12px;
    align-items: flex-end;
    justify-content: center;
}}

.player-card {{
    display: flex;
    flex-direction: column;
    align-items: center;
    width: 180px;
    padding: 14px 10px 12px;
    background: var(--card);
    border: 1px solid var(--card-border);
    border-radius: 14px;
    position: relative;
    transition: opacity 0.4s ease, transform 0.4s ease, border-color 0.4s ease, box-shadow 0.4s ease, padding 0.4s ease;
    opacity: 1;
}}
.player-card.entering {{ animation: popIn 0.4s ease forwards; }}
.player-card.removing {{ animation: popOut 0.3s ease forwards; }}
.player-card::after {{
    content: ''; position: absolute; bottom: 0; left: 50%; transform: translateX(-50%);
    width: 60%; height: 3px; background: var(--gradient); border-radius: 2px; opacity: 0.5;
    transition: all 0.4s ease;
}}
.player-card.rank-1 {{
    box-shadow: var(--glow), inset 0 0 30px rgba(255,215,0,0.05);
    border-color: var(--rank1); transform: translateY(-8px);
    padding-top: 18px; padding-bottom: 16px;
}}
.player-card.rank-1::after {{ background: var(--rank1); width: 80%; opacity: 0.8; }}
.player-card.rank-2 {{ border-color: var(--rank2); }}
.player-card.rank-3 {{ border-color: var(--rank3); }}
.player-card.dead {{ opacity: 0.5; }}

@keyframes popIn {{
    from {{ opacity: 0; transform: translateY(15px); }}
    to {{ opacity: 1; transform: translateY(0); }}
}}
@keyframes popOut {{
    from {{ opacity: 1; }}
    to {{ opacity: 0; transform: scale(0.8); }}
}}

/* ?? RANK BADGE ?? */
.rank-badge {{
    position: absolute;
    top: -14px;
    width: 32px; height: 32px;
    display: flex; align-items: center; justify-content: center;
    font-family: var(--title-font);
    font-weight: 900;
    font-size: 16px;
    border-radius: 50%;
    background: rgba(255,255,255,0.05);
    border: 2px solid var(--card-border);
    color: var(--text2);
    z-index: 2;
    transition: all 0.4s ease;
}}
.rank-1 .rank-badge {{ background: linear-gradient(135deg, #ffd700, #ffaa00); color: #1a1000; border-color: #ffd700; box-shadow: 0 0 12px rgba(255,215,0,0.5); width: 38px; height: 38px; top: -16px; font-size: 20px; }}
.rank-2 .rank-badge {{ background: linear-gradient(135deg, #c0c0c0, #999); color: #1a1a1a; border-color: #c0c0c0; }}
.rank-3 .rank-badge {{ background: linear-gradient(135deg, #cd7f32, #a05a20); color: #1a0e00; border-color: #cd7f32; }}

/* ?? PROFILE PIC ?? */
.profile-pic {{
    width: 64px; height: 64px;
    border-radius: 50%;
    border: 3px solid var(--card-border);
    overflow: hidden;
    margin-top: 6px;
    position: relative;
    transition: all 0.4s ease;
}}
.rank-1 .profile-pic {{ width: 76px; height: 76px; border-color: var(--rank1); box-shadow: 0 0 16px rgba(255,215,0,0.3); }}
.rank-2 .profile-pic {{ border-color: var(--rank2); }}
.rank-3 .profile-pic {{ border-color: var(--rank3); }}
.profile-pic img {{
    width: 100%; height: 100%;
    object-fit: cover;
}}
.status-dot {{
    position: absolute;
    bottom: 0px; right: 0px;
    width: 14px; height: 14px;
    border-radius: 50%;
    border: 2px solid var(--card);
    transition: background 0.4s ease;
}}
.status-dot.alive {{ background: #00ff88; box-shadow: 0 0 8px #00ff88; }}
.status-dot.dead {{ background: #ff3278; box-shadow: none; }}

/* ?? PLAYER NAME ?? */
.player-name {{
    font-size: 18px;
    font-weight: 700;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    max-width: 100%;
    text-align: center;
    margin-top: 7px;
    color: var(--text);
    transition: color 0.4s ease;
}}
.rank-1 .player-name {{ color: var(--rank1); font-size: 20px; }}

/* ?? STATS ?? */
.stats-row {{
    display: flex;
    gap: 10px;
    margin-top: 8px;
    justify-content: center;
}}
.stat {{
    display: flex;
    flex-direction: column;
    align-items: center;
}}
.stat-value {{
    font-family: var(--title-font);
    font-size: 18px;
    font-weight: 700;
    color: var(--accent);
    transition: color 0.3s ease;
}}
.rank-1 .stat-value {{ font-size: 22px; }}
.stat-label {{
    font-size: 11px;
    color: var(--text2);
    letter-spacing: 1px;
    text-transform: uppercase;
}}

/* ?? HP BAR ?? */
.hp-bar-container {{
    width: 80%;
    height: 5px;
    background: rgba(255,255,255,0.06);
    border-radius: 3px;
    overflow: hidden;
    margin-top: 7px;
}}
.hp-bar {{ height: 100%; border-radius: 3px; background: var(--hp-bar); transition: width 0.8s ease, background 0.4s ease; }}
.hp-bar.low {{ background: var(--hp-bar-low); }}

/* ?? EMPTY ?? */
.empty-state {{
    text-align: center;
    padding: 50px 20px;
    color: var(--text2);
    font-size: 18px;
}}
.empty-icon {{
    font-size: 40px;
    margin-bottom: 8px;
    opacity: 0.4;
}}
</style>
</head>
<body>
<div class=""overlay-container"">
    <div class=""header"">
        <h1>&#x2694;&#xFE0F; ARENA RANKING</h1>
        <div class=""header-line""></div>
        <div class=""header-sub"">CREATURE BATTLE LEADERBOARD</div>
    </div>
    <div id=""playerList""></div>
    <div id=""emptyState"" class=""empty-state"" style=""display:none"">
        <div class=""empty-icon"">&#x2694;&#xFE0F;</div>
        Waiting for combatants...
    </div>
</div>
<script>
{GetSharedUpdateScript()}

const STAT_COLS = {statCols};
const DATA_URL = '/overlay/{cfg.Id}/data';
const REFRESH = {cfg.RefreshIntervalMs};

const cardMap = new Map();

function createCard(p) {{
    const card = document.createElement('div');
    card.className = 'player-card entering';
    card.dataset.username = p.username;

    let statsHtml = '';
    if (STAT_COLS.includes('HP'))    statsHtml += '<div class=""stat""><div class=""stat-value hp-val""></div><div class=""stat-label"">HP</div></div>';
    if (STAT_COLS.includes('DMG'))   statsHtml += '<div class=""stat""><div class=""stat-value dmg-val""></div><div class=""stat-label"">DMG</div></div>';
    if (STAT_COLS.includes('KILLS')) statsHtml += '<div class=""stat""><div class=""stat-value kill-val""></div><div class=""stat-label"">KILLS</div></div>';

    card.innerHTML =
        '<div class=""rank-badge""></div>' +
        '<div class=""profile-pic"">' +
        '  <img src="""" alt="""" onerror=""this.src=\'https://minotar.net/helm/MHF_Steve/64\'"" />' +
        '  <div class=""status-dot""></div>' +
        '</div>' +
        '<div class=""player-info"">' +
        '  <div class=""player-name""></div>' +
        '  <div class=""creature-name""></div>' +
        '</div>' +
        '<div class=""stats-row"">' + statsHtml + '</div>';

    card.addEventListener('animationend', () => card.classList.remove('entering'), {{ once: true }});
    return card;
}}

function createHpBar() {{
    const container = document.createElement('div');
    container.className = 'hp-bar-container';
    container.innerHTML = '<div class=""hp-bar""></div>';
    return container;
}}

function updateCard(card, p) {{
    setRankClass(card, p.rank);
    setClass(card, 'dead', !p.alive);

    const badge = card.querySelector('.rank-badge');
    if (badge && badge.textContent !== String(p.rank)) badge.textContent = p.rank;

    const img = card.querySelector('.profile-pic img');
    if (img && img.getAttribute('src') !== p.profilePicture) img.src = p.profilePicture;

    const dot = card.querySelector('.status-dot');
    if (dot) {{ dot.classList.toggle('alive', p.alive); dot.classList.toggle('dead', !p.alive); }}

    setText(card, '.player-name', p.nickname);
    setText(card, '.creature-name', p.creature);

    const hpEl = card.querySelector('.hp-val');
    if (hpEl) hpEl.textContent = p.alive ? Math.round(p.hp) : '??';
    const dmgEl = card.querySelector('.dmg-val');
    if (dmgEl) dmgEl.textContent = p.damage;
    const killEl = card.querySelector('.kill-val');
    if (killEl) killEl.textContent = p.kills;

    const bar = card.querySelector('.hp-bar');
    if (bar) {{
        const hpPct = p.maxHp > 0 ? Math.round((p.hp / p.maxHp) * 100) : 0;
        bar.style.width = (p.alive ? hpPct : 0) + '%';
        bar.classList.toggle('low', hpPct < 30);
    }}
    const barC = card.querySelector('.hp-bar-container');
    if (barC) barC.style.display = p.alive ? '' : 'none';
}}

function renderPlayers(players) {{
    const list = document.getElementById('playerList');
    const empty = document.getElementById('emptyState');
    if (!players || players.length === 0) {{
        // Fade out remaining cards
        cardMap.forEach((els) => {{
            els.card.classList.add('removing');
            if (els.hpBar) els.hpBar.style.display = 'none';
        }});
        setTimeout(() => {{ list.innerHTML = ''; cardMap.clear(); }}, 350);
        empty.style.display = 'block';
        return;
    }}
    empty.style.display = 'none';

    const newUsernames = new Set(players.map(p => p.username));

    // Remove cards for players no longer in the list
    cardMap.forEach((els, username) => {{
        if (!newUsernames.has(username)) {{
            els.card.classList.add('removing');
            if (els.hpBar) els.hpBar.style.display = 'none';
            setTimeout(() => {{
                els.card.remove();
                if (els.hpBar) els.hpBar.remove();
                cardMap.delete(username);
            }}, 350);
        }}
    }});

    // Update or create cards in order
    let insertBefore = null;
    for (let i = players.length - 1; i >= 0; i--) {{
        const p = players[i];
        let els = cardMap.get(p.username);
        if (!els) {{
            const card = createCard(p);
            const hpBar = (STAT_COLS.includes('HP')) ? createHpBar() : null;
            els = {{ card, hpBar }};
            cardMap.set(p.username, els);
        }}

        updateCard(els.card, p);

        // Update HP bar
        if (els.hpBar) {{
            const hpPct = p.maxHp > 0 ? Math.round((p.hp / p.maxHp) * 100) : 0;
            const bar = els.hpBar.querySelector('.hp-bar');
            if (bar) {{
                bar.style.width = (p.alive ? hpPct : 0) + '%';
                bar.classList.toggle('low', hpPct < 30);
            }}
            els.hpBar.style.display = p.alive ? '' : 'none';
        }}

        // Re-order: insert at correct position
        if (els.hpBar) {{
            list.insertBefore(els.hpBar, insertBefore);
            insertBefore = els.hpBar;
        }}
        list.insertBefore(els.card, insertBefore);
        insertBefore = els.card;
    }}
}}

async function fetchData() {{
    try {{
        const r = await fetch(DATA_URL);
        const d = await r.json();
        renderPlayers(d.players);
    }} catch(e) {{ /* retry next tick */ }}
}}

fetchData();
setInterval(fetchData, REFRESH);
</script>
</body>
</html>";
    }

    // ---------------------------------------------------------------
    //   GIFT WALL OVERLAY
    // ---------------------------------------------------------------

    private static string BuildGiftItemsJson(OverlayConfig cfg)
    {
        var giftItems = new List<string>();
        foreach (var giftName in cfg.SelectedGiftNames)
        {
            var gift = Core.TikTokGiftLibrary.FindByName(giftName);
            if (gift == null) continue;

            var safeName = WebUtility.HtmlEncode(gift.Name);
            var localImageUrl = $"/giftimage/{Uri.EscapeDataString(gift.Name)}";
            var fallbackUrl = gift.ImageUrl.Replace("\"", "\\\"");
            var textLabel = cfg.GiftTextLabels.TryGetValue(gift.Name, out var lbl) ? lbl : "";
            var safeText = WebUtility.HtmlEncode(textLabel).Replace("\"", "\\\"");
            var isFree = gift.IsFreeInteraction ? "true" : "false";
            giftItems.Add($"{{\"name\":\"{safeName}\",\"price\":{gift.CoinPrice},\"localImg\":\"{localImageUrl}\",\"remoteImg\":\"{fallbackUrl}\",\"text\":\"{safeText}\",\"isFree\":{isFree}}}");
        }
        return "[" + string.Join(",", giftItems) + "]";
    }

    private string GenerateGiftWallHtml(OverlayConfig cfg)
    {
        var themeVars = GetThemeColors(cfg.Theme);
        var giftsJson = BuildGiftItemsJson(cfg);

        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<title>{WebUtility.HtmlEncode(cfg.Name)}</title>
<link href=""https://fonts.googleapis.com/css2?family=Orbitron:wght@400;700;900&family=Rajdhani:wght@400;600;700&display=swap"" rel=""stylesheet"">
<style>
* {{ margin: 0; padding: 0; box-sizing: border-box; }}
:root {{ {themeVars} }}
body {{
    background: transparent;
    font-family: 'Rajdhani', 'Segoe UI', sans-serif;
    color: var(--text);
    overflow: hidden;
}}

.overlay-container {{
    padding: 16px;
    max-width: 800px;
}}

.header {{
    text-align: center;
    margin-bottom: 18px;
}}
.header h1 {{
    font-family: var(--title-font);
    font-size: 26px;
    font-weight: 900;
    text-transform: uppercase;
    letter-spacing: 5px;
    background: var(--gradient);
    -webkit-background-clip: text;
    -webkit-text-fill-color: transparent;
    background-clip: text;
    filter: drop-shadow(0 0 10px var(--accent));
}}
.header-line {{
    height: 3px;
    background: var(--gradient);
    border-radius: 2px;
    margin-top: 8px;
    opacity: 0.7;
}}
.header-sub {{
    font-size: 14px;
    color: var(--text2);
    letter-spacing: 4px;
    text-transform: uppercase;
    margin-top: 6px;
}}

.gifts-grid {{
    display: flex;
    flex-wrap: wrap;
    gap: 14px;
    justify-content: center;
}}

.gift-card {{
    display: flex;
    flex-direction: column;
    align-items: center;
    width: 160px;
    padding: 14px 10px 12px;
    background: var(--card);
    border: 1px solid var(--card-border);
    border-radius: 12px;
    position: relative;
    transition: transform 0.3s ease, box-shadow 0.3s ease;
    animation: fadeIn 0.4s ease forwards;
}}
.gift-card:hover {{
    transform: translateY(-3px);
    box-shadow: var(--glow);
}}
.gift-card::before {{
    content: '';
    position: absolute;
    bottom: 0; left: 50%; transform: translateX(-50%);
    width: 60%; height: 3px;
    background: var(--gradient);
    border-radius: 2px;
    opacity: 0.4;
}}

@keyframes fadeIn {{
    from {{ opacity: 0; transform: scale(0.9); }}
    to {{ opacity: 1; transform: scale(1); }}
}}

.gift-img {{
    width: 76px;
    height: 76px;
    object-fit: contain;
    border-radius: 10px;
    margin-bottom: 8px;
    filter: drop-shadow(0 3px 8px rgba(0,0,0,0.4));
    background: rgba(255,255,255,0.03);
    padding: 4px;
}}

.gift-name {{
    font-size: 18px;
    font-weight: 700;
    color: var(--text);
    text-align: center;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    max-width: 145px;
    line-height: 1.3;
}}

.gift-price {{
    display: flex;
    align-items: center;
    gap: 4px;
    margin-top: 4px;
    font-size: 15px;
    font-weight: 600;
    color: var(--accent);
}}

.gift-price.free {{
    color: var(--accent2);
    font-style: italic;
}}

.gift-text {{
    font-size: 20px;
    font-weight: 600;
    color: var(--accent2);
    text-align: center;
    margin-top: 6px;
    padding: 4px 8px;
    background: rgba(255,255,255,0.05);
    border: 1px solid var(--card-border);
    border-radius: 8px;
    max-width: 145px;
    word-wrap: break-word;
    line-height: 1.3;
}}

.empty-state {{
    text-align: center;
    padding: 50px 20px;
    color: var(--text2);
    font-size: 18px;
}}
</style>
</head>
<body>
<div class=""overlay-container"">
    <div class=""header"">
        <h1>&#x1F381; {WebUtility.HtmlEncode(cfg.Name)}</h1>
        <div class=""header-line""></div>
        <div class=""header-sub"">TIKTOK GIFTS &amp; INTERACTIONS</div>
    </div>
    <div class=""gifts-grid"" id=""giftsGrid""></div>
</div>
<script>
const GIFTS = {giftsJson};

function renderGifts() {{
    const grid = document.getElementById('giftsGrid');
    if (!GIFTS || GIFTS.length === 0) {{
        grid.innerHTML = '<div class=""empty-state"">No gifts selected</div>';
        return;
    }}

    GIFTS.forEach((g, i) => {{
        const card = document.createElement('div');
        card.className = 'gift-card';
        card.style.animationDelay = (i * 0.05) + 's';

        const img = document.createElement('img');
        img.className = 'gift-img';
        img.alt = g.name;
        img.src = g.localImg;
        img.onerror = function() {{
            if (this.src !== g.remoteImg) {{
                this.src = g.remoteImg;
            }}
        }};

        const name = document.createElement('div');
        name.className = 'gift-name';
        name.textContent = g.name;
        name.title = g.name;

        const price = document.createElement('div');
        price.className = 'gift-price';
        if (g.isFree) {{
            price.classList.add('free');
            price.textContent = 'FREE';
        }} else {{
            price.innerHTML = g.price + ' &#x1FA99;';
        }}

        card.appendChild(img);
        card.appendChild(name);
        card.appendChild(price);

        if (g.text && g.text.length > 0) {{
            const txt = document.createElement('div');
            txt.className = 'gift-text';
            txt.textContent = g.text;
            card.appendChild(txt);
        }}

        grid.appendChild(card);
    }});
}}

renderGifts();
</script>
</body>
</html>";
    }

    // ---------------------------------------------------------------
    //   VERTICAL GIFT WALL OVERLAY
    // ---------------------------------------------------------------

    private string GenerateGiftWallVerticalHtml(OverlayConfig cfg)
    {
        var themeVars = GetThemeColors(cfg.Theme);
        var giftsJson = BuildGiftItemsJson(cfg);

        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<title>{WebUtility.HtmlEncode(cfg.Name)}</title>
<link href=""https://fonts.googleapis.com/css2?family=Orbitron:wght@400;700;900&family=Rajdhani:wght@400;600;700&display=swap"" rel=""stylesheet"">
<style>
* {{ margin: 0; padding: 0; box-sizing: border-box; }}
:root {{ {themeVars} }}
body {{
    background: transparent;
    font-family: 'Rajdhani', 'Segoe UI', sans-serif;
    color: var(--text);
    overflow: hidden;
}}

.overlay-container {{
    padding: 16px;
    width: 520px;
}}

.header {{
    text-align: center;
    margin-bottom: 18px;
}}
.header h1 {{
    font-family: var(--title-font);
    font-size: 28px;
    font-weight: 900;
    text-transform: uppercase;
    letter-spacing: 5px;
    background: var(--gradient);
    -webkit-background-clip: text;
    -webkit-text-fill-color: transparent;
    background-clip: text;
    filter: drop-shadow(0 0 10px var(--accent));
}}
.header-line {{
    height: 3px;
    background: var(--gradient);
    border-radius: 2px;
    margin-top: 8px;
    opacity: 0.7;
}}
.header-sub {{
    font-size: 14px;
    color: var(--text2);
    letter-spacing: 4px;
    text-transform: uppercase;
    margin-top: 6px;
}}

.gifts-list {{
    display: flex;
    flex-direction: column;
    gap: 10px;
}}

.gift-card {{
    display: flex;
    align-items: center;
    gap: 16px;
    padding: 12px 16px;
    background: var(--card);
    border: 1px solid var(--card-border);
    border-radius: 12px;
    position: relative;
    overflow: hidden;
    transition: transform 0.3s ease, box-shadow 0.3s ease;
    animation: slideIn 0.4s ease forwards;
}}
.gift-card:hover {{
    transform: translateY(-3px);
    box-shadow: var(--glow);
}}
.gift-card::before {{
    content: '';
    position: absolute;
    top: 0; left: 0;
    width: 4px; height: 100%;
    background: var(--gradient);
    border-radius: 4px 0 0 4px;
}}

@keyframes slideIn {{
    from {{ opacity: 0; transform: translateX(-20px); }}
    to {{ opacity: 1; transform: translateX(0); }}
}}

.gift-img {{
    width: 64px;
    height: 64px;
    object-fit: contain;
    border-radius: 10px;
    flex-shrink: 0;
    filter: drop-shadow(0 3px 8px rgba(0,0,0,0.4));
    background: rgba(255,255,255,0.03);
    padding: 4px;
}}

.gift-info {{
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 3px;
}}

.gift-name {{
    font-size: 20px;
    font-weight: 700;
    color: var(--text);
    text-align: center;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    max-width: 145px;
    line-height: 1.3;
}}

.gift-price {{
    display: flex;
    align-items: center;
    gap: 4px;
    margin-top: 4px;
    font-size: 16px;
    font-weight: 600;
    color: var(--accent);
}}

.gift-price.free {{
    color: var(--accent2);
    font-style: italic;
}}

.gift-text {{
    font-size: 20px;
    font-weight: 600;
    color: var(--accent2);
    text-align: center;
    margin-top: 6px;
    padding: 5px 12px;
    background: rgba(255,255,255,0.04);
    border: 1px solid var(--card-border);
    border-radius: 8px;
    flex-shrink: 0;
    max-width: 180px;
    text-align: center;
    word-wrap: break-word;
    line-height: 1.3;
}}

.empty-state {{
    text-align: center;
    padding: 50px 20px;
    color: var(--text2);
    font-size: 18px;
}}
.empty-icon {{
    font-size: 44px;
    margin-bottom: 10px;
    opacity: 0.4;
}}
</style>
</head>
<body>
<div class=""overlay-container"">
    <div class=""header"">
        <h1>&#x1F381; {WebUtility.HtmlEncode(cfg.Name)}</h1>
        <div class=""header-line""></div>
        <div class=""header-sub"">TIKTOK GIFTS &amp; INTERACTIONS</div>
    </div>
    <div class=""gifts-list"" id=""giftsList""></div>
    <div id=""emptyState"" class=""empty-state"" style=""display:none"">
        <div class=""empty-icon"">&#x1F381;</div>
        No gifts selected
    </div>
</div>
<script>
const GIFTS = {giftsJson};

function renderGifts() {{
    const list = document.getElementById('giftsList');
    const empty = document.getElementById('emptyState');
    list.innerHTML = '';

    if (!GIFTS || GIFTS.length === 0) {{
        empty.style.display = 'block';
        return;
    }}
    empty.style.display = 'none';

    GIFTS.forEach((g, i) => {{
        const card = document.createElement('div');
        card.className = 'gift-card';
        card.style.animationDelay = (i * 0.06) + 's';

        const img = document.createElement('img');
        img.className = 'gift-img';
        img.alt = g.name;
        img.src = g.localImg;
        img.onerror = function() {{
            if (this.src !== g.remoteImg) {{
                this.src = g.remoteImg;
            }}
        }};

        const info = document.createElement('div');
        info.className = 'gift-info';

        const name = document.createElement('div');
        name.className = 'gift-name';
        name.textContent = g.name;
        name.title = g.name;

        const price = document.createElement('div');
        price.className = 'gift-price';
        if (g.isFree) {{
            price.classList.add('free');
            price.textContent = 'FREE';
        }} else {{
            price.innerHTML = g.price + ' &#x1FA99;';
        }}

        info.appendChild(name);
        info.appendChild(price);

        card.appendChild(img);
        card.appendChild(info);

        if (g.text && g.text.length > 0) {{
            const txt = document.createElement('div');
            txt.className = 'gift-text';
            txt.textContent = g.text;
            card.appendChild(txt);
        }}

        list.appendChild(card);
    }});
}}

renderGifts();
</script>
</body>
</html>";
    }

    // ---------------------------------------------------------------
    //   LIKES RANKING VERTICAL
    // ---------------------------------------------------------------

    private string GenerateLikesRankingVerticalHtml(OverlayConfig cfg)
    {
        var themeVars = GetThemeColors(cfg.Theme);

        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<title>{WebUtility.HtmlEncode(cfg.Name)}</title>
<link href=""https://fonts.googleapis.com/css2?family=Orbitron:wght@400;700;900&family=Rajdhani:wght@400;600;700&family=Cinzel:wght@400;700;900&display=swap"" rel=""stylesheet"">
<style>
* {{ margin: 0; padding: 0; box-sizing: border-box; }}
:root {{ {themeVars} }}
body {{ background: transparent; font-family: 'Rajdhani', 'Segoe UI', sans-serif; color: var(--text); overflow: hidden; }}
.overlay-container {{ width: 380px; padding: 16px; }}
.header {{ text-align: center; margin-bottom: 18px; }}
.header h1 {{ font-family: var(--title-font); font-size: 24px; font-weight: 900; text-transform: uppercase; letter-spacing: 4px; background: var(--gradient); -webkit-background-clip: text; -webkit-text-fill-color: transparent; background-clip: text; filter: drop-shadow(0 0 10px var(--accent)); }}
.header-line {{ height: 3px; background: var(--gradient); border-radius: 2px; margin-top: 8px; opacity: 0.7; }}
.header-sub {{ font-size: 13px; color: var(--text2); letter-spacing: 3px; text-transform: uppercase; margin-top: 6px; }}
.player-card {{ display: flex; align-items: center; gap: 12px; padding: 10px 12px; margin-bottom: 8px; background: var(--card); border: 1px solid var(--card-border); border-radius: 12px; position: relative; overflow: hidden; transition: all 0.4s ease; }}
.player-card.entering {{ animation: slideIn 0.4s ease forwards; }}
.player-card.removing {{ animation: slideOut 0.3s ease forwards; }}
.player-card::before {{ content: ''; position: absolute; top: 0; left: 0; width: 4px; height: 100%; background: var(--gradient); border-radius: 4px 0 0 4px; }}
.player-card.rank-1 {{ box-shadow: var(--glow), inset 0 0 30px rgba(255,215,0,0.05); border-color: var(--rank1); }}
.player-card.rank-1::before {{ background: var(--rank1); }}
.player-card.rank-2 {{ border-color: var(--rank2); }}
.player-card.rank-2::before {{ background: var(--rank2); }}
.player-card.rank-3 {{ border-color: var(--rank3); }}
.player-card.rank-3::before {{ background: var(--rank3); }}
@keyframes slideIn {{ from {{ opacity: 0; transform: translateX(-20px); }} to {{ opacity: 1; transform: translateX(0); }} }}
@keyframes slideOut {{ from {{ opacity: 1; transform: translateX(0); }} to {{ opacity: 0; transform: translateX(20px); }} }}
.rank-badge {{ width: 38px; height: 38px; display: flex; align-items: center; justify-content: center; font-family: var(--title-font); font-weight: 900; font-size: 18px; border-radius: 50%; flex-shrink: 0; background: rgba(255,255,255,0.05); border: 2px solid var(--card-border); color: var(--text2); }}
.rank-1 .rank-badge {{ background: linear-gradient(135deg, #ffd700, #ffaa00); color: #1a1000; border-color: #ffd700; box-shadow: 0 0 14px rgba(255,215,0,0.5); }}
.rank-2 .rank-badge {{ background: linear-gradient(135deg, #c0c0c0, #999); color: #1a1a1a; border-color: #c0c0c0; }}
.rank-3 .rank-badge {{ background: linear-gradient(135deg, #cd7f32, #a05a20); color: #1a0e00; border-color: #cd7f32; }}
.profile-pic {{ width: 48px; height: 48px; border-radius: 50%; border: 3px solid var(--card-border); overflow: hidden; flex-shrink: 0; }}
.rank-1 .profile-pic {{ border-color: var(--rank1); box-shadow: 0 0 12px rgba(255,215,0,0.3); }}
.rank-2 .profile-pic {{ border-color: var(--rank2); }}
.rank-3 .profile-pic {{ border-color: var(--rank3); }}
.profile-pic img {{ width: 100%; height: 100%; object-fit: cover; display: block; }}
.player-info {{ flex: 1; min-width: 0; }}
.player-name {{ font-size: 18px; font-weight: 700; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; color: var(--text); }}
.rank-1 .player-name {{ color: var(--rank1); }}
.like-count {{ font-family: var(--title-font); font-size: 22px; font-weight: 700; color: var(--accent); flex-shrink: 0; display: flex; align-items: center; gap: 4px; }}
.like-icon {{ font-size: 18px; }}
.empty-state {{ text-align: center; padding: 40px 20px; color: var(--text2); font-size: 16px; }}
.empty-icon {{ font-size: 40px; margin-bottom: 8px; opacity: 0.4; }}
</style>
</head>
<body>
<div class=""overlay-container"">
    <div class=""header"">
        <h1>&#x2764;&#xFE0F; LIKES RANKING</h1>
        <div class=""header-line""></div>
        <div class=""header-sub"">TOP LIKERS THIS SESSION</div>
    </div>
    <div id=""playerList""></div>
    <div id=""emptyState"" class=""empty-state"" style=""display:none"">
        <div class=""empty-icon"">&#x2764;&#xFE0F;</div>
        Waiting for likes...
    </div>
</div>
<script>
{GetSharedUpdateScript()}
const DATA_URL = '/overlay/{cfg.Id}/data';
const REFRESH = {cfg.RefreshIntervalMs};
const cardMap = new Map();

function fmt(n) {{ if (n >= 1000000) return (n/1000000).toFixed(1)+'M'; if (n >= 1000) return (n/1000).toFixed(1)+'K'; return String(n); }}

function createCard(p) {{
    const card = document.createElement('div');
    card.className = 'player-card entering';
    card.dataset.username = p.username;
    card.innerHTML = '<div class=""rank-badge""></div><div class=""profile-pic""><img src="""" onerror=""this.src=\'https://minotar.net/helm/MHF_Steve/64\'"" /></div><div class=""player-info""><div class=""player-name""></div></div><div class=""like-count""><span class=""like-icon"">&#x2764;&#xFE0F;</span><span class=""like-val""></span></div>';
    card.addEventListener('animationend', () => card.classList.remove('entering'), {{ once: true }});
    return card;
}}

function updateCard(card, p) {{
    setRankClass(card, p.rank);
    const badge = card.querySelector('.rank-badge');
    if (badge && badge.textContent !== String(p.rank)) badge.textContent = p.rank;
    const img = card.querySelector('.profile-pic img');
    if (img && img.getAttribute('src') !== p.profilePicture) img.src = p.profilePicture;
    setText(card, '.player-name', p.nickname);
    const likeVal = card.querySelector('.like-val');
    if (likeVal) likeVal.textContent = fmt(p.totalLikes);
}}

function renderPlayers(players) {{
    const list = document.getElementById('playerList');
    const empty = document.getElementById('emptyState');
    if (!players || players.length === 0) {{ cardMap.forEach(c => c.classList.add('removing')); setTimeout(() => {{ list.innerHTML = ''; cardMap.clear(); }}, 350); empty.style.display = 'block'; return; }}
    empty.style.display = 'none';
    const newSet = new Set(players.map(p => p.username));
    cardMap.forEach((card, u) => {{ if (!newSet.has(u)) {{ card.classList.add('removing'); setTimeout(() => {{ card.remove(); cardMap.delete(u); }}, 350); }} }});
    let ins = null;
    for (let i = players.length - 1; i >= 0; i--) {{
        const p = players[i];
        let card = cardMap.get(p.username);
        if (!card) {{ card = createCard(p); cardMap.set(p.username, card); }}
        updateCard(card, p);
        list.insertBefore(card, ins);
        ins = card;
    }}
}}

async function fetchData() {{ try {{ const r = await fetch(DATA_URL); const d = await r.json(); renderPlayers(d.players); }} catch(e) {{}} }}
fetchData();
setInterval(fetchData, REFRESH);
</script>
</body>
</html>";
    }

    // ---------------------------------------------------------------
    //   LIKES RANKING HORIZONTAL
    // ---------------------------------------------------------------

    private string GenerateLikesRankingHorizontalHtml(OverlayConfig cfg)
    {
        var themeVars = GetThemeColors(cfg.Theme);

        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<title>{WebUtility.HtmlEncode(cfg.Name)}</title>
<link href=""https://fonts.googleapis.com/css2?family=Orbitron:wght@400;700;900&family=Rajdhani:wght@400;600;700&display=swap"" rel=""stylesheet"">
<style>
* {{ margin: 0; padding: 0; box-sizing: border-box; }}
:root {{ {themeVars} }}
body {{ background: transparent; font-family: 'Rajdhani', 'Segoe UI', sans-serif; color: var(--text); overflow: hidden; }}
.overlay-container {{ display: flex; flex-direction: column; align-items: center; padding: 14px 20px; }}
.header {{ text-align: center; margin-bottom: 14px; width: 100%; }}
.header h1 {{ font-family: var(--title-font); font-size: 22px; font-weight: 900; text-transform: uppercase; letter-spacing: 4px; background: var(--gradient); -webkit-background-clip: text; -webkit-text-fill-color: transparent; background-clip: text; filter: drop-shadow(0 0 8px var(--accent)); display: inline-block; }}
.header-line {{ height: 3px; background: var(--gradient); border-radius: 2px; margin-top: 6px; opacity: 0.6; }}
.players-row {{ display: flex; gap: 12px; align-items: flex-end; justify-content: center; flex-wrap: wrap; }}
.player-card {{ display: flex; flex-direction: column; align-items: center; width: 150px; padding: 14px 10px 12px; background: var(--card); border: 1px solid var(--card-border); border-radius: 14px; position: relative; transition: all 0.4s ease; }}
.player-card.entering {{ animation: popIn 0.4s ease forwards; }}
.player-card.removing {{ animation: popOut 0.3s ease forwards; }}
.player-card::after {{ content: ''; position: absolute; bottom: 0; left: 50%; transform: translateX(-50%); width: 60%; height: 3px; background: var(--gradient); border-radius: 2px; opacity: 0.5; }}
.player-card.rank-1 {{ box-shadow: var(--glow), inset 0 0 30px rgba(255,215,0,0.05); border-color: var(--rank1); transform: translateY(-8px); padding-top: 18px; }}
.player-card.rank-1::after {{ background: var(--rank1); width: 80%; opacity: 0.8; }}
.player-card.rank-2 {{ border-color: var(--rank2); }}
.player-card.rank-3 {{ border-color: var(--rank3); }}
@keyframes popIn {{ from {{ opacity: 0; transform: translateY(15px); }} to {{ opacity: 1; transform: translateY(0); }} }}
@keyframes popOut {{ from {{ opacity: 1; }} to {{ opacity: 0; transform: scale(0.8); }} }}
.rank-badge {{ position: absolute; top: -14px; width: 30px; height: 30px; display: flex; align-items: center; justify-content: center; font-family: var(--title-font); font-weight: 900; font-size: 15px; border-radius: 50%; background: rgba(255,255,255,0.05); border: 2px solid var(--card-border); color: var(--text2); z-index: 2; }}
.rank-1 .rank-badge {{ background: linear-gradient(135deg, #ffd700, #ffaa00); color: #1a1000; border-color: #ffd700; box-shadow: 0 0 12px rgba(255,215,0,0.5); width: 36px; height: 36px; top: -16px; font-size: 18px; }}
.rank-2 .rank-badge {{ background: linear-gradient(135deg, #c0c0c0, #999); color: #1a1a1a; border-color: #c0c0c0; }}
.rank-3 .rank-badge {{ background: linear-gradient(135deg, #cd7f32, #a05a20); color: #1a0e00; border-color: #cd7f32; }}
.profile-pic {{ width: 56px; height: 56px; border-radius: 50%; border: 3px solid var(--card-border); overflow: hidden; margin-top: 6px; }}
.rank-1 .profile-pic {{ width: 68px; height: 68px; border-color: var(--rank1); box-shadow: 0 0 16px rgba(255,215,0,0.3); }}
.rank-2 .profile-pic {{ border-color: var(--rank2); }}
.rank-3 .profile-pic {{ border-color: var(--rank3); }}
.profile-pic img {{ width: 100%; height: 100%; object-fit: cover; }}
.player-name {{ font-size: 16px; font-weight: 700; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; max-width: 100%; text-align: center; margin-top: 6px; color: var(--text); }}
.rank-1 .player-name {{ color: var(--rank1); font-size: 18px; }}
.like-count {{ font-family: var(--title-font); font-size: 20px; font-weight: 700; color: var(--accent); margin-top: 4px; display: flex; align-items: center; gap: 4px; }}
.rank-1 .like-count {{ font-size: 24px; }}
.like-icon {{ font-size: 16px; }}
.empty-state {{ text-align: center; padding: 40px 20px; color: var(--text2); font-size: 16px; }}
.empty-icon {{ font-size: 40px; margin-bottom: 8px; opacity: 0.4; }}
</style>
</head>
<body>
<div class=""overlay-container"">
    <div class=""header"">
        <h1>&#x2764;&#xFE0F; LIKES RANKING</h1>
        <div class=""header-line""></div>
    </div>
    <div class=""players-row"" id=""playerList""></div>
    <div id=""emptyState"" class=""empty-state"" style=""display:none"">
        <div class=""empty-icon"">&#x2764;&#xFE0F;</div>
        Waiting for likes...
    </div>
</div>
<script>
{GetSharedUpdateScript()}
const DATA_URL = '/overlay/{cfg.Id}/data';
const REFRESH = {cfg.RefreshIntervalMs};
const cardMap = new Map();
function fmt(n) {{ if (n >= 1000000) return (n/1000000).toFixed(1)+'M'; if (n >= 1000) return (n/1000).toFixed(1)+'K'; return String(n); }}

function createCard(p) {{
    const card = document.createElement('div');
    card.className = 'player-card entering';
    card.dataset.username = p.username;
    card.innerHTML = '<div class=""rank-badge""></div><div class=""profile-pic""><img src="""" onerror=""this.src=\'https://minotar.net/helm/MHF_Steve/64\'"" /></div><div class=""player-name""></div><div class=""like-count""><span class=""like-icon"">&#x2764;&#xFE0F;</span><span class=""like-val""></span></div>';
    card.addEventListener('animationend', () => card.classList.remove('entering'), {{ once: true }});
    return card;
}}

function updateCard(card, p) {{
    setRankClass(card, p.rank);
    const badge = card.querySelector('.rank-badge');
    if (badge && badge.textContent !== String(p.rank)) badge.textContent = p.rank;
    const img = card.querySelector('.profile-pic img');
    if (img && img.getAttribute('src') !== p.profilePicture) img.src = p.profilePicture;
    setText(card, '.player-name', p.nickname);
    const likeVal = card.querySelector('.like-val');
    if (likeVal) likeVal.textContent = fmt(p.totalLikes);
}}

function renderPlayers(players) {{
    const list = document.getElementById('playerList');
    const empty = document.getElementById('emptyState');
    if (!players || players.length === 0) {{ cardMap.forEach(c => c.classList.add('removing')); setTimeout(() => {{ list.innerHTML = ''; cardMap.clear(); }}, 350); empty.style.display = 'block'; return; }}
    empty.style.display = 'none';
    const newSet = new Set(players.map(p => p.username));
    cardMap.forEach((card, u) => {{ if (!newSet.has(u)) {{ card.classList.add('removing'); setTimeout(() => {{ card.remove(); cardMap.delete(u); }}, 350); }} }});
    let ins = null;
    for (let i = players.length - 1; i >= 0; i--) {{
        const p = players[i];
        let card = cardMap.get(p.username);
        if (!card) {{ card = createCard(p); cardMap.set(p.username, card); }}
        updateCard(card, p);
        list.insertBefore(card, ins);
        ins = card;
    }}
}}

async function fetchData() {{ try {{ const r = await fetch(DATA_URL); const d = await r.json(); renderPlayers(d.players); }} catch(e) {{}} }}
fetchData();
setInterval(fetchData, REFRESH);
</script>
</body>
</html>";
    }

    // ---------------------------------------------------------------
    //   GIFT RANKING VERTICAL
    // ---------------------------------------------------------------

    private string GenerateGiftRankingVerticalHtml(OverlayConfig cfg)
    {
        var themeVars = GetThemeColors(cfg.Theme);

        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<title>{WebUtility.HtmlEncode(cfg.Name)}</title>
<link href=""https://fonts.googleapis.com/css2?family=Orbitron:wght@400;700;900&family=Rajdhani:wght@400;600;700&family=Cinzel:wght@400;700;900&display=swap"" rel=""stylesheet"">
<style>
* {{ margin: 0; padding: 0; box-sizing: border-box; }}
:root {{ {themeVars} }}
body {{ background: transparent; font-family: 'Rajdhani', 'Segoe UI', sans-serif; color: var(--text); overflow: hidden; }}
.overlay-container {{ width: 380px; padding: 16px; }}
.header {{ text-align: center; margin-bottom: 18px; }}
.header h1 {{ font-family: var(--title-font); font-size: 24px; font-weight: 900; text-transform: uppercase; letter-spacing: 4px; background: var(--gradient); -webkit-background-clip: text; -webkit-text-fill-color: transparent; background-clip: text; filter: drop-shadow(0 0 10px var(--accent)); }}
.header-line {{ height: 3px; background: var(--gradient); border-radius: 2px; margin-top: 8px; opacity: 0.7; }}
.header-sub {{ font-size: 13px; color: var(--text2); letter-spacing: 3px; text-transform: uppercase; margin-top: 6px; }}
.player-card {{ display: flex; align-items: center; gap: 12px; padding: 10px 12px; margin-bottom: 8px; background: var(--card); border: 1px solid var(--card-border); border-radius: 12px; position: relative; overflow: hidden; transition: all 0.4s ease; }}
.player-card.entering {{ animation: slideIn 0.4s ease forwards; }}
.player-card.removing {{ animation: slideOut 0.3s ease forwards; }}
.player-card::before {{ content: ''; position: absolute; top: 0; left: 0; width: 4px; height: 100%; background: var(--gradient); border-radius: 4px 0 0 4px; }}
.player-card.rank-1 {{ box-shadow: var(--glow), inset 0 0 30px rgba(255,215,0,0.05); border-color: var(--rank1); }}
.player-card.rank-1::before {{ background: var(--rank1); }}
.player-card.rank-2 {{ border-color: var(--rank2); }}
.player-card.rank-2::before {{ background: var(--rank2); }}
.player-card.rank-3 {{ border-color: var(--rank3); }}
.player-card.rank-3::before {{ background: var(--rank3); }}
@keyframes slideIn {{ from {{ opacity: 0; transform: translateX(-20px); }} to {{ opacity: 1; transform: translateX(0); }} }}
@keyframes slideOut {{ from {{ opacity: 1; transform: translateX(0); }} to {{ opacity: 0; transform: translateX(20px); }} }}
.rank-badge {{ width: 38px; height: 38px; display: flex; align-items: center; justify-content: center; font-family: var(--title-font); font-weight: 900; font-size: 18px; border-radius: 50%; flex-shrink: 0; background: rgba(255,255,255,0.05); border: 2px solid var(--card-border); color: var(--text2); }}
.rank-1 .rank-badge {{ background: linear-gradient(135deg, #ffd700, #ffaa00); color: #1a1000; border-color: #ffd700; box-shadow: 0 0 14px rgba(255,215,0,0.5); }}
.rank-2 .rank-badge {{ background: linear-gradient(135deg, #c0c0c0, #999); color: #1a1a1a; border-color: #c0c0c0; }}
.rank-3 .rank-badge {{ background: linear-gradient(135deg, #cd7f32, #a05a20); color: #1a0e00; border-color: #cd7f32; }}
.profile-pic {{ width: 48px; height: 48px; border-radius: 50%; border: 3px solid var(--card-border); overflow: hidden; flex-shrink: 0; }}
.rank-1 .profile-pic {{ border-color: var(--rank1); box-shadow: 0 0 12px rgba(255,215,0,0.3); }}
.rank-2 .profile-pic {{ border-color: var(--rank2); }}
.rank-3 .profile-pic {{ border-color: var(--rank3); }}
.profile-pic img {{ width: 100%; height: 100%; object-fit: cover; display: block; }}
.player-info {{ flex: 1; min-width: 0; }}
.player-name {{ font-size: 18px; font-weight: 700; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; color: var(--text); }}
.rank-1 .player-name {{ color: var(--rank1); }}
.gift-sub {{ font-size: 13px; color: var(--text2); }}
.coin-count {{ font-family: var(--title-font); font-size: 20px; font-weight: 700; color: var(--accent); flex-shrink: 0; display: flex; align-items: center; gap: 4px; }}
.coin-icon {{ font-size: 16px; }}
.empty-state {{ text-align: center; padding: 40px 20px; color: var(--text2); font-size: 16px; }}
.empty-icon {{ font-size: 40px; margin-bottom: 8px; opacity: 0.4; }}
</style>
</head>
<body>
<div class=""overlay-container"">
    <div class=""header"">
        <h1>&#x1F381; GIFT RANKING</h1>
        <div class=""header-line""></div>
        <div class=""header-sub"">TOP GIFTERS THIS SESSION</div>
    </div>
    <div id=""playerList""></div>
    <div id=""emptyState"" class=""empty-state"" style=""display:none"">
        <div class=""empty-icon"">&#x1F381;</div>
        Waiting for gifts...
    </div>
</div>
<script>
{GetSharedUpdateScript()}
const DATA_URL = '/overlay/{cfg.Id}/data';
const REFRESH = {cfg.RefreshIntervalMs};
const cardMap = new Map();

function fmt(n) {{ if (n >= 1000000) return (n/1000000).toFixed(1)+'M'; if (n >= 1000) return (n/1000).toFixed(1)+'K'; return String(n); }}

function createCard(p) {{
    const card = document.createElement('div');
    card.className = 'player-card entering';
    card.dataset.username = p.username;
    card.innerHTML = '<div class=""rank-badge""></div><div class=""profile-pic""><img src="""" onerror=""this.src=\'https://minotar.net/helm/MHF_Steve/64\'"" /></div><div class=""player-info""><div class=""player-name""></div><div class=""gift-sub""></div></div><div class=""coin-count""><span class=""coin-val""></span><span class=""coin-icon"">&#x1FA99;</span></div>';
    card.addEventListener('animationend', () => card.classList.remove('entering'), {{ once: true }});
    return card;
}}

function updateCard(card, p) {{
    setRankClass(card, p.rank);
    const badge = card.querySelector('.rank-badge');
    if (badge && badge.textContent !== String(p.rank)) badge.textContent = p.rank;
    const img = card.querySelector('.profile-pic img');
    if (img && img.getAttribute('src') !== p.profilePicture) img.src = p.profilePicture;
    setText(card, '.player-name', p.nickname);
    setText(card, '.gift-sub', p.giftCount + ' gift' + (p.giftCount !== 1 ? 's' : ''));
    const coinVal = card.querySelector('.coin-val');
    if (coinVal) coinVal.textContent = fmt(p.totalCoins);
}}

function renderPlayers(players) {{
    const list = document.getElementById('playerList');
    const empty = document.getElementById('emptyState');
    if (!players || players.length === 0) {{ cardMap.forEach(c => c.classList.add('removing')); setTimeout(() => {{ list.innerHTML = ''; cardMap.clear(); }}, 350); empty.style.display = 'block'; return; }}
    empty.style.display = 'none';
    const newSet = new Set(players.map(p => p.username));
    cardMap.forEach((card, u) => {{ if (!newSet.has(u)) {{ card.classList.add('removing'); setTimeout(() => {{ card.remove(); cardMap.delete(u); }}, 350); }} }});
    let ins = null;
    for (let i = players.length - 1; i >= 0; i--) {{
        const p = players[i];
        let card = cardMap.get(p.username);
        if (!card) {{ card = createCard(p); cardMap.set(p.username, card); }}
        updateCard(card, p);
        list.insertBefore(card, ins);
        ins = card;
    }}
}}

async function fetchData() {{ try {{ const r = await fetch(DATA_URL); const d = await r.json(); renderPlayers(d.players); }} catch(e) {{}} }}
fetchData();
setInterval(fetchData, REFRESH);
</script>
</body>
</html>";
    }

    // ---------------------------------------------------------------
    //   GIFT RANKING HORIZONTAL
    // ---------------------------------------------------------------

    private string GenerateGiftRankingHorizontalHtml(OverlayConfig cfg)
    {
        var themeVars = GetThemeColors(cfg.Theme);

        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<title>{WebUtility.HtmlEncode(cfg.Name)}</title>
<link href=""https://fonts.googleapis.com/css2?family=Orbitron:wght@400;700;900&family=Rajdhani:wght@400;600;700&display=swap"" rel=""stylesheet"">
<style>
* {{ margin: 0; padding: 0; box-sizing: border-box; }}
:root {{ {themeVars} }}
body {{ background: transparent; font-family: 'Rajdhani', 'Segoe UI', sans-serif; color: var(--text); overflow: hidden; }}
.overlay-container {{ display: flex; flex-direction: column; align-items: center; padding: 14px 20px; }}
.header {{ text-align: center; margin-bottom: 14px; width: 100%; }}
.header h1 {{ font-family: var(--title-font); font-size: 22px; font-weight: 900; text-transform: uppercase; letter-spacing: 4px; background: var(--gradient); -webkit-background-clip: text; -webkit-text-fill-color: transparent; background-clip: text; filter: drop-shadow(0 0 8px var(--accent)); display: inline-block; }}
.header-line {{ height: 3px; background: var(--gradient); border-radius: 2px; margin-top: 6px; opacity: 0.6; }}
.players-row {{ display: flex; gap: 12px; align-items: flex-end; justify-content: center; flex-wrap: wrap; }}
.player-card {{ display: flex; flex-direction: column; align-items: center; width: 150px; padding: 14px 10px 12px; background: var(--card); border: 1px solid var(--card-border); border-radius: 14px; position: relative; transition: all 0.4s ease; }}
.player-card.entering {{ animation: popIn 0.4s ease forwards; }}
.player-card.removing {{ animation: popOut 0.3s ease forwards; }}
.player-card::after {{ content: ''; position: absolute; bottom: 0; left: 50%; transform: translateX(-50%); width: 60%; height: 3px; background: var(--gradient); border-radius: 2px; opacity: 0.5; }}
.player-card.rank-1 {{ box-shadow: var(--glow), inset 0 0 30px rgba(255,215,0,0.05); border-color: var(--rank1); transform: translateY(-8px); padding-top: 18px; }}
.player-card.rank-1::after {{ background: var(--rank1); width: 80%; opacity: 0.8; }}
.player-card.rank-2 {{ border-color: var(--rank2); }}
.player-card.rank-3 {{ border-color: var(--rank3); }}
@keyframes popIn {{ from {{ opacity: 0; transform: translateY(15px); }} to {{ opacity: 1; transform: translateY(0); }} }}
@keyframes popOut {{ from {{ opacity: 1; }} to {{ opacity: 0; transform: scale(0.8); }} }}
.rank-badge {{ position: absolute; top: -14px; width: 30px; height: 30px; display: flex; align-items: center; justify-content: center; font-family: var(--title-font); font-weight: 900; font-size: 15px; border-radius: 50%; background: rgba(255,255,255,0.05); border: 2px solid var(--card-border); color: var(--text2); z-index: 2; }}
.rank-1 .rank-badge {{ background: linear-gradient(135deg, #ffd700, #ffaa00); color: #1a1000; border-color: #ffd700; box-shadow: 0 0 12px rgba(255,215,0,0.5); width: 36px; height: 36px; top: -16px; font-size: 18px; }}
.rank-2 .rank-badge {{ background: linear-gradient(135deg, #c0c0c0, #999); color: #1a1a1a; border-color: #c0c0c0; }}
.rank-3 .rank-badge {{ background: linear-gradient(135deg, #cd7f32, #a05a20); color: #1a0e00; border-color: #cd7f32; }}
.profile-pic {{ width: 56px; height: 56px; border-radius: 50%; border: 3px solid var(--card-border); overflow: hidden; margin-top: 6px; }}
.rank-1 .profile-pic {{ width: 68px; height: 68px; border-color: var(--rank1); box-shadow: 0 0 16px rgba(255,215,0,0.3); }}
.rank-2 .profile-pic {{ border-color: var(--rank2); }}
.rank-3 .profile-pic {{ border-color: var(--rank3); }}
.profile-pic img {{ width: 100%; height: 100%; object-fit: cover; }}
.player-name {{ font-size: 16px; font-weight: 700; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; max-width: 100%; text-align: center; margin-top: 6px; color: var(--text); }}
.rank-1 .player-name {{ color: var(--rank1); font-size: 18px; }}
.gift-sub {{ font-size: 12px; color: var(--text2); text-align: center; }}
.coin-count {{ font-family: var(--title-font); font-size: 20px; font-weight: 700; color: var(--accent); margin-top: 4px; display: flex; align-items: center; gap: 4px; }}
.rank-1 .coin-count {{ font-size: 24px; }}
.coin-icon {{ font-size: 16px; }}
.empty-state {{ text-align: center; padding: 40px 20px; color: var(--text2); font-size: 16px; }}
.empty-icon {{ font-size: 40px; margin-bottom: 8px; opacity: 0.4; }}
</style>
</head>
<body>
<div class=""overlay-container"">
    <div class=""header"">
        <h1>&#x1F381; GIFT RANKING</h1>
        <div class=""header-line""></div>
    </div>
    <div class=""players-row"" id=""playerList""></div>
    <div id=""emptyState"" class=""empty-state"" style=""display:none"">
        <div class=""empty-icon"">&#x1F381;</div>
        Waiting for gifts...
    </div>
</div>
<script>
{GetSharedUpdateScript()}
const DATA_URL = '/overlay/{cfg.Id}/data';
const REFRESH = {cfg.RefreshIntervalMs};
const cardMap = new Map();
function fmt(n) {{ if (n >= 1000000) return (n/1000000).toFixed(1)+'M'; if (n >= 1000) return (n/1000).toFixed(1)+'K'; return String(n); }}

function createCard(p) {{
    const card = document.createElement('div');
    card.className = 'player-card entering';
    card.dataset.username = p.username;
    card.innerHTML = '<div class=""rank-badge""></div><div class=""profile-pic""><img src="""" onerror=""this.src=\'https://minotar.net/helm/MHF_Steve/64\'"" /></div><div class=""player-name""></div><div class=""gift-sub""></div><div class=""coin-count""><span class=""coin-val""></span><span class=""coin-icon"">&#x1FA99;</span></div>';
    card.addEventListener('animationend', () => card.classList.remove('entering'), {{ once: true }});
    return card;
}}

function updateCard(card, p) {{
    setRankClass(card, p.rank);
    const badge = card.querySelector('.rank-badge');
    if (badge && badge.textContent !== String(p.rank)) badge.textContent = p.rank;
    const img = card.querySelector('.profile-pic img');
    if (img && img.getAttribute('src') !== p.profilePicture) img.src = p.profilePicture;
    setText(card, '.player-name', p.nickname);
    setText(card, '.gift-sub', p.giftCount + ' gift' + (p.giftCount !== 1 ? 's' : ''));
    const coinVal = card.querySelector('.coin-val');
    if (coinVal) coinVal.textContent = fmt(p.totalCoins);
}}

function renderPlayers(players) {{
    const list = document.getElementById('playerList');
    const empty = document.getElementById('emptyState');
    if (!players || players.length === 0) {{ cardMap.forEach(c => c.classList.add('removing')); setTimeout(() => {{ list.innerHTML = ''; cardMap.clear(); }}, 350); empty.style.display = 'block'; return; }}
    empty.style.display = 'none';
    const newSet = new Set(players.map(p => p.username));
    cardMap.forEach((card, u) => {{ if (!newSet.has(u)) {{ card.classList.add('removing'); setTimeout(() => {{ card.remove(); cardMap.delete(u); }}, 350); }} }});
    let ins = null;
    for (let i = players.length - 1; i >= 0; i--) {{
        const p = players[i];
        let card = cardMap.get(p.username);
        if (!card) {{ card = createCard(p); cardMap.set(p.username, card); }}
        updateCard(card, p);
        list.insertBefore(card, ins);
        ins = card;
    }}
}}

async function fetchData() {{ try {{ const r = await fetch(DATA_URL); const d = await r.json(); renderPlayers(d.players); }} catch(e) {{}} }}
fetchData();
setInterval(fetchData, REFRESH);
</script>
</body>
</html>";
    }
}
