using System;
using System.Collections.Generic;
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

    /// <summary>Built-in SVG icons for special interactions that have no downloadable image.</summary>
    private static readonly Dictionary<string, string> _builtInSvgIcons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Like"] = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128" width="128" height="128">
              <defs>
                <linearGradient id="lg" x1="0" y1="0" x2="1" y2="1">
                  <stop offset="0%" stop-color="#ff3b5c"/>
                  <stop offset="100%" stop-color="#ff0050"/>
                </linearGradient>
              </defs>
              <circle cx="64" cy="64" r="60" fill="#1a1a2e"/>
              <path d="M64 108 C40 88 16 68 16 48 C16 30 30 18 46 18 C54 18 60 22 64 28 C68 22 74 18 82 18 C98 18 112 30 112 48 C112 68 88 88 64 108Z" fill="url(#lg)"/>
            </svg>
            """,
        ["Follow"] = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128" width="128" height="128">
              <defs>
                <linearGradient id="lg" x1="0" y1="0" x2="1" y2="1">
                  <stop offset="0%" stop-color="#00c8ff"/>
                  <stop offset="100%" stop-color="#b400ff"/>
                </linearGradient>
              </defs>
              <circle cx="64" cy="64" r="60" fill="#1a1a2e"/>
              <circle cx="52" cy="44" r="18" fill="url(#lg)"/>
              <path d="M22 100 C22 78 36 66 52 66 C68 66 82 78 82 100Z" fill="url(#lg)"/>
              <line x1="96" y1="56" x2="96" y2="84" stroke="#00ff88" stroke-width="7" stroke-linecap="round"/>
              <line x1="82" y1="70" x2="110" y2="70" stroke="#00ff88" stroke-width="7" stroke-linecap="round"/>
            </svg>
            """
    };

    static GiftImageService()
    {
        _cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArcanePlayConnect", "GiftImages");
        Directory.CreateDirectory(_cacheDir);

        // Pre-generate built-in SVG icons on disk
        foreach (var (name, svg) in _builtInSvgIcons)
        {
            var safeName = SanitizeFileName(name);
            var localPath = Path.Combine(_cacheDir, safeName + ".svg");
            try
            {
                File.WriteAllText(localPath, svg.Trim());
            }
            catch
            {
                // Silently ignore write failures
            }
        }
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

        // Check for built-in SVG icon first
        if (_builtInSvgIcons.ContainsKey(gift.Name))
        {
            var svgPath = Path.Combine(_cacheDir, safeName + ".svg");
            if (File.Exists(svgPath))
            {
                gift.LocalImagePath = svgPath;
                return svgPath;
            }
        }

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

        // Check for built-in SVG icon first
        if (_builtInSvgIcons.ContainsKey(gift.Name))
        {
            var svgPath = Path.Combine(_cacheDir, safeName + ".svg");
            if (File.Exists(svgPath))
            {
                gift.LocalImagePath = svgPath;
                return svgPath;
            }
        }

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
        if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            contentType = "image/svg+xml";
        else if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            contentType = "image/png";
        else
            contentType = "image/webp";
        return true;
    }

    /// <summary>
    /// Returns an inline data URI for a built-in SVG icon, or null if the gift has no built-in icon.
    /// </summary>
    public static string? GetBuiltInDataUri(string giftName)
    {
        if (_builtInSvgIcons.TryGetValue(giftName, out var svg))
        {
            var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(svg.Trim()));
            return $"data:image/svg+xml;base64,{base64}";
        }
        return null;
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
