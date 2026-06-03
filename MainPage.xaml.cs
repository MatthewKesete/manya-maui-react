#if ANDROID
using Android.Webkit;
using ManyaApp.Bridge;
using ManyaApp.Services;

namespace ManyaApp; // Must match x:Class="ManyaApp.MainPage"

public partial class MainPage : ContentPage  // Must be 'partial'
{
    private readonly DatabaseService _dbService;
    private readonly DbInitializer _dbInitializer;
    private readonly AuthService _authService;
    private readonly KvService _kvService;
    private readonly FileService _fileService;
    private readonly SeedService _seedService;
    private ManyaBridge? _bridge;

    public MainPage(
        DatabaseService dbService,
        DbInitializer dbInitializer,
        AuthService authService,
        KvService kvService,
        FileService fileService,
        SeedService seedService)
    {
        InitializeComponent(); // Generated from XAML

        _dbService = dbService;
        _dbInitializer = dbInitializer;
        _authService = authService;
        _kvService = kvService;
        _fileService = fileService;
        _seedService = seedService;
    }

    protected override async void OnHandlerChanged()
    {
        base.OnHandlerChanged();

        if (AppWebView?.Handler?.PlatformView is Android.Webkit.WebView nativeWebView)
        {
            await _dbInitializer.RunAsync();
            ConfigureWebView(nativeWebView);
        }
    }

    private void ConfigureWebView(Android.Webkit.WebView nativeView)
    {
        var settings = nativeView.Settings!;
        settings.JavaScriptEnabled = true;
        settings.DomStorageEnabled = true;
        settings.AllowFileAccess = true;
        settings.AllowFileAccessFromFileURLs = true;
        settings.AllowUniversalAccessFromFileURLs = true;
        settings.MediaPlaybackRequiresUserGesture = false;
        settings.MixedContentMode = MixedContentHandling.AlwaysAllow;

        _bridge = new ManyaBridge(nativeView, _dbService, _authService, _kvService, _fileService, _seedService);
        nativeView.AddJavascriptInterface(_bridge, "ManyaBackend");
        nativeView.LoadUrl("file:///android_asset/wwwroot/index.html");
    }
}
#else
using ManyaApp.Services;

namespace ManyaApp;

public partial class MainPage : ContentPage
{
    public MainPage(DatabaseService db, AuthService auth, KvService kv, FileService files, SeedService seed)
    {
        InitializeComponent();
    }
}
#endif