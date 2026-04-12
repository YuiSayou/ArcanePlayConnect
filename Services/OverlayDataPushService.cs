using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArcanePlayConnect.Core.Models;

namespace ArcanePlayConnect.Services;

/// <summary>
/// Pushes overlay data to Cloudflare Durable Object relay endpoint.
/// This enables cloud overlays to work without port forwarding — the app
/// pushes data via HTTP POST, and the Durable Object broadcasts it instantly
/// to all connected overlay WebSocket clients.
///
/// Quota optimization (Durable Object + WebSocket architecture):
///   - Zero KV reads/writes (Durable Object holds data in memory)
///   - Overlay browsers use WebSocket (zero polling, zero function invocations)
///   - Only this service's HTTP POSTs count as Worker requests
///   - Content hash dedup: skips POST entirely when data hasn't changed
///   - Adaptive check interval: slows down local checks when data is idle
/// </summary>
public class OverlayDataPushService : IDisposable
{
    private readonly LoggingService _logger;
    private readonly OverlayServerService _overlayServer;
    private readonly HttpClient _httpClient;

    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<string, Task> _pushTasks = new();

    /// <summary>
    /// Push token for authenticating data pushes. Generated once per app instance
    /// from the StreamerId to prevent other users from overwriting data.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _pushTokens = new();

    /// <summary>
    /// Stores the last content hash per overlay to skip unchanged pushes client-side.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _lastContentHashes = new();

    /// <summary>
    /// Maximum interval between keepalive pushes when data is idle.
    /// The Durable Object stays alive as long as WebSocket clients are connected,
    /// but we send periodic pushes to keep the data fresh and handle reconnects.
    /// </summary>
    private const int KeepaliveIntervalMs = 30_000;

    public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;
    public event Action? StatusChanged;

    public OverlayDataPushService(LoggingService logger, OverlayServerService overlayServer)
    {
        _logger = logger;
        _overlayServer = overlayServer;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    /// <summary>
    /// Starts pushing data for the given overlay config to Cloudflare.
    /// </summary>
    public void StartPushing(OverlayConfig config)
    {
        if (string.IsNullOrEmpty(config.WorkerUrl) || string.IsNullOrEmpty(config.StreamerId))
            return;

        var key = $"{config.StreamerId}:{config.Id}";

        // Stop existing push for this overlay if any
        StopPushing(config.Id);

        _cts ??= new CancellationTokenSource();

        var token = _cts.Token;
        var task = Task.Run(() => PushLoopAsync(config, token), token);
        _pushTasks[key] = task;

        _logger.LogInfo($"[CloudRelay] Started pushing data for overlay '{config.Name}' ({config.Id})", LogCategory.System);
        StatusChanged?.Invoke();
    }

    /// <summary>
    /// Stops pushing data for a specific overlay.
    /// </summary>
    public void StopPushing(string overlayId)
    {
        var keysToRemove = new System.Collections.Generic.List<string>();
        foreach (var kvp in _pushTasks)
        {
            if (kvp.Key.EndsWith($":{overlayId}"))
                keysToRemove.Add(kvp.Key);
        }

        foreach (var key in keysToRemove)
        {
            _pushTasks.TryRemove(key, out _);
            _lastContentHashes.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Stops all push tasks.
    /// </summary>
    public void StopAll()
    {
        _cts?.Cancel();
        _pushTasks.Clear();
        _lastContentHashes.Clear();
        _cts = null;
        _logger.LogInfo("[CloudRelay] All push tasks stopped.", LogCategory.System);
        StatusChanged?.Invoke();
    }

    /// <summary>
    /// FNV-1a 32-bit hash for content change detection.
    /// </summary>
    private static string Fnv1aHash(string input)
    {
        uint hash = 0x811c9dc5;
        foreach (char c in input)
        {
            hash ^= c;
            hash *= 0x01000193;
        }
        return hash.ToString("x8");
    }

    /// <summary>
    /// Strips the "timestamp" field from JSON before hashing so that
    /// the hash only reflects actual data changes (not the ever-changing timestamp).
    /// </summary>
    private static string StripTimestampForHash(string json)
    {
        var idx = json.IndexOf("\"timestamp\":", StringComparison.Ordinal);
        if (idx < 0) return json;

        var endIdx = json.IndexOf(',', idx);
        if (endIdx < 0) endIdx = json.IndexOf('}', idx);
        if (endIdx < 0) return json;

        var removeEnd = endIdx;
        if (endIdx < json.Length && json[endIdx] == ',')
            removeEnd = endIdx + 1;

        return string.Concat(json.AsSpan(0, idx), json.AsSpan(removeEnd));
    }

    private async Task PushLoopAsync(OverlayConfig config, CancellationToken token)
    {
        var pushUrl = BuildPushUrl(config);
        var pushToken = GetOrCreatePushToken(config.StreamerId);
        var hashKey = $"{config.StreamerId}:{config.Id}";

        _logger.LogInfo($"[CloudRelay] Push URL: {pushUrl}", LogCategory.System);

        int consecutiveNoChange = 0;
        long lastPushTimeMs = 0;

        while (!token.IsCancellationRequested)
        {
            try
            {
                var json = BuildJsonData(config);
                if (json != null)
                {
                    var contentHash = Fnv1aHash(StripTimestampForHash(json));
                    _lastContentHashes.TryGetValue(hashKey, out var lastHash);
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                    var timeSinceLastPush = now - lastPushTimeMs;
                    var dataChanged = lastHash == null || lastHash != contentHash;

                    if (dataChanged)
                    {
                        // Data changed — push immediately, DO will broadcast to WebSocket clients
                        await PushDataAsync(pushUrl, json, pushToken, contentHash, token);
                        _lastContentHashes[hashKey] = contentHash;
                        lastPushTimeMs = now;
                        consecutiveNoChange = 0;
                    }
                    else if (timeSinceLastPush >= KeepaliveIntervalMs)
                    {
                        // Keepalive push — DO uses hash dedup so this won't broadcast
                        await PushDataAsync(pushUrl, json, pushToken, contentHash, token);
                        lastPushTimeMs = now;
                    }
                    else
                    {
                        consecutiveNoChange++;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[CloudRelay] Push error for '{config.Name}': {ex.Message}", LogCategory.System);
                consecutiveNoChange = 0;
            }

            try
            {
                var baseDelay = config.RefreshIntervalMs;
                var delay = consecutiveNoChange > 0
                    ? Math.Min(baseDelay * (1 + consecutiveNoChange / 2), KeepaliveIntervalMs)
                    : baseDelay;

                await Task.Delay((int)delay, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private bool _loggedFirstSuccess = false;

    private async Task PushDataAsync(string url, string json, string pushToken, string contentHash, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation("X-Push-Token", pushToken);
        request.Headers.TryAddWithoutValidation("X-Content-Hash", contentHash);

        using var response = await _httpClient.SendAsync(request, token);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(token);
            _logger.LogWarning($"[CloudRelay] POST {url} returned {(int)response.StatusCode} {response.StatusCode}: {body}", LogCategory.System);
        }
        else if (!_loggedFirstSuccess)
        {
            _loggedFirstSuccess = true;
            _logger.LogInfo($"[CloudRelay] Push OK — data is being relayed via Durable Object.", LogCategory.System);
        }
    }

    private string? BuildJsonData(OverlayConfig config)
    {
        try
        {
            return _overlayServer.BuildOverlayJson(config);
        }
        catch
        {
            return null;
        }
    }

    private static string BuildPushUrl(OverlayConfig config)
    {
        var workerUrl = config.WorkerUrl.TrimEnd('/');
        return $"{workerUrl}/api/data/{Uri.EscapeDataString(config.StreamerId)}/{Uri.EscapeDataString(config.Id)}";
    }

    private string GetOrCreatePushToken(string streamerId)
    {
        return _pushTokens.GetOrAdd(streamerId, sid =>
        {
            var seed = $"{sid}:{Environment.MachineName}:{Environment.UserName}";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
            return Convert.ToHexString(hash)[..32].ToLowerInvariant();
        });
    }

    public void Dispose()
    {
        StopAll();
        _httpClient.Dispose();
    }
}
