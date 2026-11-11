using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using EdenGameLibraryManager.App.Services;
using EdenGameLibraryManager.App.ViewModels;
using EdenGameLibraryManager.App.Views;
using EdenGameLibraryManager.Core.Interfaces;
using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Infrastructure.AeroFoil;
using EdenGameLibraryManager.Infrastructure.Downloads;
using EdenGameLibraryManager.Infrastructure.Library;
using EdenGameLibraryManager.Infrastructure.Logging;
using EdenGameLibraryManager.Infrastructure.Persistence;
using EdenGameLibraryManager.Infrastructure.Security;
using EdenGameLibraryManager.Infrastructure.Storage;
using EdenGameLibraryManager.Infrastructure.TitleDb;
using EdenGameLibraryManager.Infrastructure.TorBox;
using EdenGameLibraryManager.Infrastructure.Updates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EdenGameLibraryManager.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private bool _shutdownComplete;
    private string? _restartExecutable;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var paths = new AppPaths();
        paths.EnsureCreated();
        _services = ConfigureServices(paths);
        var logger = _services.GetRequiredService<ILogger<App>>();
        logger.LogInformation("Eden Game Library Manager {Version} starting on {OS}",
            typeof(App).Assembly.GetName().Version, System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        HookUnhandledExceptions(logger);

        ApplyTheme(_services.GetRequiredService<ISettingsService>().Current.Theme);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainWindowViewModel(_services);
            var window = new MainWindow { DataContext = viewModel };
            _services.GetRequiredService<DialogService>().Owner = window;
            window.Opened += async (_, _) => await viewModel.StartAsync();
            // An installed update closes the app the normal way (pausing downloads), then starts the new exe.
            viewModel.Updates.RestartRequested += (_, exe) =>
            {
                _restartExecutable = exe;
                window.Close();
            };
            window.Closing += async (_, e) =>
            {
                if (_shutdownComplete) return;
                // Pause active transfers so they can be resumed next time, then close for real.
                e.Cancel = true;
                await viewModel.ShutdownAsync();
                _shutdownComplete = true;
                _services.Dispose();
                if (_restartExecutable is { } exe) StartUpdatedApp(exe, logger);
                window.Close();
            };
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void StartUpdatedApp(string exe, ILogger logger)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            })?.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not start the updated app");
        }
    }

    public static void ApplyTheme(AppTheme theme)
    {
        if (Current is null) return;
        Current.RequestedThemeVariant = theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.System => ThemeVariant.Default,
            _ => ThemeVariant.Dark,
        };
    }

    private static ServiceProvider ConfigureServices(AppPaths paths)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder
            .SetMinimumLevel(LogLevel.Information)
            .AddProvider(new FileLoggerProvider(paths.LogsDirectory)));

        services.AddSingleton(paths);
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton(sp => PlatformCredentialStore.Create(paths, sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<IConnectionProvider, SettingsConnectionProvider>();

        services.AddSingleton(_ =>
        {
            var http = new HttpClient(AeroFoilClient.CreateDefaultHandler()) { Timeout = Timeout.InfiniteTimeSpan };
            var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"EdenGameLibraryManager/{version}");
            return http;
        });
        services.AddSingleton<IAeroFoilClient, AeroFoilClient>();
        services.AddSingleton<TorBoxClient>();
        services.AddSingleton<ITitleDatabase, TitleDatabase>();
        services.AddSingleton<IGameLibraryProvider, ServerLibraryProvider>();
        services.AddSingleton<IGameMetadataService, GameMetadataService>();
        services.AddSingleton<IDownloadLinkResolver, DownloadLinkResolver>();

        services.AddSingleton<IManifestService>(sp => new ManifestService(paths.DatabaseFile, sp.GetRequiredService<ILogger<ManifestService>>()));
        services.AddSingleton<ILocalLibraryService, LocalLibraryService>();
        services.AddSingleton<ICacheManager, CacheManager>();
        services.AddSingleton<IPlayTimeService, EdenPlayTimeReader>();
        services.AddSingleton<IUpdateService>(sp => new UpdateService(
            UpdateService.CreateHttpClient($"EdenGameLibraryManager/{typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}"),
            paths, sp.GetRequiredService<ILogger<UpdateService>>()));
        services.AddSingleton<BandwidthLimiter>();
        services.AddSingleton<HttpDownloader>();
        services.AddSingleton<IDownloadManager, DownloadManager>();

        services.AddSingleton<DialogService>();
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<DialogService>());
        services.AddSingleton<LibraryCoordinator>();

        services.AddSingleton<AvailableGamesViewModel>();
        services.AddSingleton<DownloadedGamesViewModel>();
        services.AddSingleton<DownloadsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<UpdatesViewModel>();
        services.AddTransient<SetupViewModel>();

        return services.BuildServiceProvider();
    }

    private static void HookUnhandledExceptions(ILogger logger)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            logger.LogCritical(e.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            logger.LogError(e.Exception, "Unhandled UI exception");
            e.Handled = true;
        };
    }
}
