using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using ArcanePlayConnect.Core.Models;

namespace ArcanePlayConnect.Services;

public class WebhookListenerService
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly LoggingService _logger;

    public bool IsRunning => _listener?.IsListening == true;

    public event Action<WebhookEvent>? EventReceived;
    public event Action? StatusChanged;

    public WebhookListenerService(LoggingService logger)
    {
        _logger = logger;
    }

    public void Start(int port)
    {
        if (IsRunning)
        {
            _logger.LogWarning("Webhook listener is already running.");
            return;
        }

        try
        {
            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
            _logger.LogInfo($"Webhook listener started on port {port}");
            StatusChanged?.Invoke();

            Task.Run(() => ListenLoop(_cts.Token));
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to start webhook listener: {ex.Message}");
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
            _logger.LogInfo("Webhook listener stopped.");
            StatusChanged?.Invoke();
        }
    }

    private async Task ListenLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener?.IsListening == true)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequest(context), token);
            }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) { break; }
            catch (Exception ex)
            {
                _logger.LogError($"Listener error: {ex.Message}");
            }
        }
    }

    private async Task HandleRequest(HttpListenerContext context)
    {
        try
        {
            if (context.Request.HttpMethod == "POST" &&
                context.Request.Url?.AbsolutePath == "/event")
            {
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();

                // Log raw body to Webhook category (visible when Webhook filter is on)
                _logger.LogInfo($"RAW: {body}", LogCategory.Webhook);

                try
                {
                    var webhookEvent = ParseTikFinityPayload(body, context.Request.ContentType ?? string.Empty);
                    if (webhookEvent != null)
                        EventReceived?.Invoke(webhookEvent);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to parse webhook payload: {ex.Message}", LogCategory.Webhook);
                }

                context.Response.StatusCode = 200;
                var responseBytes = Encoding.UTF8.GetBytes("{\"status\":\"ok\"}");
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(responseBytes);
            }
            else
            {
                context.Response.StatusCode = 404;
            }

            context.Response.Close();
        }
        catch (Exception ex)
        {
            _logger.LogError($"Request handling error: {ex.Message}", LogCategory.Webhook);
        }
    }

    /// <summary>
    /// Parses a TikFinity webhook payload — handles both form-encoded and JSON bodies.
    /// Returns a flat string?string dictionary of all fields present.
    /// </summary>
    private static Dictionary<string, string> ExtractFields(string body, string contentType)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var isJson = contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase)
                     || (body.TrimStart().StartsWith('{') && body.TrimStart().StartsWith('{'));

        if (isJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    fields[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString() ?? string.Empty
                        : prop.Value.ToString();
                }
            }
            catch { /* fall through to form-encoded */ }
        }

        if (fields.Count == 0)
        {
            // Form-encoded (application/x-www-form-urlencoded)
            var parsed = HttpUtility.ParseQueryString(body);
            foreach (string? key in parsed.Keys)
            {
                if (key != null)
                    fields[key] = parsed[key] ?? string.Empty;
            }
        }

        return fields;
    }

    private WebhookEvent? ParseTikFinityPayload(string body, string contentType)
    {
        var f = ExtractFields(body, contentType);

        // Log all parsed field keys to System so they're always visible for debugging
        _logger.LogInfo($"Parsed fields: [{string.Join(", ", f.Keys)}]", LogCategory.System);

        var evt = new WebhookEvent
        {
            Nickname          = Get(f, "nickname"),
            Username          = Get(f, "username", "value1"),
            ProfilePictureUrl = Get(f, "profilePictureUrl", "avatar_url"),
            FollowStatus      = TryParseInt(Get(f, "followStatus"), 0),
            IsSubscriber      = Get(f, "isSubscriber") == "1"
        };

        // ?? Gift ??????????????????????????????????????????????????????????????
        var giftName = Get(f, "giftName");
        if (!string.IsNullOrEmpty(giftName))
        {
            evt.EventType      = WebhookEventType.Gift;
            evt.GiftName       = giftName;
            evt.GiftPictureUrl = Get(f, "giftPictureUrl");
            _logger.LogInfo($"Gift event — Nickname: {evt.Nickname}, Gift: {evt.GiftName}", LogCategory.Gift);
            return evt;
        }

        // ?? Follow ????????????????????????????????????????????????????????????
        // triggerTypeId=9  ? follow/join in TikFinity
        // triggerTypeId=3  ? gift
        // triggerTypeId=7  ? like
        // triggerTypeId=11 ? chat
        var triggerTypeId = TryParseInt(Get(f, "triggerTypeId"), -1);

        // ?? Like ??????????????????????????????????????????????????????????????
        var isLike =
            triggerTypeId == 7 ||
            ContainsWord(Get(f, "content"), "LikeEvent");

        if (isLike)
        {
            evt.EventType      = WebhookEventType.Like;
            evt.LikeCount      = TryParseInt(Get(f, "likeCount"), 0);
            evt.TotalLikeCount = TryParseInt(Get(f, "totalLikeCount"), 0);
            _logger.LogInfo($"Like event — Nickname: {evt.Nickname}, Likes: {evt.LikeCount} (Total: {evt.TotalLikeCount})", LogCategory.Like);
            return evt;
        }

        var isFollow =
            triggerTypeId == 9                              ||  // TikFinity follow trigger ID
            ContainsWord(Get(f, "value2"),  "follow")      ||
            ContainsWord(Get(f, "type"),    "follow")      ||
            ContainsWord(Get(f, "action"),  "follow")      ||
            ContainsWord(Get(f, "event"),   "follow")      ||
            ContainsWord(Get(f, "event"),   "member")      ||
            ContainsWord(Get(f, "value2"),  "member")      ||
            ContainsWord(Get(f, "type"),    "member");

        if (isFollow)
        {
            evt.EventType = WebhookEventType.Follow;
            _logger.LogInfo($"Follow event — Nickname: {evt.Nickname}", LogCategory.Follow);
            return evt;
        }

        // ?? Chat ??????????????????????????????????????????????????????????????
        var content = Get(f, "content", "comment", "message");
        if (!string.IsNullOrEmpty(content))
        {
            evt.EventType = WebhookEventType.Chat;
            evt.Comment   = content;
            _logger.LogInfo($"Chat event — {evt.Nickname}: {evt.Comment}", LogCategory.Chat);
            return evt;
        }

        _logger.LogWarning($"Unknown event — fields: [{string.Join(", ", f.Keys)}]", LogCategory.System);
        return null;
    }

    // ?? Helpers ???????????????????????????????????????????????????????????????

    private static string Get(Dictionary<string, string> f, params string[] keys)
    {
        foreach (var key in keys)
            if (f.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v))
                return v;
        return string.Empty;
    }

    private static bool ContainsWord(string value, string word) =>
        value.Contains(word, StringComparison.OrdinalIgnoreCase);

    private static int TryParseInt(string? value, int fallback) =>
        int.TryParse(value, out var result) ? result : fallback;
}