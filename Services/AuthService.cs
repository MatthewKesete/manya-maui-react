using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ManyaApp.Services;

/// <summary>
/// Authentication service using Supabase GoTrue REST API.
/// 
/// CRITICAL: This service ONLY handles authentication.
/// - signIn / signUp: ONLINE-ONLY, call Supabase /auth/v1 directly
/// - getSession: OFFLINE-CAPABLE, reads from Keystore + SharedPreferences
/// - NO local DB operations here (SeedService handles that separately)
/// 
/// Flow:
///   1. User calls signIn(email, password)
///   2. App POSTs to Supabase /auth/v1/token
///   3. On success: Store JWT in Keystore, UID in SharedPreferences
///   4. Return {uid, email} immediately
///   5. Webapp calls seedFromCloud() AFTER auth (separate step)
///
/// JWT Storage:
///   - Access token:  Android Keystore (SecureStorage)
///   - Refresh token: Android Keystore (SecureStorage)
///   - UID:           SharedPreferences (persistent across app launches)
/// </summary>
public class AuthService
{
    private readonly KvService _kv;

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public AuthService(KvService kv)
    {
        _kv = kv;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PUBLIC API — Matches window.ManyaBackend.auth contract
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sign in an existing user (ONLINE-ONLY).
    /// - Calls POST /auth/v1/token
    /// - On success: stores JWT + UID
    /// - Returns: {uid, email}
    /// 
    /// Next step: Webapp calls seedFromCloud() to populate local DB.
    /// </summary>
    public async Task<AuthResult> SignInAsync(string email, string password)
    {
        var url = $"{AppSettings.GoTrueUrl}/token?grant_type=password";
        var body = JsonSerializer.Serialize(new { email, password }, _jsonOpts);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add("apikey", AppSettings.SupabaseAnonKey);

        using var response = await _http.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync();
            throw new Exception($"Sign-in failed ({response.StatusCode}): {errBody}");
        }

        var tokenResp = await response.Content.ReadFromJsonAsync<GoTrueTokenResponse>(_jsonOpts)
                        ?? throw new Exception("Invalid token response from Supabase");

        var uid = tokenResp.User?.Id ?? throw new Exception("No user ID in auth response");
        var email2 = tokenResp.User?.Email ?? email;

        // ── Store session for next app launch (offline-capable) ──
        await StoreSessionAsync(uid, email2, tokenResp.AccessToken, tokenResp.RefreshToken);

        System.Diagnostics.Debug.WriteLine($"[AuthService] ✅ Signed in: {email2} ({uid})");
        return new AuthResult { Uid = uid, Email = email2 };
    }

    /// <summary>
    /// Sign up a new user (ONLINE-ONLY).
    /// - Calls POST /auth/v1/signup
    /// - On success: stores JWT + UID
    /// - Returns: {uid, email}
    /// 
    /// IMPORTANT: This does NOT create the profiles row.
    /// The Supabase trigger (deployed separately) handles that.
    /// Or SeedService.EnsureProfileExistsAsync() as backup.
    /// </summary>
    public async Task<AuthResult> SignUpAsync(string email, string password, object? metadata = null)
    {
        var url = $"{AppSettings.GoTrueUrl}/signup";
        var body = JsonSerializer.Serialize(new { email, password, data = metadata ?? new { } }, _jsonOpts);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add("apikey", AppSettings.SupabaseAnonKey);

        using var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            throw new Exception($"Sign-up failed ({response.StatusCode}): {err}");
        }

        var signupResp = await response.Content.ReadFromJsonAsync<GoTrueTokenResponse>(_jsonOpts)
                         ?? throw new Exception("Invalid signup response");

        var uid = signupResp.User?.Id ?? "";
        var email2 = signupResp.User?.Email ?? email;

        // ── Store session for next app launch (offline-capable) ──
        await StoreSessionAsync(uid, email2, signupResp.AccessToken, signupResp.RefreshToken);

        System.Diagnostics.Debug.WriteLine($"[AuthService] ✅ Signed up: {email2} ({uid})");
        return new AuthResult { Uid = uid, Email = email2 };
    }

    /// <summary>
    /// Get current session (OFFLINE-CAPABLE).
    /// - Reads from Keystore + SharedPreferences (no network)
    /// - Returns stored session or null
    /// 
    /// Called on app startup to check if already logged in.
    /// </summary>
    public async Task<AuthResult?> GetSessionAsync()
    {
        try
        {
            var jwt = await SecureStorage.Default.GetAsync(AppSettings.JwtKey);
            var uid = Preferences.Default.Get<string?>(AppSettings.SessionIdKey, null);

            if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(jwt))
                return null;

            // Parse email from JWT payload (no network, just base64 decode)
            var email = ExtractEmailFromJwt(jwt) ?? uid;
            return new AuthResult { Uid = uid, Email = email };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Sign out and clear all session data.
    /// </summary>
    public Task SignOutAsync()
    {
        try { SecureStorage.Default.Remove(AppSettings.JwtKey); }
        catch { }
        try { SecureStorage.Default.Remove(AppSettings.RefreshTokenKey); }
        catch { }
        Preferences.Default.Remove(AppSettings.SessionIdKey);
        _kv.Remove(AppSettings.SessionIdKey);

        System.Diagnostics.Debug.WriteLine("[AuthService] ✅ Signed out.");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Send password reset email (ONLINE-ONLY).
    /// </summary>
    public async Task ResetPasswordAsync(string email)
    {
        var url = $"{AppSettings.GoTrueUrl}/recover";
        var body = JsonSerializer.Serialize(new { email }, _jsonOpts);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add("apikey", AppSettings.SupabaseAnonKey);

        using var response = await _http.SendAsync(request);
        System.Diagnostics.Debug.WriteLine($"[AuthService] Password reset email sent to {email}: {response.StatusCode}");
    }

    /// <summary>
    /// Update current user's password (ONLINE-ONLY).
    /// Requires: Valid JWT from Keystore
    /// </summary>
    public async Task UpdatePasswordAsync(string newPassword)
    {
        var jwt = await SecureStorage.Default.GetAsync(AppSettings.JwtKey)
                  ?? throw new Exception("Not authenticated");

        var url = $"{AppSettings.GoTrueUrl}/user";
        var body = JsonSerializer.Serialize(new { password = newPassword }, _jsonOpts);

        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add("apikey", AppSettings.SupabaseAnonKey);
        request.Headers.Add("Authorization", $"Bearer {jwt}");

        using var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            throw new Exception($"Password update failed: {err}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PRIVATE HELPERS
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Store session in Keystore (JWT) + SharedPreferences (UID).
    /// Called after successful auth.
    /// </summary>
    private async Task StoreSessionAsync(string uid, string email, string jwt, string? refreshToken)
    {
        // Store in Android Keystore (encrypted)
        await SecureStorage.Default.SetAsync(AppSettings.JwtKey, jwt);
        if (!string.IsNullOrEmpty(refreshToken))
            await SecureStorage.Default.SetAsync(AppSettings.RefreshTokenKey, refreshToken);

        // Store UID in SharedPreferences for offline access
        Preferences.Default.Set(AppSettings.SessionIdKey, uid);
        _kv.Set(AppSettings.SessionIdKey, uid);
    }

    /// <summary>
    /// Extract email from JWT payload (no network call).
    /// JWT format: header.payload.signature
    /// Payload is base64-encoded JSON.
    /// </summary>
    private static string? ExtractEmailFromJwt(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return null;
            var payload = parts[1];

            // Pad base64 string to correct length
            var padded = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded));

            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("email", out var emailProp)
                ? emailProp.GetString()
                : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Get JWT token for API calls (used by SyncService).
    /// </summary>
    public async Task<string?> GetJwtAsync()
        => await SecureStorage.Default.GetAsync(AppSettings.JwtKey);

    /// <summary>
    /// Get current UID (from SharedPreferences, no network).
    /// </summary>
    public string? GetCurrentUid()
        => Preferences.Default.Get<string?>(AppSettings.SessionIdKey, null);

}

// ── DTOs ─────────────────────────────────────────────────────────────────────
public class AuthResult
{
    [JsonPropertyName("uid")]   public string Uid   { get; set; } = "";
    [JsonPropertyName("email")] public string Email { get; set; } = "";
}

internal class GoTrueTokenResponse
{
    [JsonPropertyName("access_token")]  public string  AccessToken  { get; set; } = "";
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    [JsonPropertyName("token_type")]    public string  TokenType    { get; set; } = "";
    [JsonPropertyName("user")]          public GoTrueUser? User     { get; set; }
}

internal class GoTrueUser
{
    [JsonPropertyName("id")]    public string? Id    { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
}
