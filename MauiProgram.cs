using ManyaApp.Services;

namespace ManyaApp;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
         .UseMauiApp<App>();

        // Register services for DI
        builder.Services.AddSingleton<DatabaseService>();
        builder.Services.AddSingleton<DbInitializer>();
        builder.Services.AddSingleton<AuthService>();
        builder.Services.AddSingleton<KvService>();
        builder.Services.AddSingleton<FileService>();
        builder.Services.AddSingleton<SeedService>();
        builder.Services.AddTransient<MainPage>();

        return builder.Build();
    }
}