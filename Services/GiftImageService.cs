using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using ArcanePlayConnect.Core;
using ArcanePlayConnect.Core.Models;

namespace ArcanePlayConnect.Services;

/// <summary>
/// Downloads and caches TikTok gift images locally in the app data folder.
/// </summary>
public class GiftImageService
{
    private static readonly HttpClient _http = new();
    private static readonly string _cacheDir;

    static GiftImageService()
    {
        _cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArcanePlayConnect", "GiftImages");
        Directory.CreateDirectory(_cacheDir);
    }

    /// <summary>Gets the local cache directory for gift images.</summary>
    public static string CacheDirectory => _cacheDir;

    /// <summary>
    /// Returns the local file path for a gift image. Downloads it if not cached.
    /// </summary>
    public static async Task<string?> GetLocalImagePathAsync(TikTokGift gift)
    {
        if (!string.IsNullOrEmpty(gift.LocalImagePath) && File.Exists(gift.LocalImagePath))
            return gift.LocalImagePath;

        var safeName = SanitizeFileName(gift.Name);
        var ext = gift.ImageUrl.Contains(".png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".webp";
        var localPath = Path.Combine(_cacheDir, safeName + ext);

        if (File.Exists(localPath))
        {
            gift.LocalImagePath = localPath;
            return localPath;
        }

        try
        {
            var bytes = await _http.GetByteArrayAsync(gift.ImageUrl);
            await File.WriteAllBytesAsync(localPath, bytes);
            gift.LocalImagePath = localPath;
            return localPath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the local file path if cached, otherwise null (non-blocking).
    /// </summary>
    public static string? GetCachedImagePath(TikTokGift gift)
    {
        if (!string.IsNullOrEmpty(gift.LocalImagePath) && File.Exists(gift.LocalImagePath))
            return gift.LocalImagePath;

        var safeName = SanitizeFileName(gift.Name);
        var ext = gift.ImageUrl.Contains(".png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".webp";
        var localPath = Path.Combine(_cacheDir, safeName + ext);

        if (File.Exists(localPath))
        {
            gift.LocalImagePath = localPath;
            return localPath;
        }

        return null;
    }

    /// <summary>
    /// Downloads all gift images in the background. Fire-and-forget safe.
    /// </summary>
    public static async Task PreloadAllAsync()
    {
        foreach (var gift in TikTokGiftLibrary.All)
        {
            try
            {
                await GetLocalImagePathAsync(gift);
            }
            catch
            {
                // Silently skip failed downloads
            }
        }
    }

    /// <summary>
    /// Serves a cached gift image file to an HTTP response stream. Returns false if not found.
    /// </summary>
    public static bool TryGetImageBytes(string giftName, out byte[]? data, out string contentType)
    {
        data = null;
        contentType = "image/webp";

        var gift = TikTokGiftLibrary.FindByName(giftName);
        if (gift == null) return false;

        var path = GetCachedImagePath(gift);
        if (path == null || !File.Exists(path)) return false;

        data = File.ReadAllBytes(path);
        contentType = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            ? "image/png" : "image/webp";
        return true;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = name;
        foreach (var c in invalid)
            result = result.Replace(c, '_');
        return result;
    }
}
