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
/// Pushes overlay data to Cloudflare Pages Functions relay endpoint.
/// This enables cloud overlays to work without port forwarding - the app
/// pushes data to Cloudflare, and the overlay pages read from the same origin.
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
        if (string.IsNullOrEmpty(config.CloudflareBaseUrl) || string.IsNullOrEmpty(config.StreamerId))
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
        }
    }

    /// <summary>
    /// Stops all push tasks.
    /// </summary>
    public void StopAll()
    {
        _cts?.Cancel();
        _pushTasks.Clear();
        _cts = null;
        _logger.LogInfo("[CloudRelay] All push tasks stopped.", LogCategory.System);
        StatusChanged?.Invoke();
    }

    private async Task PushLoopAsync(OverlayConfig config, CancellationToken token)
    {
        var pushUrl = BuildPushUrl(config);
        var pushToken = GetOrCreatePushToken(config.StreamerId);

        _logger.LogInfo($"[CloudRelay] Push URL: {pushUrl}", LogCategory.System);

        while (!token.IsCancellationRequested)
        {
            try
            {
                var json = BuildJsonData(config);
                if (json != null)
                {
                    await PushDataAsync(pushUrl, json, pushToken, token);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[CloudRelay] Push error for '{config.Name}': {ex.Message}", LogCategory.System);
            }

            try
            {
                await Task.Delay(config.RefreshIntervalMs, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private bool _loggedFirstSuccess = false;

    private async Task PushDataAsync(string url, string json, string pushToken, CancellationToken token)
    {
        // Use POST instead of PUT - Cloudflare Pages Functions route POST more
        // reliably than PUT, and many CDN/proxy layers block or strip PUT requests.
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation("X-Push-Token", pushToken);

        using var response = await _httpClient.SendAsync(request, token);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(token);
            _logger.LogWarning($"[CloudRelay] POST {url} returned {(int)response.StatusCode} {response.StatusCode}: {body}", LogCategory.System);
        }
        else if (!_loggedFirstSuccess)
        {
            _loggedFirstSuccess = true;
            _logger.LogInfo($"[CloudRelay] Push OK - data is being relayed to Cloudflare.", LogCategory.System);
        }
    }

    private string? BuildJsonData(OverlayConfig config)
    {
        // Re-use the same JSON building logic from OverlayServerService
        // by fetching from the local server endpoint
        try
        {
            // Build JSON directly using the same methods as OverlayServerService
            return _overlayServer.BuildOverlayJson(config);
        }
        catch
        {
            return null;
        }
    }

    private static string BuildPushUrl(OverlayConfig config)
    {
        var baseUrl = config.CloudflareBaseUrl.TrimEnd('/');
        return $"{baseUrl}/api/data/{Uri.EscapeDataString(config.StreamerId)}/{Uri.EscapeDataString(config.Id)}";
    }

    private string GetOrCreatePushToken(string streamerId)
    {
        return _pushTokens.GetOrAdd(streamerId, sid =>
        {
            // Generate a deterministic token from streamerId + a machine-specific seed
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
