namespace ManyaApp;

/// <summary>
/// Supabase project settings.
/// Fill in your project URL and anon key from the Supabase dashboard (Settings → API).
/// These match the values in manya-react/.env (VITE_SUPABASE_URL / VITE_SUPABASE_ANON_KEY).
/// </summary>
public static class AppSettings
{
    // ── Replace these with your actual Supabase project values ───────────────
    public const string SupabaseUrl     = "https://nvwzsrrsbrioragjchyn.supabase.co";
    public const string SupabaseAnonKey = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Im52d3pzcnJzYnJpb3JhZ2pjaHluIiwicm9sZSI6ImFub24iLCJpYXQiOjE3NzU3MzUxNTUsImV4cCI6MjA5MTMxMTE1NX0.hxj4SYLjRmUYWX8ijJkdZzgmYSoU0gvGu9Q41eJ4G_U";

    // ── Derived endpoints (do not change) ────────────────────────────────────
    public const string GoTrueUrl  = $"{SupabaseUrl}/auth/v1";
    public const string RestApiUrl = $"{SupabaseUrl}/rest/v1";

    // ── Local DB file name ────────────────────────────────────────────────────
    public const string DbFileName = "manya.db";

    // ── Secure storage keys ───────────────────────────────────────────────────
    public const string JwtKey          = "manya_jwt";
    public const string RefreshTokenKey = "manya_refresh_token";
    public const string SessionIdKey    = "manya_session_id";

    // ── Sync interval ─────────────────────────────────────────────────────────
    public static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(15);
}
