using System.Text.Json;

namespace ManyaApp.Services;

/// <summary>
/// Asset and file reading service.
/// Implements window.ManyaBackend.files.* contract.
///
/// Content priority:
///   1. FileSystem.AppDataDirectory/content/{path}  (downloaded/updated files)
///   2. Bundled APK assets at Resources/Raw/content/{path}  (always available offline)
///
/// getAssetUrl() returns "file:///android_asset/{path}" — Android WebView can load these directly.
/// </summary>
public class FileService
{
    // ── readJson(path) ────────────────────────────────────────────────────────
    public async Task<object?> ReadJsonAsync(string path)
    {
        var stream = await OpenContentStreamAsync(path);
        if (stream == null) throw new FileNotFoundException($"Content file not found: {path}");

        using (stream)
        {
            var doc = await JsonDocument.ParseAsync(stream);
            // Return as JsonElement which JsonSerializer will handle
            return doc.RootElement.Clone();
        }
    }

    // ── readText(path) ────────────────────────────────────────────────────────
    public async Task<string> ReadTextAsync(string path)
    {
        var stream = await OpenContentStreamAsync(path);
        if (stream == null) throw new FileNotFoundException($"Content file not found: {path}");

        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    // ── getAssetUrl(path) — SYNCHRONOUS ──────────────────────────────────────
    /// <summary>
    /// Returns a URL the Android WebView can use to load static assets (audio, images, HTML).
    /// Android WebView serves bundled assets at "file:///android_asset/".
    /// 
    /// Examples:
    ///   "audios/correct/sfx.mp3" → "file:///android_asset/audios/correct/sfx.mp3"
    ///   "images/math_island.webp" → "file:///android_asset/images/math_island.webp"
    ///   "content/math/q1.json"   → "file:///android_asset/content/math/q1.json"
    /// </summary>
    public string GetAssetUrl(string path)
    {
        // Normalize: remove leading slash
        var cleanPath = path.TrimStart('/');
        return $"file:///android_asset/{cleanPath}";
    }

    // ── writeJson(path, data) ─────────────────────────────────────────────────
    public async Task WriteJsonAsync(string path, object data)
    {
        var fullPath = Path.Combine(FileSystem.AppDataDirectory, "content", NormalizePath(path));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, JsonSerializer.Serialize(data));
        System.Diagnostics.Debug.WriteLine($"[FileService] ✅ Written: {fullPath}");
    }

    // ── Internal: open stream with fallback ───────────────────────────────────
    private async Task<Stream?> OpenContentStreamAsync(string path)
    {
        var normalPath = NormalizePath(path);

        // 1. Try internal storage first (for content updates)
        var localPath = Path.Combine(FileSystem.AppDataDirectory, "content", normalPath);
        if (File.Exists(localPath))
        {
            System.Diagnostics.Debug.WriteLine($"[FileService] 📂 Local: {localPath}");
            return File.OpenRead(localPath);
        }

        // 2. Fall back to bundled APK asset
        var assetPath = $"content/{normalPath}";
        try
        {
            var stream = await FileSystem.OpenAppPackageFileAsync(assetPath);
            System.Diagnostics.Debug.WriteLine($"[FileService] 📦 Asset: {assetPath}");
            return stream;
        }
        catch
        {
            // Try without "content/" prefix (e.g., curriculum-master.json is at root)
            try
            {
                var stream = await FileSystem.OpenAppPackageFileAsync(normalPath);
                System.Diagnostics.Debug.WriteLine($"[FileService] 📦 Root asset: {normalPath}");
                return stream;
            }
            catch
            {
                System.Diagnostics.Debug.WriteLine($"[FileService] ❌ Not found: {path}");
                return null;
            }
        }
    }

    private static string NormalizePath(string path) =>
        path.TrimStart('/').Replace('\\', '/');
}
