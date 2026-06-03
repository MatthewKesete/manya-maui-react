namespace ManyaApp.Services;

/// <summary>
/// Key-value store backed by MAUI Preferences (Android SharedPreferences).
/// Implements window.ManyaBackend.kv.* — all methods are SYNCHRONOUS.
/// Matches web localStorage behavior expected by storageFacade.js.
/// </summary>
public class KvService
{
    // ── Core KV operations ────────────────────────────────────────────────────
    public string? Get(string key)
    {
        return Preferences.Default.Get<string?>(key, null);
    }

    public void Set(string key, string value)
    {
        Preferences.Default.Set(key, value);
    }

    public void Remove(string key)
    {
        Preferences.Default.Remove(key);
    }

    public void Clear()
    {
        Preferences.Default.Clear();
    }

    // ── Typed helpers (for internal use only) ─────────────────────────────────
    public string? GetString(string key, string? defaultValue = null)
        => Preferences.Default.Get(key, defaultValue);

    public void SetString(string key, string value)
        => Preferences.Default.Set(key, value);
}
