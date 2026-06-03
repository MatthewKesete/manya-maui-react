using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace ManyaApp.Services;

/// <summary>
/// Background sync service running as an IHostedService.
/// Periodically checks the SQLite `sync_logs` table (the outbox) and replays
/// pending operations to Supabase REST API.
/// Marks rows as synced=1 upon success.
/// </summary>
public class SyncService : BackgroundService
{
    private readonly IServiceProvider _services;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public SyncService(IServiceProvider services)
    {
        _services = services;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Delay initial sync slightly to let app start up
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Only sync if internet is available
                if (Connectivity.Current.NetworkAccess == NetworkAccess.Internet)
                {
                    await ProcessSyncQueueAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SyncService] ⚠️ Sync cycle error: {ex.Message}");
            }

            // Wait for next cycle (15 minutes)
            await Task.Delay(AppSettings.SyncInterval, stoppingToken);
        }
    }

    private async Task ProcessSyncQueueAsync()
    {
        // Resolve scoped dependencies
        var db   = _services.GetRequiredService<DatabaseService>();
        var auth = _services.GetRequiredService<AuthService>();

        var jwt = await auth.GetJwtAsync();
        if (string.IsNullOrEmpty(jwt)) return; // Not logged in

        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        if (!_http.DefaultRequestHeaders.Contains("apikey"))
            _http.DefaultRequestHeaders.Add("apikey", AppSettings.SupabaseAnonKey);

        // Fetch pending items (synced = 0), order by ID to preserve chronological order
        var pendingLogs = await db.QueryAsync("SELECT * FROM sync_logs WHERE synced = 0 ORDER BY id ASC LIMIT 50");
        if (pendingLogs.Count == 0) return;

        System.Diagnostics.Debug.WriteLine($"[SyncService] 🔄 Processing {pendingLogs.Count} pending sync items...");

        foreach (var log in pendingLogs)
        {
            var id      = log["id"]?.ToString();
            var type    = log["type"]?.ToString();
            var payload = log["data"]?.ToString();

            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(type) || string.IsNullOrEmpty(payload))
            {
                // Invalid log, mark synced to skip
                await db.ExecuteAsync("UPDATE sync_logs SET synced = 1 WHERE id = ?", id);
                continue;
            }

            bool success = await SyncItemAsync(type, payload);
            if (success)
            {
                await db.ExecuteAsync("UPDATE sync_logs SET synced = 1 WHERE id = ?", id);
                System.Diagnostics.Debug.WriteLine($"[SyncService] ✅ Synced item {id} ({type})");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[SyncService] ❌ Failed to sync item {id} ({type}). Will retry next cycle.");
            }
        }
    }

    private static async Task<bool> SyncItemAsync(string type, string jsonPayload)
    {
        try
        {
            var (endpoint, method) = type.ToUpperInvariant() switch
            {
                "ANSWER"   => ("user_answers",      HttpMethod.Post), // Insert only
                "PROFILE"  => ("profiles",          HttpMethod.Post), // Supabase UPSERT is POST with Prefer: resolution=merge-duplicates
                "PROGRESS" => ("quest_progress",    HttpMethod.Post),
                "VAULT"    => ("user_vault",        HttpMethod.Post),
                "BADGE"    => ("badges",            HttpMethod.Post),
                "MASTERY"  => ("concept_mastery",   HttpMethod.Post),
                "SESSION"  => ("user_sessions",     HttpMethod.Post), // Insert
                "EMOTION"  => ("emotional_metrics", HttpMethod.Post), // Insert
                "BALANCE"  => ("user_balances",     HttpMethod.Post), // Upsert
                _          => (null, null)
            };

            if (endpoint == null || method == null) return true; // Unknown type, skip it

            var url = $"{AppSettings.RestApiUrl}/{endpoint}";
            
            using var request = new HttpRequestMessage(method, url)
            {
                Content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json")
            };

            // For UPSERT in Supabase PostgREST, we must set Prefer header
            if (type is "PROFILE" or "PROGRESS" or "VAULT" or "BADGE" or "MASTERY" or "BALANCE")
            {
                request.Headers.Add("Prefer", "resolution=merge-duplicates");
            }

            using var response = await _http.SendAsync(request);
            
            if (response.IsSuccessStatusCode) return true;

            // Log error body for debugging
            var err = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine($"[SyncService] Supabase error ({response.StatusCode}): {err}");

            // If 400 Bad Request, it's a validation error. Better to mark synced=1 so it doesn't block queue forever.
            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                return true; 
            }

            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SyncService] Network error: {ex.Message}");
            return false;
        }
    }
}
