using SQLite;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ManyaApp.Services;

/// <summary>
/// Raw SQLite access layer using sqlite-net-pcl + SQLitePCL.raw.
/// 
/// Pattern mirrors Services to refer/DatabaseService.cs:
///  - Schema-agnostic queries (no EF Core entity mapping needed)
///  - Returns List&lt;Dictionary&lt;string, object&gt;&gt; for all SELECT queries
///  - NEVER creates tables — the schema is in the bundled manya.db
///
/// Implements the full db.* contract from storageFacade.js:
///   get, insert, upsert, patch, delete, deleteAll, bulkUpsert
/// </summary>
public class DatabaseService
{
    private SQLiteAsyncConnection? _connection;
    private static readonly SemaphoreSlim _initLock = new(1, 1);

    // Tables that must NEVER be deleted from (curriculum data)
    private static readonly HashSet<string> _protectedTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "questions", "daily_challenges", "qbrss", "quests",
        "achievements", "chest_reward_pool", "reward_config"
    };

    private static readonly Dictionary<string, string> _primaryKeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["users"] = "uid",
        ["questions"] = "qid",
        ["profiles"] = "id",
        ["public_profiles"] = "id",
        ["public_quest_progress"] = "id",
        ["public_user_answers"] = "id",
        ["public_user_chests"] = "id",
        ["public_badges"] = "id"
    };

    private static string GetPrimaryKeyColumn(string table)
    {
        return _primaryKeyMap.TryGetValue(table, out var key) ? key : "id";
    }

    // ── Initialization ────────────────────────────────────────────────────────
    public async Task InitializeAsync(string dbPath)
    {
        await _initLock.WaitAsync();
        try
        {
            if (_connection != null) return;
            _connection = new SQLiteAsyncConnection(dbPath);

            // Required PRAGMAs on every connection
            await _connection.ExecuteScalarAsync<string>("PRAGMA foreign_keys=ON;");
            await _connection.ExecuteScalarAsync<string>("PRAGMA journal_mode=WAL;");

            System.Diagnostics.Debug.WriteLine("[DatabaseService] ✅ SQLite connection opened with WAL mode.");
        }
        finally
        {
            _initLock.Release();
        }
    }

    private Task<SQLiteAsyncConnection> GetConnectionAsync()
    {
        if (_connection != null) return Task.FromResult(_connection);
        throw new InvalidOperationException("DatabaseService not initialized. Call InitializeAsync first.");
    }

    // ── Generic Query (schema-agnostic, mirrors Services to refer/DatabaseService.cs) ──
    public async Task<List<Dictionary<string, object?>>> QueryAsync(string sql, params object?[]? args)
    {
        var conn     = await GetConnectionAsync();
        var syncConn = conn.GetConnection();
        var results  = new List<Dictionary<string, object?>>();

        int rc = SQLitePCL.raw.sqlite3_prepare_v2(syncConn.Handle, sql, out var stmt);
        if (rc != SQLitePCL.raw.SQLITE_OK)
            throw new Exception($"Prepare failed ({rc}): {SQLitePCL.raw.sqlite3_errmsg(syncConn.Handle).utf8_to_string()}");

        try
        {
            // Bind parameters
            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    int idx = i + 1;
                    var val = args[i];
                    if      (val == null)        SQLitePCL.raw.sqlite3_bind_null(stmt, idx);
                    else if (val is int    iv)   SQLitePCL.raw.sqlite3_bind_int64(stmt, idx, iv);
                    else if (val is long   lv)   SQLitePCL.raw.sqlite3_bind_int64(stmt, idx, lv);
                    else if (val is double dv)   SQLitePCL.raw.sqlite3_bind_double(stmt, idx, dv);
                    else if (val is float  fv)   SQLitePCL.raw.sqlite3_bind_double(stmt, idx, fv);
                    else if (val is bool   bv)   SQLitePCL.raw.sqlite3_bind_int(stmt, idx, bv ? 1 : 0);
                    else SQLitePCL.raw.sqlite3_bind_text(stmt, idx, val.ToString() ?? "");
                }
            }

            // Read column names
            int colCount   = SQLitePCL.raw.sqlite3_column_count(stmt);
            var colNames   = new string[colCount];
            for (int i = 0; i < colCount; i++)
                colNames[i] = SQLitePCL.raw.sqlite3_column_name(stmt, i).utf8_to_string();

            // Iterate rows
            while (SQLitePCL.raw.sqlite3_step(stmt) == SQLitePCL.raw.SQLITE_ROW)
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < colCount; i++)
                {
                    int type = SQLitePCL.raw.sqlite3_column_type(stmt, i);
                    object? value = type switch
                    {
                        SQLitePCL.raw.SQLITE_INTEGER => SQLitePCL.raw.sqlite3_column_int64(stmt, i),
                        SQLitePCL.raw.SQLITE_FLOAT   => SQLitePCL.raw.sqlite3_column_double(stmt, i),
                        SQLitePCL.raw.SQLITE_TEXT    => SQLitePCL.raw.sqlite3_column_text(stmt, i).utf8_to_string(),
                        SQLitePCL.raw.SQLITE_NULL    => null,
                        _                            => SQLitePCL.raw.sqlite3_column_text(stmt, i).utf8_to_string()
                    };
                    row[colNames[i]] = value;
                }
                results.Add(row);
            }
        }
        finally
        {
            SQLitePCL.raw.sqlite3_finalize(stmt);
        }

        return results;
    }

    // ── Generic Execute (INSERT, UPDATE, DELETE) ──────────────────────────────
    public async Task<int> ExecuteAsync(string sql, params object?[]? args)
    {
        var conn = await GetConnectionAsync();
        return await conn.ExecuteAsync(sql, args ?? Array.Empty<object?>());
    }

    // ── Execute returning last inserted row ───────────────────────────────────
    public async Task<long> ExecuteGetLastRowIdAsync(string sql, params object?[]? args)
    {
        var conn = await GetConnectionAsync();
        await conn.ExecuteAsync(sql, args ?? Array.Empty<object?>());
        var rows = await QueryAsync("SELECT last_insert_rowid() AS id");
        return rows.Count > 0 && rows[0]["id"] is long lid ? lid : 0;
    }

    // ── Transaction helper ────────────────────────────────────────────────────
    public async Task RunInTransactionAsync(Func<Task> work)
    {
        await ExecuteAsync("BEGIN TRANSACTION");
        try
        {
            await work();
            await ExecuteAsync("COMMIT");
        }
        catch
        {
            await ExecuteAsync("ROLLBACK");
            throw;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HIGH-LEVEL db.* CONTRACT METHODS
    // Implements window.ManyaBackend.db.* for the bridge.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// db.get(table, query) — SELECT with filter/limit/orderBy/single support.
    /// </summary>
    public async Task<object?> DbGet(string table, DbQuery query)
    {
        var (sql, args) = BuildSelectSql(table, query);
        var rows = await QueryAsync(sql, args.ToArray());

        if (query.Single == "true" || query.Id != null)
            return rows.Count > 0 ? rows[0] : throw new Exception($"Row not found in {table}");
        if (query.Single == "maybe")
            return rows.Count > 0 ? (object?)rows[0] : null;

        return rows;
    }

    /// <summary>
    /// db.insert(table, payload) — INSERT a single row, returns the inserted row.
    /// </summary>
    public async Task<Dictionary<string, object?>> DbInsert(string table, Dictionary<string, object?> payload)
    {
        var cols      = payload.Keys.ToList();
        var colList   = string.Join(", ", cols);
        var paramList = string.Join(", ", cols.Select(_ => "?"));
        var args      = cols.Select(c => payload[c]).ToArray();

        await ExecuteAsync($"INSERT INTO {table} ({colList}) VALUES ({paramList})", args);

        // Return the inserted row
        var id = await QueryAsync("SELECT last_insert_rowid() AS id");
        if (id.Count > 0 && id[0]["id"] is long rowId && rowId > 0)
        {
            var inserted = await QueryAsync($"SELECT * FROM {table} WHERE rowid = ?", rowId);
            return inserted.Count > 0 ? inserted[0] : payload;
        }
        return payload;
    }

    /// <summary>
    /// db.upsert(table, payload, options) — INSERT OR REPLACE based on conflictCol or primary key.
    /// </summary>
    public async Task<List<Dictionary<string, object?>>> DbUpsert(
        string table,
        object payloadObj,
        Dictionary<string, object?>? options = null)
    {
        // payload can be a single dict or a list of dicts
        var payloads = payloadObj is List<Dictionary<string, object?>> list
            ? list
            : new List<Dictionary<string, object?>> { (Dictionary<string, object?>)payloadObj };

        string? conflictCol = options?.ContainsKey("conflictCol") == true
            ? options["conflictCol"]?.ToString()
            : null;

        var result = new List<Dictionary<string, object?>>();

        await RunInTransactionAsync(async () =>
        {
            foreach (var payload in payloads)
            {
                var cols      = payload.Keys.ToList();
                var colList   = string.Join(", ", cols);
                var paramList = string.Join(", ", cols.Select(_ => "?"));
                var args      = cols.Select(c => payload[c]).ToArray();

                string upsertSql;
                if (conflictCol != null)
                    upsertSql = $"INSERT INTO {table} ({colList}) VALUES ({paramList}) ON CONFLICT({conflictCol}) DO UPDATE SET {string.Join(", ", cols.Where(c => c != conflictCol).Select(c => $"{c}=excluded.{c}"))}";
                else
                    upsertSql = $"INSERT OR REPLACE INTO {table} ({colList}) VALUES ({paramList})";

                await ExecuteAsync(upsertSql, args);
                result.Add(payload);
            }
        });

        return result;
    }

    /// <summary>
    /// db.patch(table, id, patchObj) — UPDATE table SET ... WHERE id = ?.
    /// </summary>
    public async Task<Dictionary<string, object?>> DbPatch(string table, string id, Dictionary<string, object?> patch)
    {
        var idColumn = GetPrimaryKeyColumn(table);
        var setClauses = string.Join(", ", patch.Keys.Select(k => $"{k} = ?"));
        var args       = patch.Values.Append((object?)id).ToArray();

        await ExecuteAsync($"UPDATE {table} SET {setClauses} WHERE {idColumn} = ?", args);

        var updated = await QueryAsync($"SELECT * FROM {table} WHERE {idColumn} = ?", id);
        return updated.Count > 0 ? updated[0] : patch;
    }

    /// <summary>
    /// db.delete(table, id) — DELETE FROM table WHERE id = ?.
    /// Protected tables are blocked.
    /// </summary>
    public async Task DbDelete(string table, string id)
    {
        if (_protectedTables.Contains(table))
            throw new InvalidOperationException($"DELETE blocked on protected table: {table}");
        var idColumn = GetPrimaryKeyColumn(table);
        await ExecuteAsync($"DELETE FROM {table} WHERE {idColumn} = ?", id);
    }

    /// <summary>
    /// db.deleteAll(table) — DELETE all rows. Protected tables are blocked.
    /// </summary>
    public async Task DbDeleteAll(string table)
    {
        if (_protectedTables.Contains(table))
            throw new InvalidOperationException($"deleteAll blocked on protected table: {table}");
        await ExecuteAsync($"DELETE FROM {table}");
    }

    /// <summary>
    /// db.bulkUpsert(table, rows, options) — Batch INSERT OR REPLACE in a transaction.
    /// </summary>
    public async Task DbBulkUpsert(string table, List<Dictionary<string, object?>> rows, Dictionary<string, object?>? options = null)
    {
        if (rows.Count == 0) return;
        await DbUpsert(table, rows, options);
    }

    // ── SQL Builder ───────────────────────────────────────────────────────────
    private static (string sql, List<object?>) BuildSelectSql(string table, DbQuery query)
    {
        var whereClauses = new List<string>();
        var args         = new List<object?>();

        // Direct ID lookup
        if (query.Id != null)
        {
            var idColumn = GetPrimaryKeyColumn(table);
            whereClauses.Add($"{idColumn} = ?");
            args.Add(query.Id);
        }

        // Filter map
        foreach (var kv in query.Filters)
        {
            var key = kv.Key;
            var val = kv.Value;

            // Operator filters: "col:ilike", "col:gt", etc.
            if (key.Contains(':'))
            {
                var parts = key.Split(':', 2);
                var col   = parts[0];
                var op    = parts[1].ToLower();
                var sqlOp = op switch
                {
                    "eq"    => "=",
                    "neq"   => "!=",
                    "gt"    => ">",
                    "gte"   => ">=",
                    "lt"    => "<",
                    "lte"   => "<=",
                    "like"  => "LIKE",
                    "ilike" => "LIKE",  // SQLite LIKE is case-insensitive for ASCII by default
                    "is"    => "IS",
                    _       => "="
                };

                if (op == "ilike")
                {
                    whereClauses.Add($"{col} LIKE ? COLLATE NOCASE");
                    args.Add(val?.ToString()?.Replace("*", "%") ?? "");
                }
                else
                {
                    whereClauses.Add($"{col} {sqlOp} ?");
                    args.Add(val);
                }
            }
            // uid → user_id mapping (critical: storageFacade sends uid=X)
            else if (key == "user_id" || key == "uid")
            {
                if (key == "uid" && table.Equals("users", StringComparison.OrdinalIgnoreCase))
                {
                    whereClauses.Add("uid = ?");
                }
                else
                {
                    whereClauses.Add("user_id = ?");
                }
                args.Add(val);
            }
            // Boolean: stored as INTEGER in SQLite
            else if (val is bool boolVal)
            {
                whereClauses.Add($"{key} = ?");
                args.Add(boolVal ? 1 : 0);
            }
            else
            {
                whereClauses.Add($"{key} = ?");
                args.Add(val);
            }
        }

        // OR filter (e.g. "status.eq.active,status.eq.pending")
        if (query.OrFilter != null)
        {
            var orParts = query.OrFilter.Split(',');
            var orClauses = orParts.Select(p =>
            {
                var segments = p.Split('.');
                if (segments.Length == 3)
                {
                    args.Add(segments[2]);
                    return $"{segments[0]} = ?";
                }
                return null;
            }).Where(x => x != null).ToList();

            if (orClauses.Count > 0)
                whereClauses.Add($"({string.Join(" OR ", orClauses)})");
        }

        var sql = $"SELECT * FROM {table}";
        if (whereClauses.Count > 0)
            sql += " WHERE " + string.Join(" AND ", whereClauses);

        if (query.OrderBy != null)
            sql += $" ORDER BY {query.OrderBy} {query.OrderDir.ToUpper()}";

        if (query.Limit.HasValue)
            sql += $" LIMIT {query.Limit.Value}";

        return (sql, args);
    }
}

// ── Query model (parsed from JSON args sent by storageFacade.js) ──────────────
public class DbQuery
{
    public string?                        Id       { get; set; }
    public string                         Table    { get; set; } = "";
    public Dictionary<string, object?>    Filters  { get; set; } = new();
    public int?                           Limit    { get; set; }
    public string?                        OrderBy  { get; set; }
    public string                         OrderDir { get; set; } = "asc";
    /// <summary>"true" | "maybe" | "false"</summary>
    [JsonConverter(typeof(BoolStringJsonConverter))]
    public string                         Single   { get; set; } = "false";
    public string?                        OrFilter { get; set; }
}

internal class BoolStringJsonConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString() ?? "false",
            JsonTokenType.True   => "true",
            JsonTokenType.False  => "false",
            JsonTokenType.Null   => "false",
            _ => throw new JsonException("Expected string or bool for DbQuery.Single")
        };
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}
