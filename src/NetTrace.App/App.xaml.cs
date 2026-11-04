using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetTrace.App.Services;
using NetTrace.App.ViewModels;
using NetTrace.Infrastructure;
using NetTrace.Infrastructure.Storage;

namespace NetTrace.App;

public partial class App : Application
{
    private static readonly TimeSpan HostShutdownTimeout = TimeSpan.FromSeconds(10);

    private SingleInstanceGuard? _instanceGuard;
    private IHost? _host;
    private ILogger<App>? _logger;

    // async void is acceptable here: OnStartup is a framework event handler.
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceGuard = SingleInstanceGuard.TryAcquire();
        if (_instanceGuard is null)
        {
            SingleInstanceGuard.SignalExistingInstance();
            Shutdown(0);
            return;
        }

        try
        {
            var paths = AppDataPaths.CreateDefault();
            paths.EnsureCreated();

            _host = BuildHost(e.Args, paths);
            _logger = _host.Services.GetRequiredService<ILogger<App>>();
            RegisterUnhandledExceptionHandlers();

            await _host.StartAsync();
            _logger.LogInformation(
                "NetTrace {Version} started (process {ProcessId}).",
                ApplicationInfo.Version,
                Environment.ProcessId);

            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();

            var tray = _host.Services.GetRequiredService<TrayIconService>();
            tray.Initialize(new TrayActions(
                ActivateMainWindow,
                ActivateMainWindow,
                () => _ = _host.Services.GetRequiredService<ReportService>().ExportTodayAsync(),
                () => { }));
            _host.Services.GetRequiredService<NotificationService>().Attach(tray);
            window.HiddenToTray += (_, _) => tray.NotifyHiddenToTray();

            _instanceGuard.ActivationRequested += () => Dispatcher.BeginInvoke(ActivateMainWindow);
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(ex, "NetTrace failed to start.");
            // ShutdownMode is explicit (the app lives in the tray), so a failed start must shut down itself.
            MessageBox.Show(
                $"NetTrace could not start.\n\n{ex.Message}",
                "NetTrace",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            _logger?.LogInformation("NetTrace stopping with exit code {ExitCode}.", e.ApplicationExitCode);
            try
            {
                // Stop off the UI thread so host shutdown cannot deadlock on the dispatcher.
                Task.Run(() => _host.StopAsync(HostShutdownTimeout)).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error while stopping the application host.");
            }
            finally
            {
                _host.Dispose();
            }
        }

        _instanceGuard?.Dispose();
        base.OnExit(e);
    }

    private static IHost BuildHost(string[] args, AppDataPaths paths)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
            // No implicit appsettings/environment configuration; settings will come from the local database.
            DisableDefaults = true,
        });

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        // The generic host's console-lifetime messages (Ctrl+C hint, content root path) do not apply to a desktop app.
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
        builder.Logging.AddDebug();
        builder.Logging.AddNetTraceFileLogger(paths.LogsDirectory);

        builder.Services.AddNetTraceInfrastructure(paths);
        builder.Services.AddSingleton<IApplicationShell, WpfApplicationShell>();
        builder.Services.AddSingleton<NetTrace.Reporting.ReportExporter>();
        builder.Services.AddSingleton<ReportService>();
        builder.Services.AddSingleton<MonitoringSettingsService>();
        builder.Services.AddSingleton<PreferencesService>();
        builder.Services.AddSingleton<NotificationService>();
        builder.Services.AddSingleton<HistoryViewModel>();
        builder.Services.AddSingleton<DayHistoryViewModel>();
        builder.Services.AddSingleton<ReportsViewModel>();
        builder.Services.AddSingleton<SettingsViewModel>();
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainWindow>();
        builder.Services.AddSingleton<TrayIconService>();

        return builder.Build();
    }

    private void RegisterUnhandledExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _logger?.LogCritical(args.ExceptionObject as Exception, "Unhandled exception; terminating: {IsTerminating}.", args.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger?.LogError(args.Exception, "Unobserved task exception.");
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogCritical(e.Exception, "Unhandled UI exception.");
    }

    private void ActivateMainWindow()
    {
        if (MainWindow is not { } window)
        {
            return;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        window.Activate();
    }
}
