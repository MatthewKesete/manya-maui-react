using SQLite;

namespace ManyaApp.Services;

/// <summary>
/// Runs once on app startup.
/// Copies the pre-built manya.db from Resources/Raw (bundled assets) to
/// FileSystem.AppDataDirectory if it doesn't already exist there.
/// Then opens the connection and runs required PRAGMAs.
///
/// CRITICAL: Never calls EnsureCreated() or Migrate(). The schema is already in manya.db.
/// </summary>
public class DbInitializer
{
    private readonly DatabaseService _db;
    private bool _initialized;

    public DbInitializer(DatabaseService db)
    {
        _db = db;
    }

    public async Task RunAsync()
    {
        if (_initialized) return;
        _initialized = true;

        var dbPath = Path.Combine(FileSystem.AppDataDirectory, AppSettings.DbFileName);
        var shouldCopyBundledDb = !File.Exists(dbPath);

        if (!shouldCopyBundledDb && !IsLocalSchemaValid(dbPath))
        {
            System.Diagnostics.Debug.WriteLine("[DbInitializer] ⚠️ Existing manya.db is invalid or stale — replacing it with the bundled asset.");
            try
            {
                File.Delete(dbPath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DbInitializer] ❌ Failed to delete invalid DB: {ex.Message}");
                throw;
            }
            shouldCopyBundledDb = true;
        }

        if (shouldCopyBundledDb)
        {
            await CopyBundledDatabaseAsync(dbPath);
        }
        else
        {
            System.Diagnostics.Debug.WriteLine("[DbInitializer] ✅ manya.db already exists and appears valid — skipping copy.");
        }

        // ── Step 2: Open connection + run PRAGMAs ─────────────────────────────
        await _db.InitializeAsync(dbPath);
        System.Diagnostics.Debug.WriteLine("[DbInitializer] ✅ Database ready.");

        // Quick sanity check: ensure the local Android offline schema exists.
        try
        {
            // Attempt a harmless query against `users` and `questions` to confirm the expected Android DB schema.
            await _db.DbGet("users", new DbQuery { Limit = 1, Single = "maybe" });
            await _db.DbGet("questions", new DbQuery { Limit = 1, Single = "maybe" });
            System.Diagnostics.Debug.WriteLine("[DbInitializer] ✅ users/questions tables exist — offline schema ready.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DbInitializer] ❌ DB sanity check failed: {ex.Message}");
            throw new Exception($"Database schema check failed — expected Android local schema, but found invalid DB at {dbPath}: {ex.Message}", ex);
        }
    }

    private static async Task CopyBundledDatabaseAsync(string dbPath)
    {
        System.Diagnostics.Debug.WriteLine("[DbInitializer] 📦 Copying bundled manya.db...");
        try
        {
            using var bundledStream = await FileSystem.OpenAppPackageFileAsync(AppSettings.DbFileName);
            using var fileStream = File.OpenWrite(dbPath);
            await bundledStream.CopyToAsync(fileStream);
            System.Diagnostics.Debug.WriteLine($"[DbInitializer] ✅ manya.db copied to {dbPath}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DbInitializer] ❌ Copy failed: {ex.Message}");
            throw;
        }
    }

    private static bool IsLocalSchemaValid(string dbPath)
    {
        try
        {
            using var conn = new SQLiteConnection(dbPath);
            var hasUsers = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='users';") > 0;
            var hasQuestions = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='questions';") > 0;
            return hasUsers && hasQuestions;
        }
        catch
        {
            return false;
        }
    }
}

