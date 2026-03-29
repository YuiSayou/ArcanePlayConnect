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

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _port;

    private readonly ConcurrentDictionary<string, OverlayConfig> _overlays = new();

    public bool IsRunning => _listener?.IsListening == true;
    public int Port => _port;
    public event Action? StatusChanged;

    public OverlayServerService(LoggingService logger, CreatureTrackerService tracker)
    {
        _logger = logger;
        _tracker = tracker;
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

            // Route: /giftimage/{name}  ? serve cached gift image
            if (path.StartsWith("/giftimage/"))
            {
                var giftName = Uri.UnescapeDataString(path.Replace("/giftimage/", "").TrimEnd('/'));
                if (GiftImageService.TryGetImageBytes(giftName, out var imgData, out var contentType) && imgData != null)
                {
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = contentType;
                    ctx.Response.Headers.Add("Cache-Control", "public, max-age=86400");
                    await ctx.Response.OutputStream.WriteAsync(imgData);
                    ctx.Response.Close();
                    return;
                }
            }

            // Route: /overlay/{id}/data  ? JSON data
            if (path.StartsWith("/overlay/") && path.EndsWith("/data"))
            {
                var id = path.Replace("/overlay/", "").Replace("/data", "");
                if (_overlays.TryGetValue(id, out var cfg))
                {
                    await ServeJsonDataAsync(ctx, cfg);
                    return;
                }
            }
            // Route: /overlay/{id}  ? HTML page
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

    private async Task ServeJsonDataAsync(HttpListenerContext ctx, OverlayConfig cfg)
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

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
        ctx.Response.Headers.Add("Cache-Control", "no-cache");
        var bytes = Encoding.UTF8.GetBytes(json);
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private async Task ServeOverlayHtmlAsync(HttpListenerContext ctx, OverlayConfig cfg)
    {
        var html = cfg.Type switch
        {
            OverlayType.RankingVertical => GenerateVerticalRankingHtml(cfg),
            OverlayType.RankingHorizontal => GenerateHorizontalRankingHtml(cfg),
            OverlayType.GiftWall => GenerateGiftWallHtml(cfg),
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

    // ???????????????????????????????????????????????????????????????
    //  THEME / STAT HELPERS
    // ???????????????????????????????????????????????????????????????

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

    // ???????????????????????????????????????????????????????????????
    //  SHARED JS — flicker-free DOM-diffing update logic
    // ???????????????????????????????????????????????????????????????

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

    // ???????????????????????????????????????????????????????????????
    //   VERTICAL RANKING
    // ???????????????????????????????????????????????????????????????

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

.overlay-container {{ width: 340px; padding: 12px; }}

/* ?? HEADER ?? */
.header {{
    text-align: center;
    margin-bottom: 14px;
    position: relative;
}}
.header h1 {{
    font-family: var(--title-font);
    font-size: 18px;
    font-weight: 900;
    text-transform: uppercase;
    letter-spacing: 4px;
    background: var(--gradient);
    -webkit-background-clip: text;
    -webkit-text-fill-color: transparent;
    background-clip: text;
    filter: drop-shadow(0 0 8px var(--accent));
}}
.header-line {{
    height: 2px;
    background: var(--gradient);
    border-radius: 1px;
    margin-top: 6px;
    opacity: 0.7;
}}
.header-sub {{
    font-size: 10px;
    color: var(--text2);
    letter-spacing: 3px;
    text-transform: uppercase;
    margin-top: 4px;
}}

/* ?? PLAYER CARD ?? */
.player-card {{
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 8px 10px;
    margin-bottom: 6px;
    background: var(--card);
    border: 1px solid var(--card-border);
    border-radius: 10px;
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
    width: 3px; height: 100%;
    background: var(--gradient);
    border-radius: 3px 0 0 3px;
    transition: background 0.4s ease;
}}
.player-card.rank-1 {{ box-shadow: var(--glow), inset 0 0 30px rgba(255,215,0,0.05); border-color: var(--rank1); }}
.player-card.rank-1::before {{ background: var(--rank1); }}
.player-card.rank-2 {{ border-color: var(--rank2); }}
.player-card.rank-2::before {{ background: var(--rank2); }}
.player-card.rank-3 {{ border-color: var(--rank3); }}
.player-card.rank-3::before {{ background: var(--rank3); }}
.player-card.dead {{ opacity: 0.55; }}

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
    width: 28px; height: 28px;
    display: flex; align-items: center; justify-content: center;
    font-family: var(--title-font);
    font-weight: 900;
    font-size: 13px;
    border-radius: 50%;
    flex-shrink: 0;
    background: rgba(255,255,255,0.05);
    border: 2px solid var(--card-border);
    color: var(--text2);
    transition: all 0.4s ease;
}}
.rank-1 .rank-badge {{ background: linear-gradient(135deg, #ffd700, #ffaa00); color: #1a1000; border-color: #ffd700; box-shadow: 0 0 12px rgba(255,215,0,0.5); }}
.rank-2 .rank-badge {{ background: linear-gradient(135deg, #c0c0c0, #999); color: #1a1a1a; border-color: #c0c0c0; }}
.rank-3 .rank-badge {{ background: linear-gradient(135deg, #cd7f32, #a05a20); color: #1a0e00; border-color: #cd7f32; }}

/* ?? PROFILE PIC ?? */
.profile-pic {{
    width: 36px; height: 36px;
    border-radius: 50%;
    border: 2px solid var(--card-border);
    overflow: hidden;
    flex-shrink: 0;
    position: relative;
    transition: border-color 0.4s ease, box-shadow 0.4s ease;
}}
.rank-1 .profile-pic {{ border-color: var(--rank1); box-shadow: 0 0 10px rgba(255,215,0,0.3); }}
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
    width: 10px; height: 10px;
    border-radius: 50%;
    border: 2px solid var(--card);
    transition: background 0.4s ease;
}}
.status-dot.alive {{ background: #00ff88; box-shadow: 0 0 6px #00ff88; }}
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
    font-size: 13px;
    font-weight: 700;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    color: var(--text);
    transition: color 0.4s ease;
}}
.rank-1 .player-name {{ color: var(--rank1); }}
.creature-name {{
    font-size: 10px;
    color: var(--text2);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
}}

/* ?? STATS ?? */
.stats-row {{
    display: flex;
    gap: 8px;
    flex-shrink: 0;
}}
.stat {{
    display: flex;
    flex-direction: column;
    align-items: center;
    min-width: 36px;
}}
.stat-value {{
    font-family: var(--title-font);
    font-size: 13px;
    font-weight: 700;
    color: var(--accent);
    transition: color 0.3s ease;
}}
.stat-label {{
    font-size: 8px;
    color: var(--text2);
    letter-spacing: 1px;
    text-transform: uppercase;
}}

/* ?? HP BAR (below card) ?? */
.hp-bar-container {{
    margin: -2px 10px 6px 78px;
    height: 3px;
    background: rgba(255,255,255,0.06);
    border-radius: 2px;
    overflow: hidden;
}}
.hp-bar {{
    height: 100%;
    border-radius: 2px;
    background: var(--hp-bar);
    transition: width 0.8s ease, background 0.4s ease;
}}
.hp-bar.low {{
    background: var(--hp-bar-low);
}}

/* ?? EMPTY ?? */
.empty-state {{
    text-align: center;
    padding: 40px 20px;
    color: var(--text2);
    font-size: 13px;
}}
.empty-icon {{
    font-size: 32px;
    margin-bottom: 8px;
    opacity: 0.4;
}}
</style>
</head>
<body>
<div class=""overlay-container"">
    <div class=""header"">
        <h1>? ARENA RANKING</h1>
        <div class=""header-line""></div>
        <div class=""header-sub"">CREATURE BATTLE LEADERBOARD</div>
    </div>
    <div id=""playerList""></div>
    <div id=""emptyState"" class=""empty-state"" style=""display:none"">
        <div class=""empty-icon"">?</div>
        Waiting for combatants...
    </div>
</div>
<script>
{GetSharedUpdateScript()}

const STAT_COLS = {statCols};
const DATA_URL = '/overlay/{cfg.Id}/data';
const REFRESH = {cfg.RefreshIntervalMs};

// Track current cards by username for in-place updates
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

    // Remove entering animation class after it plays
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

    // Only update image src if the username changed (prevents re-fetch flicker)
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

    // ???????????????????????????????????????????????????????????????
    //   HORIZONTAL RANKING
    // ???????????????????????????????????????????????????????????????

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

.overlay-container {{ display: flex; flex-direction: column; align-items: center; padding: 10px 16px; }}

/* ?? HEADER ?? */
.header {{
    text-align: center;
    margin-bottom: 10px;
    width: 100%;
}}
.header h1 {{
    font-family: var(--title-font);
    font-size: 14px;
    font-weight: 900;
    text-transform: uppercase;
    letter-spacing: 4px;
    background: var(--gradient);
    -webkit-background-clip: text;
    -webkit-text-fill-color: transparent;
    background-clip: text;
    filter: drop-shadow(0 0 6px var(--accent)); display: inline-block;
}}
.header-line {{
    height: 2px;
    background: var(--gradient);
    border-radius: 1px;
    margin-top: 4px;
    opacity: 0.6;
}}

/* ?? HORIZONTAL PLAYER ROW ?? */
.players-row {{
    display: flex;
    gap: 8px;
    align-items: flex-end;
    justify-content: center;
}}

.player-card {{
    display: flex;
    flex-direction: column;
    align-items: center;
    width: 120px;
    padding: 10px 6px 8px;
    background: var(--card);
    border: 1px solid var(--card-border);
    border-radius: 12px;
    position: relative;
    transition: opacity 0.4s ease, transform 0.4s ease, border-color 0.4s ease, box-shadow 0.4s ease, padding 0.4s ease;
    opacity: 1;
}}
.player-card.entering {{ animation: popIn 0.4s ease forwards; }}
.player-card.removing {{ animation: popOut 0.3s ease forwards; }}
.player-card::after {{
    content: ''; position: absolute; bottom: 0; left: 50%; transform: translateX(-50%);
    width: 60%; height: 2px; background: var(--gradient); border-radius: 1px; opacity: 0.5;
    transition: all 0.4s ease;
}}
.player-card.rank-1 {{
    box-shadow: var(--glow), inset 0 0 30px rgba(255,215,0,0.05);
    border-color: var(--rank1); transform: translateY(-6px);
    padding-top: 14px; padding-bottom: 12px;
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
    top: -10px;
    width: 22px; height: 22px;
    display: flex; align-items: center; justify-content: center;
    font-family: var(--title-font);
    font-weight: 900;
    font-size: 11px;
    border-radius: 50%;
    background: rgba(255,255,255,0.05);
    border: 2px solid var(--card-border);
    color: var(--text2);
    z-index: 2;
    transition: all 0.4s ease;
}}
.rank-1 .rank-badge {{ background: linear-gradient(135deg, #ffd700, #ffaa00); color: #1a1000; border-color: #ffd700; box-shadow: 0 0 10px rgba(255,215,0,0.5); width: 26px; height: 26px; top: -12px; font-size: 13px; }}
.rank-2 .rank-badge {{ background: linear-gradient(135deg, #c0c0c0, #999); color: #1a1a1a; border-color: #c0c0c0; }}
.rank-3 .rank-badge {{ background: linear-gradient(135deg, #cd7f32, #a05a20); color: #1a0e00; border-color: #cd7f32; }}

/* ?? PROFILE PIC ?? */
.profile-pic {{
    width: 44px; height: 44px;
    border-radius: 50%;
    border: 2px solid var(--card-border);
    overflow: hidden;
    margin-top: 4px;
    position: relative;
    transition: all 0.4s ease;
}}
.rank-1 .profile-pic {{ width: 52px; height: 52px; border-color: var(--rank1); box-shadow: 0 0 14px rgba(255,215,0,0.3); }}
.rank-2 .profile-pic {{ border-color: var(--rank2); }}
.rank-3 .profile-pic {{ border-color: var(--rank3); }}
.profile-pic img {{
    width: 100%; height: 100%;
    object-fit: cover;
}}
.status-dot {{
    position: absolute;
    bottom: 0px; right: 0px;
    width: 10px; height: 10px;
    border-radius: 50%;
    border: 2px solid var(--card);
    transition: background 0.4s ease;
}}
.status-dot.alive {{ background: #00ff88; box-shadow: 0 0 6px #00ff88; }}
.status-dot.dead {{ background: #ff3278; box-shadow: none; }}

/* ?? PLAYER NAME ?? */
.player-name {{
    font-size: 12px;
    font-weight: 700;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    max-width: 100%;
    text-align: center;
    margin-top: 5px;
    color: var(--text);
    transition: color 0.4s ease;
}}
.rank-1 .player-name {{ color: var(--rank1); font-size: 13px; }}

/* ?? STATS ?? */
.stats-row {{
    display: flex;
    gap: 6px;
    margin-top: 6px;
    justify-content: center;
}}
.stat {{
    display: flex;
    flex-direction: column;
    align-items: center;
}}
.stat-value {{
    font-family: var(--title-font);
    font-size: 12px;
    font-weight: 700;
    color: var(--accent);
    transition: color 0.3s ease;
}}
.rank-1 .stat-value {{ font-size: 14px; }}
.stat-label {{
    font-size: 7px;
    color: var(--text2);
    letter-spacing: 1px;
    text-transform: uppercase;
}}

/* ?? HP BAR ?? */
.hp-bar-container {{
    width: 80%;
    height: 3px;
    background: rgba(255,255,255,0.06);
    border-radius: 2px;
    overflow: hidden;
    margin-top: 5px;
}}
.hp-bar {{ height: 100%; border-radius: 2px; background: var(--hp-bar); transition: width 0.8s ease, background 0.4s ease; }}
.hp-bar.low {{ background: var(--hp-bar-low); }}

/* ?? EMPTY ?? */
.empty-state {{
    text-align: center;
    padding: 30px 20px;
    color: var(--text2);
    font-size: 13px;
}}
.empty-icon {{ font-size: 28px; margin-bottom: 6px; opacity: 0.4; }}
</style>
</head>
<body>
<div class=""overlay-container"">
    <div class=""header"">
        <h1>? ARENA RANKING</h1>
        <div class=""header-line""></div>
    </div>
    <div class=""players-row"" id=""playerList""></div>
    <div id=""emptyState"" class=""empty-state"" style=""display:none"">
        <div class=""empty-icon"">?</div>
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

    let hpBarHtml = STAT_COLS.includes('HP')
        ? '<div class=""hp-bar-container""><div class=""hp-bar""></div></div>'
        : '';

    card.innerHTML =
        '<div class=""rank-badge""></div>' +
        '<div class=""profile-pic"">' +
        '  <img src="""" alt="""" onerror=""this.src=\'https://minotar.net/helm/MHF_Steve/64\'"" />' +
        '  <div class=""status-dot""></div>' +
        '</div>' +
        '<div class=""player-name""></div>' +
        '<div class=""creature-name""></div>' +
        '<div class=""stats-row"">' + statsHtml + '</div>' +
        hpBarHtml;

    // Remove entering animation class after it plays
    card.addEventListener('animationend', () => card.classList.remove('entering'), {{ once: true }});
    return card;
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

    // Podium reorder: [2nd, 1st, 3rd, rest...]
    let ordered = [...players];
    if (ordered.length >= 3) {{
        const first = ordered[0], second = ordered[1], third = ordered[2];
        ordered = [second, first, third, ...ordered.slice(3)];
    }} else if (ordered.length === 2) {{
        ordered = [ordered[1], ordered[0]];
    }}

    const newUsernames = new Set(players.map(p => p.username));

    // Remove departed
    cardMap.forEach((card, username) => {{
        if (!newUsernames.has(username)) {{
            card.classList.add('removing');
            setTimeout(() => {{ card.remove(); cardMap.delete(username); }}, 350);
        }}
    }});

    // Update or create, then reorder
    ordered.forEach p => {{
        let card = cardMap.get(p.username);
        if (!card) {{
            card = createCard(p);
            cardMap.set(p.username, card);
        }}
        updateCard(card, p);
        list.appendChild(card); // moves existing without re-creating
    }});
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

    // ???????????????????????????????????????????????????????????????
    //   GIFT WALL OVERLAY
    // ???????????????????????????????????????????????????????????????

    private string GenerateGiftWallHtml(OverlayConfig cfg)
    {
        var themeVars = GetThemeColors(cfg.Theme);

        // Build gift items JSON for the selected gifts
        var giftItems = new List<string>();
        foreach (var giftName in cfg.SelectedGiftNames)
        {
            var gift = Core.TikTokGiftLibrary.FindByName(giftName);
            if (gift == null) continue;

            var safeName = WebUtility.HtmlEncode(gift.Name);
            var localImageUrl = $"/giftimage/{Uri.EscapeDataString(gift.Name)}";
            var fallbackUrl = gift.ImageUrl.Replace("\"", "\\\"");
            giftItems.Add($"{{\"name\":\"{safeName}\",\"price\":{gift.CoinPrice},\"localImg\":\"{localImageUrl}\",\"remoteImg\":\"{fallbackUrl}\"}}");
        }

        var giftsJson = "[" + string.Join(",", giftItems) + "]";

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
    padding: 12px;
    max-width: 600px;
}}

.header {{
    text-align: center;
    margin-bottom: 14px;
}}
.header h1 {{
    font-family: var(--title-font);
    font-size: 16px;
    font-weight: 900;
    text-transform: uppercase;
    letter-spacing: 4px;
    background: var(--gradient);
    -webkit-background-clip: text;
    -webkit-text-fill-color: transparent;
    background-clip: text;
    filter: drop-shadow(0 0 8px var(--accent));
}}
.header-line {{
    height: 2px;
    background: var(--gradient);
    border-radius: 1px;
    margin-top: 6px;
    opacity: 0.7;
}}
.header-sub {{
    font-size: 10px;
    color: var(--text2);
    letter-spacing: 3px;
    text-transform: uppercase;
    margin-top: 4px;
}}

.gifts-grid {{
    display: flex;
    flex-wrap: wrap;
    gap: 10px;
    justify-content: center;
}}

.gift-card {{
    display: flex;
    flex-direction: column;
    align-items: center;
    width: 90px;
    padding: 10px 6px 8px;
    background: var(--card);
    border: 1px solid var(--card-border);
    border-radius: 10px;
    transition: transform 0.3s ease, box-shadow 0.3s ease;
    animation: fadeIn 0.4s ease forwards;
}}
.gift-card:hover {{
    transform: translateY(-3px);
    box-shadow: var(--glow);
}}

@keyframes fadeIn {{
    from {{ opacity: 0; transform: scale(0.9); }}
    to {{ opacity: 1; transform: scale(1); }}
}}

.gift-img {{
    width: 52px;
    height: 52px;
    object-fit: contain;
    border-radius: 6px;
    margin-bottom: 6px;
    filter: drop-shadow(0 2px 6px rgba(0,0,0,0.4));
}}

.gift-name {{
    font-size: 10px;
    font-weight: 700;
    color: var(--text);
    text-align: center;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    max-width: 80px;
    line-height: 1.2;
}}

.gift-price {{
    display: flex;
    align-items: center;
    gap: 3px;
    margin-top: 3px;
    font-size: 10px;
    font-weight: 600;
    color: var(--accent);
}}
.gift-price .coin {{
    width: 11px;
    height: 11px;
}}

.empty-state {{
    text-align: center;
    padding: 40px 20px;
    color: var(--text2);
    font-size: 13px;
}}
</style>
</head>
<body>
<div class=""overlay-container"">
    <div class=""header"">
        <h1>?? {WebUtility.HtmlEncode(cfg.Name)}</h1>
        <div class=""header-line""></div>
        <div class=""header-sub"">TIKTOK GIFTS</div>
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
        price.innerHTML = g.price + ' <span class=""coin"">??</span>';

        card.appendChild(img);
        card.appendChild(name);
        card.appendChild(price);
        grid.appendChild(card);
    }});
}}

renderGifts();
</script>
</body>
</html>";
    }
}
