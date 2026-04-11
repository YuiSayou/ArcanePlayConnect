using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TikTokLiveSharp.Client;
using TikTokLiveSharp.Client.Config;
using TikTokLiveSharp.Events;
using TikTokLiveSharp.Events.Objects;
using ArcanePlayConnect.Core.Models;

namespace ArcanePlayConnect.Services;

public class TikTokLiveService
{
    private TikTokLiveClient? _client;
    private CancellationTokenSource? _cts;
    private readonly LoggingService _logger;
    private string _currentUsername = string.Empty;

    /// <summary>Maximum number of automatic retry attempts when the initial connection fails.</summary>
    private const int MaxRetryAttempts = 3;

    /// <summary>Base delay in seconds between retry attempts (doubles each retry).</summary>
    private const int BaseRetryDelaySec = 3;

    public bool IsConnected { get; private set; }

    /// <summary>Current viewer count in the live room.</summary>
    public int ViewerCount { get; private set; }

    /// <summary>Total likes in the live room.</summary>
    public long TotalLikes { get; private set; }

    public event Action<WebhookEvent>? EventReceived;
    public event Action? StatusChanged;
    public event Action<int>? ViewerCountUpdated;
    public event Action<long>? TotalLikesUpdated;

    public TikTokLiveService(LoggingService logger)
    {
        _logger = logger;
    }

    public async Task ConnectAsync(string tiktokUsername)
    {
        if (IsConnected)
        {
            _logger.LogWarning("TikTok Live is already connected.");
            return;
        }

        if (string.IsNullOrWhiteSpace(tiktokUsername))
        {
            _logger.LogError("TikTok username cannot be empty.");
            return;
        }

        // Strip @ prefix if provided
        tiktokUsername = tiktokUsername.TrimStart('@').Trim();
        _currentUsername = tiktokUsername;

        _logger.LogInfo($"Connecting to TikTok Live @{tiktokUsername}...", LogCategory.System);

        for (int attempt = 1; attempt <= MaxRetryAttempts; attempt++)
        {
            if (IsConnected) return;

            try
            {
                _cts?.Cancel();
                _cts?.Dispose();
                CleanupClient();
                _cts = new CancellationTokenSource();

                var settings = new ClientSettings
                {
                    RetryOnConnectionFailure = true,
                    Timeout = 30f,
                    ReconnectInterval = 5f,
                    PollingInterval = 1f,
                    PrintToConsole = false,
                    LogLevel = TikTokLiveSharp.Client.Config.LogLevel.None,
                    DownloadGiftInfo = true,
                    HandleExistingMessagesOnConnect = false,
                    CheckForUnparsedData = false,
                    PrintMessageData = false,
                    ClientLanguage = "en-US"
                };

                _client = new TikTokLiveClient(tiktokUsername, settings: settings);

                // Subscribe to events
                _client.OnChatMessage += OnChat;
                _client.OnGiftMessage += OnGift;
                _client.OnFollow += OnFollow;
                _client.OnJoin += OnJoin;
                _client.OnLike += OnLike;
                _client.OnShare += OnShare;
                _client.OnSubscribe += OnSubscribe;
                _client.OnRoomUpdate += OnRoomUpdate;
                _client.OnConnected += OnConnected;
                _client.OnDisconnected += OnDisconnected;
                _client.OnException += OnException;

                if (attempt > 1)
                    _logger.LogInfo($"Retry attempt {attempt}/{MaxRetryAttempts} for @{tiktokUsername}...", LogCategory.System);

                await _client.Start(_cts.Token);
                return; // Connected successfully
            }
            catch (Exception ex)
            {
                var errorDetail = GetDetailedErrorMessage(ex);

                // Don't retry for errors that won't resolve with retries
                if (IsNonRetryableError(ex))
                {
                    _logger.LogError($"Failed to connect to TikTok Live: {errorDetail}");
                    IsConnected = false;
                    StatusChanged?.Invoke();
                    return;
                }

                if (attempt < MaxRetryAttempts)
                {
                    var delay = BaseRetryDelaySec * (int)Math.Pow(2, attempt - 1);
                    _logger.LogWarning($"Connection attempt {attempt} failed: {errorDetail}. Retrying in {delay}s...");
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(delay), _cts?.Token ?? CancellationToken.None);
                    }
                    catch (TaskCanceledException)
                    {
                        _logger.LogInfo("Connection retry cancelled.", LogCategory.System);
                        IsConnected = false;
                        StatusChanged?.Invoke();
                        return;
                    }
                }
                else
                {
                    _logger.LogError($"Failed to connect to TikTok Live after {MaxRetryAttempts} attempts: {errorDetail}");
                    IsConnected = false;
                    StatusChanged?.Invoke();
                }
            }
        }
    }

    /// <summary>
    /// Determines whether an error is unlikely to resolve with retries.
    /// </summary>
    private static bool IsNonRetryableError(Exception ex)
    {
        var msg = ex.Message;
        // "LiveStream for HostID could not be found" means the user is simply not live
        if (msg.Contains("LiveStream") && msg.Contains("could not be found"))
            return true;
        return false;
    }

    /// <summary>
    /// Provides a user-friendly error message with actionable guidance.
    /// </summary>
    private static string GetDetailedErrorMessage(Exception ex)
    {
        var msg = ex.Message;

        if (msg.Contains("RoomId") && msg.Contains("Webpage"))
        {
            return "Could not find RoomId on TikTok page. "
                 + "This can happen if: (1) the account is not currently live, "
                 + "(2) TikTok is temporarily rate-limiting requests from your IP, "
                 + "or (3) the username is incorrect. "
                 + "Please verify the username matches your TikTok profile URL and that you are actively streaming.";
        }

        if (msg.Contains("LiveStream") && msg.Contains("could not be found"))
        {
            return "No active livestream found for this account. "
                 + "Please make sure you are currently live on TikTok before connecting.";
        }

        // Include inner exception details when available
        if (ex.InnerException != null)
            return $"{msg} ({ex.InnerException.Message})";

        return msg;
    }

    public async Task DisconnectAsync()
    {
        try
        {
            _cts?.Cancel();
            if (_client != null)
            {
                await _client.Stop();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Error during TikTok disconnect: {ex.Message}");
        }
        finally
        {
            CleanupClient();
            IsConnected = false;
            ViewerCount = 0;
            _logger.LogInfo("Disconnected from TikTok Live.");
            StatusChanged?.Invoke();
        }
    }

    private void CleanupClient()
    {
        if (_client != null)
        {
            _client.OnChatMessage -= OnChat;
            _client.OnGiftMessage -= OnGift;
            _client.OnFollow -= OnFollow;
            _client.OnJoin -= OnJoin;
            _client.OnLike -= OnLike;
            _client.OnShare -= OnShare;
            _client.OnSubscribe -= OnSubscribe;
            _client.OnRoomUpdate -= OnRoomUpdate;
            _client.OnConnected -= OnConnected;
            _client.OnDisconnected -= OnDisconnected;
            _client.OnException -= OnException;
            _client = null;
        }
        _cts?.Dispose();
        _cts = null;
    }

    // ?? Event Handlers ??????????????????????????????????????????????????

    private void OnConnected(TikTokLiveClient sender, bool e)
    {
        IsConnected = true;
        _logger.LogInfo($"Connected to TikTok Live @{_currentUsername}", LogCategory.System);
        StatusChanged?.Invoke();
    }

    private void OnDisconnected(TikTokLiveClient sender, bool e)
    {
        IsConnected = false;
        ViewerCount = 0;
        _logger.LogWarning("Disconnected from TikTok Live.", LogCategory.System);
        StatusChanged?.Invoke();
    }

    private void OnException(object? sender, Exception exception)
    {
        _logger.LogError($"TikTok Live error: {exception.Message}");
    }

    private void OnChat(TikTokLiveClient sender, Chat e)
    {
        var evt = new WebhookEvent
        {
            EventType = WebhookEventType.Chat,
            Nickname = e.Sender?.NickName ?? string.Empty,
            Username = e.Sender?.UniqueId ?? string.Empty,
            ProfilePictureUrl = GetAvatarUrl(e.Sender),
            Comment = e.Message ?? string.Empty,
            FollowStatus = (int)(e.Sender?.FollowStatus ?? 0),
            IsSubscriber = e.Sender?.Subscribe_Info != null
        };

        _logger.LogInfo($"Chat from {evt.Nickname}: {evt.Comment}", LogCategory.Chat);
        EventReceived?.Invoke(evt);
    }

    private void OnGift(TikTokLiveClient sender, GiftMessage e)
    {
        var evt = new WebhookEvent
        {
            EventType = WebhookEventType.Gift,
            Nickname = e.User?.NickName ?? string.Empty,
            Username = e.User?.UniqueId ?? string.Empty,
            ProfilePictureUrl = GetAvatarUrl(e.User),
            GiftName = e.Gift?.Name ?? $"Gift #{e.GiftId}",
            GiftPictureUrl = GetPictureUrl(e.Gift?.Image),
            GiftId = (int)e.GiftId,
            GiftDiamondCost = e.Gift?.DiamondCost ?? 0,
            GiftRepeatCount = (int)e.RepeatCount,
            GiftStreakEnd = e.StreakEnd,
            FollowStatus = (int)(e.User?.FollowStatus ?? 0),
            IsSubscriber = e.User?.Subscribe_Info != null
        };

        _logger.LogInfo($"Gift: {evt.GiftName} -{evt.GiftRepeatCount} from {evt.Nickname} ({evt.GiftDiamondCost}??)", LogCategory.Gift);
        EventReceived?.Invoke(evt);
    }

    private void OnFollow(TikTokLiveClient sender, Follow e)
    {
        var evt = new WebhookEvent
        {
            EventType = WebhookEventType.Follow,
            Nickname = e.User?.NickName ?? string.Empty,
            Username = e.User?.UniqueId ?? string.Empty,
            ProfilePictureUrl = GetAvatarUrl(e.User),
            FollowStatus = 1
        };

        _logger.LogInfo($"Follow from {evt.Nickname}", LogCategory.Follow);
        EventReceived?.Invoke(evt);
    }

    private void OnJoin(TikTokLiveClient sender, Join e)
    {
        ViewerCount = (int)e.ViewerCount;
        ViewerCountUpdated?.Invoke(ViewerCount);

        var evt = new WebhookEvent
        {
            EventType = WebhookEventType.Join,
            Nickname = e.User?.NickName ?? string.Empty,
            Username = e.User?.UniqueId ?? string.Empty,
            ProfilePictureUrl = GetAvatarUrl(e.User),
            FollowStatus = (int)(e.User?.FollowStatus ?? 0),
            IsSubscriber = e.User?.Subscribe_Info != null,
            ViewerCount = (int)e.ViewerCount
        };

        _logger.LogInfo($"Join from {evt.Nickname} (viewers: {evt.ViewerCount})", LogCategory.Join);
        EventReceived?.Invoke(evt);
    }

    private void OnLike(TikTokLiveClient sender, Like e)
    {
        TotalLikes = e.Total;
        TotalLikesUpdated?.Invoke(TotalLikes);

        var evt = new WebhookEvent
        {
            EventType = WebhookEventType.Like,
            Nickname = e.Sender?.NickName ?? string.Empty,
            Username = e.Sender?.UniqueId ?? string.Empty,
            ProfilePictureUrl = GetAvatarUrl(e.Sender),
            LikeCount = (int)e.Count,
            TotalLikeCount = (int)e.Total,
            FollowStatus = (int)(e.Sender?.FollowStatus ?? 0),
            IsSubscriber = e.Sender?.Subscribe_Info != null
        };

        _logger.LogInfo($"Like from {evt.Nickname} -{evt.LikeCount} (total: {evt.TotalLikeCount})", LogCategory.Like);
        EventReceived?.Invoke(evt);
    }

    private void OnShare(TikTokLiveClient sender, Share e)
    {
        var evt = new WebhookEvent
        {
            EventType = WebhookEventType.Share,
            Nickname = e.User?.NickName ?? string.Empty,
            Username = e.User?.UniqueId ?? string.Empty,
            ProfilePictureUrl = GetAvatarUrl(e.User),
            FollowStatus = (int)(e.User?.FollowStatus ?? 0),
            IsSubscriber = e.User?.Subscribe_Info != null
        };

        _logger.LogInfo($"Share from {evt.Nickname}", LogCategory.System);
        EventReceived?.Invoke(evt);
    }

    private void OnSubscribe(TikTokLiveClient sender, Subscribe e)
    {
        var evt = new WebhookEvent
        {
            EventType = WebhookEventType.Subscribe,
            Nickname = e.User?.NickName ?? string.Empty,
            Username = e.User?.UniqueId ?? string.Empty,
            ProfilePictureUrl = GetAvatarUrl(e.User),
            IsSubscriber = true
        };

        _logger.LogInfo($"Subscribe from {evt.Nickname}", LogCategory.Follow);
        EventReceived?.Invoke(evt);
    }

    private void OnRoomUpdate(TikTokLiveClient sender, RoomUpdate e)
    {
        // Room update doesn't directly have viewer count accessible as a simple field,
        // but we can track it from join events
    }

    // ?? Helpers ??????????????????????????????????????????????????????????

    private static string GetAvatarUrl(User? user)
    {
        if (user?.AvatarThumbnail?.Urls != null && user.AvatarThumbnail.Urls.Count > 0)
            return user.AvatarThumbnail.Urls.First();
        if (user?.AvatarMedium?.Urls != null && user.AvatarMedium.Urls.Count > 0)
            return user.AvatarMedium.Urls.First();
        return string.Empty;
    }

    private static string GetPictureUrl(Picture? picture)
    {
        if (picture?.Urls != null && picture.Urls.Count > 0)
            return picture.Urls.First();
        return string.Empty;
    }
}
