#if ANDROID
using Android.Webkit;
using AndroidWebView = Android.Webkit.WebView;
using Java.Interop;
using System.Text.Json;
using System.Text.Json.Serialization;
using ManyaApp.Services;

namespace ManyaApp.Bridge;

public class ManyaBridge : Java.Lang.Object
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly AndroidWebView _webView;
    private readonly DatabaseService _dbService;
    private readonly AuthService _authService;
    private readonly KvService _kvService;
    private readonly FileService _fileService;
    private readonly SeedService _seedService;

    public ManyaBridge(
        AndroidWebView webView,
        DatabaseService dbService,
        AuthService authService,
        KvService kvService,
        FileService fileService,
        SeedService seedService)
    {
        _webView = webView;
        _dbService = dbService;
        _authService = authService;
        _kvService = kvService;
        _fileService = fileService;
        _seedService = seedService;
    }

    [JavascriptInterface]
    [Export("authSignIn")]
    public void AuthSignIn(string email, string password, string callbackId)
        => _ = RunCallbackAsync(callbackId, async () => await _authService.SignInAsync(email, password));

    [JavascriptInterface]
    [Export("authSignUp")]
    public void AuthSignUp(string email, string password, string metadataJson, string callbackId)
        => _ = RunCallbackAsync(callbackId, async () =>
        {
            var metadata = string.IsNullOrWhiteSpace(metadataJson)
                ? null
                : JsonSerializer.Deserialize<object>(metadataJson, _jsonOptions);
            return await _authService.SignUpAsync(email, password, metadata);
        });

    [JavascriptInterface]
    [Export("authSignOut")]
    public void AuthSignOut(string callbackId)
        => _ = RunCallbackAsync(callbackId, async () =>
        {
            await _authService.SignOutAsync();
            return new { success = true };
        });

    [JavascriptInterface]
    [Export("authGetSession")]
    public void AuthGetSession(string callbackId)
        => _ = RunCallbackAsync(callbackId, async () => await _authService.GetSessionAsync());

    [JavascriptInterface]
    [Export("authUpdatePassword")]
    public void AuthUpdatePassword(string newPassword, string callbackId)
        => _ = RunCallbackAsync(callbackId, async () =>
        {
            await _authService.UpdatePasswordAsync(newPassword);
            return new { success = true };
        });

    [JavascriptInterface]
    [Export("authResetPassword")]
    public void AuthResetPassword(string email, string callbackId)
        => _ = RunCallbackAsync(callbackId, async () =>
        {
            await _authService.ResetPasswordAsync(email);
            return new { success = true };
        });

    [JavascriptInterface]
    [Export("dbGet")]
    public void DbGet(string table, string queryJson, string callbackId)
        => _ = RunCallbackAsync(callbackId, async () =>
        {
            var query = ParseJson<DbQuery>(queryJson) ?? new DbQuery();
            return await _dbService.DbGet(table, query);
        });

    [JavascriptInterface]
    [Export("dbInsert")]
    public void DbInsert(string table, string payloadJson, string callbackId)
        => _ = RunCallbackAsync(callbackId, async () =>
        {
            var payload = ParseJson<Dictionary<string, object?>>(payloadJson) ?? new Dictionary<string, object?>();
            return await _dbService.DbInsert(table, payload);
        });

    [JavascriptInterface]
    [Export("dbUpsert")]
    public void DbUpsert(string table, string payloadJson, string optionsJson, string callbackId)
        => _ = RunCallbackAsync(callbackId, async () =>
        {
            var payloadObj = ParseJson<object>(payloadJson) ?? new Dictionary<string, object?>();
            var options = ParseJson<Dictionary<string, object?>>(optionsJson);
            return await _dbService.DbUpsert(table, payloadObj, options);
        });

    [JavascriptInterface]
    [Export("dbPatch")]
    public void DbPatch(string table, string id, string patchJson, string callbackId)
        => _ = RunCallbackAsync(callbackId, async () =>
        {
            var patch = ParseJson<Dictionary<string, object?>>(patchJson) ?? new Dictionary<string, object?>();
            return await _dbService.DbPatch(table, id, patch);
        });

    [JavascriptInterface]
    [Export("dbDelete")]
    public void DbDelete(string table, string id, string callbackId)
        => _ = RunCallbackAsync(callbackId, async () =>
        {
            await _dbService.DbDelete(table, id);
            return new { success = true };
        });

    [JavascriptInterface]
    [Export("dbDeleteAll")]
    public void DbDeleteAll(string table, string callbackId)
        => _ = RunCallbackAsync(callbackId, async () =>
        {
            await _dbService.DbDeleteAll(table);
            return new { success = true };
        });

    [JavascriptInterface]
    [Export("dbBulkUpsert")]
    public void DbBulkUpsert(string table, string rowsJson, string optionsJson, string callbackId)
        => _ = RunCallbackAsync(callbackId, async () =>
        {
            var rows = ParseJson<List<Dictionary<string, object?>>>(rowsJson) ?? new List<Dictionary<string, object?>>();
            var options = ParseJson<Dictionary<string, object?>>(optionsJson);
            await _dbService.DbBulkUpsert(table, rows, options);
            return new { success = true };
        });

    [JavascriptInterface]
    [Export("fileReadJson")]
    public void FileReadJson(string path, string callbackId)
        => _ = RunCallbackAsync(callbackId, async () => await _fileService.ReadJsonAsync(path));

    [JavascriptInterface]
    [Export("fileReadText")]
    public void FileReadText(string path, string callbackId)
        => _ = RunCallbackAsync(callbackId, async () => await _fileService.ReadTextAsync(path));

    [JavascriptInterface]
    [Export("fileWriteJson")]
    public void FileWriteJson(string path, string payloadJson, string callbackId)
        => _ = RunCallbackAsync(callbackId, async () =>
        {
            var payload = ParseJson<object>(payloadJson) ?? new Dictionary<string, object?>();
            await _fileService.WriteJsonAsync(path, payload);
            return new { success = true };
        });

    [JavascriptInterface]
    [Export("fileGetAssetUrl")]
    public string FileGetAssetUrl(string path)
        => _fileService.GetAssetUrl(path);

    [JavascriptInterface]
    [Export("kvGet")]
    public string? KvGet(string key)
        => _kvService.Get(key);

    [JavascriptInterface]
    [Export("kvSet")]
    public void KvSet(string key, string value)
        => _kvService.Set(key, value);

    [JavascriptInterface]
    [Export("kvRemove")]
    public void KvRemove(string key)
        => _kvService.Remove(key);

    [JavascriptInterface]
    [Export("kvClear")]
    public void KvClear()
        => _kvService.Clear();

    private async Task RunCallbackAsync(string callbackId, Func<Task<object?>> action)
    {
        try
        {
            var result = await action();
            SendCallback(callbackId, result);
        }
        catch (Exception ex)
        {
            SendCallback(callbackId, new { error = ex.Message });
        }
    }

    private void SendCallback(string callbackId, object? payload)
    {
        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        var escaped = EscapeJsString(json);
        var script = $"window['{callbackId}']('{escaped}');";
        _webView.Post(() => _webView.EvaluateJavascript(script, null));
    }

    private static string EscapeJsString(string value)
        => value.Replace("\\", "\\\\")
                .Replace("'", "\\'")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");

    private static T? ParseJson<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(json, _jsonOptions);
        }
        catch
        {
            return default;
        }
    }
}
#endif