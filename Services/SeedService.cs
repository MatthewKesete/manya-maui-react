using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ManyaApp.Services;

/// <summary>
/// SeedFromCloudAsync — pulls user data from Supabase REST API and seeds local SQLite.
/// Called ONCE after first successful login on a device.
///
/// Safety rules:
///   - NEVER deletes from: questions, daily_challenges, qbrss, quests (curriculum)
///   - Uses UPSERT for users table (safe if called multiple times)
///   - DELETE + INSERT for user-specific tables (badges, chests, mastery, vault)
///   - All operations wrapped in a single SQLite transaction
/// </summary>
public class SeedService
{
    private readonly DatabaseService _db;
    private readonly KvService       _kv;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public SeedService(DatabaseService db, KvService kv)
    {
        _db = db;
        _kv = kv;
    }

    public async Task SeedFromCloudAsync(string uid, string jwt)
    {
        System.Diagnostics.Debug.WriteLine($"[SeedService] 🌱 Seeding from cloud for {uid}...");

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        http.DefaultRequestHeaders.Add("apikey", AppSettings.SupabaseAnonKey);
        http.DefaultRequestHeaders.Add("Prefer", "return=representation");

        try
        {
            await _db.RunInTransactionAsync(async () =>
            {
                // ── 1. PROFILE → users table (UPSERT) ────────────────────────────
                await SeedProfileAsync(http, uid);

                // ── 2. QUEST PROGRESS → KV store ─────────────────────────────────
                await SeedQuestProgressAsync(http, uid);

                // ── 3. USER VAULT → user_vault table ─────────────────────────────
                await SeedTableAsync(http, uid, "user_vault",      "user_id");

                // ── 4. BADGES → badges table ──────────────────────────────────────
                await SeedTableAsync(http, uid, "badges",          "user_id");

                // ── 5. USER CHESTS → user_chests table ────────────────────────────
                await SeedTableAsync(http, uid, "user_chests",     "user_id");

                // ── 6. CONCEPT MASTERY → concept_mastery table ───────────────────
                await SeedTableAsync(http, uid, "concept_mastery", "user_id");

                // ── 7. BALANCES → user_balances table ────────────────────────────
                await SeedBalancesAsync(http, uid);
            });

            System.Diagnostics.Debug.WriteLine("[SeedService] ✅ Cloud seed complete. App ready for offline use.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SeedService] ⚠️ Seed partial failure: {ex.Message}");
            // Don't rethrow — app can still work with local data
        }
    }

    // ── Profile ───────────────────────────────────────────────────────────────
    private async Task SeedProfileAsync(HttpClient http, string uid)
    {
        try
        {
            var url  = $"{AppSettings.RestApiUrl}/profiles?id=eq.{uid}&select=*&limit=1";
            var json = await http.GetStringAsync(url);
            var rows = JsonNode.Parse(json)?.AsArray();
            if (rows == null || rows.Count == 0) return;

            var profile = rows[0]!;

            // Map Supabase profile → local users table columns
            var userRow = new Dictionary<string, object?>
            {
                ["uid"]           = uid,
                ["email"]         = profile["email"]?.GetValue<string>(),
                ["nickname"]      = profile["full_name"]?.GetValue<string>() ?? profile["nickname"]?.GetValue<string>(),
                ["full_name"]     = profile["full_name"]?.GetValue<string>(),
                ["avatar_seed"]   = profile["avatar_url"]?.GetValue<string>() ?? "Manya",
                ["is_pro"]        = profile["is_pro"]?.GetValue<bool>() == true ? 1 : 0,
                ["theme"]         = profile["theme"]?.GetValue<string>() ?? "dark",
                ["learning_type"] = profile["learning_type"]?.GetValue<string>() ?? "ADAPTIVE",
                ["onboarded"]     = profile["onboarded"]?.GetValue<bool>() == true ? 1 : 0,
                ["created_at"]    = profile["created_at"]?.GetValue<string>(),
            };

            await _db.DbUpsert("users", userRow, new Dictionary<string, object?> { ["conflictCol"] = "uid" });
            System.Diagnostics.Debug.WriteLine("[SeedService] ✅ Profile seeded.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SeedService] ⚠️ Profile seed failed: {ex.Message}");
        }
    }

    // ── Quest progress → KV ───────────────────────────────────────────────────
    private async Task SeedQuestProgressAsync(HttpClient http, string uid)
    {
        try
        {
            // quest_progress stores KV-style progress per subject
            var url  = $"{AppSettings.RestApiUrl}/quest_progress?user_id=eq.{uid}&select=*";
            var json = await http.GetStringAsync(url);
            var rows = JsonNode.Parse(json)?.AsArray();
            if (rows == null) return;

            foreach (var row in rows)
            {
                if (row == null) continue;
                var subject = row["subject"]?.GetValue<string>() ?? row["quest_key"]?.GetValue<string>();
                var data    = row["data"]?.ToJsonString() ?? row.ToJsonString();
                if (subject != null)
                    _kv.Set($"manya_quest_progress_{subject}", data);
            }
            System.Diagnostics.Debug.WriteLine("[SeedService] ✅ Quest progress seeded to KV.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SeedService] ⚠️ Quest progress seed failed: {ex.Message}");
        }
    }

    // ── Generic user table seed (DELETE existing rows + INSERT cloud rows) ────
    private async Task SeedTableAsync(HttpClient http, string uid, string table, string userIdCol)
    {
        try
        {
            var url  = $"{AppSettings.RestApiUrl}/{table}?{userIdCol}=eq.{uid}&select=*";
            var json = await http.GetStringAsync(url);
            var rows = JsonNode.Parse(json)?.AsArray();
            if (rows == null) return;

            // Delete existing rows for this user (user-specific data only)
            await _db.ExecuteAsync($"DELETE FROM {table} WHERE {userIdCol} = ?", uid);

            // Insert cloud rows
            foreach (var row in rows)
            {
                if (row == null) continue;
                var dict = JsonNodeToDict(row.AsObject());
                if (dict.Count > 0)
                    await _db.DbInsert(table, dict);
            }

            System.Diagnostics.Debug.WriteLine($"[SeedService] ✅ {table} seeded ({rows.Count} rows).");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SeedService] ⚠️ {table} seed failed: {ex.Message}");
        }
    }

    // ── Balances → user_balances (UPSERT) ─────────────────────────────────────
    private async Task SeedBalancesAsync(HttpClient http, string uid)
    {
        try
        {
            var url  = $"{AppSettings.RestApiUrl}/user_balances?user_id=eq.{uid}&select=*&limit=1";
            var json = await http.GetStringAsync(url);
            var rows = JsonNode.Parse(json)?.AsArray();
            if (rows == null || rows.Count == 0) return;

            var dict = JsonNodeToDict(rows[0]!.AsObject());
            await _db.DbUpsert("user_balances", dict,
                new Dictionary<string, object?> { ["conflictCol"] = "user_id" });

            System.Diagnostics.Debug.WriteLine("[SeedService] ✅ Balances seeded.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SeedService] ⚠️ Balances seed failed: {ex.Message}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static Dictionary<string, object?> JsonNodeToDict(JsonObject obj)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var prop in obj)
        {
            var node = prop.Value;
            object? val = node?.GetValueKind() switch
            {
                JsonValueKind.String  => node.GetValue<string>(),
                JsonValueKind.Number  => node.GetValue<double>(),
                JsonValueKind.True    => 1,
                JsonValueKind.False   => 0,
                JsonValueKind.Null    => null,
                JsonValueKind.Object  => node.ToJsonString(),   // Store nested JSON as TEXT
                JsonValueKind.Array   => node.ToJsonString(),   // Store arrays as JSON TEXT
                _                    => null
            };
            dict[prop.Key] = val;
        }
        return dict;
    }
}
