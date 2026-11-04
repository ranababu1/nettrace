using System.IO;
using System.Globalization;
using Microsoft.Extensions.Logging;
using NetTrace.Core.Configuration;
using NetTrace.Core.Monitoring;
using NetTrace.Core.Time;
using NetTrace.Infrastructure.Startup;
using NetTrace.Infrastructure.Storage;
using NetTrace.Reporting;

namespace NetTrace.App.Services;

/// <summary>Creates report bundles under the exports folder using the user's saved preferences.</summary>
public sealed class ReportService(
    ReportExporter exporter,
    IAppPreferencesStore preferences,
    IMonitoringConfigurationStore configurationStore,
    IClock clock,
    AppDataPaths paths,
    ILogger<ReportService> logger)
{
    public string ExportsDirectory => paths.ExportsDirectory;

    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, clock.LocalTimeZone).Date);

    public async Task<ExportResult> ExportAsync(
        DateOnly first, DateOnly last, ReportDetail detail, ExportFormat format, bool redactLocalAddresses, CancellationToken cancellationToken = default)
    {
        var stored = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var request = new ReportRequest(first, last, clock.LocalTimeZone, detail, format, new ReportPrivacy(redactLocalAddresses));
        var environment = new ReportEnvironment("NetTrace", ApplicationInfo.Version, clock.UtcNow, stored.Configuration);
        Directory.CreateDirectory(paths.ExportsDirectory);

        var result = await exporter.ExportAsync(request, environment, paths.ExportsDirectory, cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "Exported {Format} report for {First} to {Last} ({Detail}) with {Files} files.",
            format, first.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), last.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            detail, result.Files.Count);
        return result;
    }

    public Task<ExportResult> ExportTodayAsync(CancellationToken cancellationToken = default) =>
        ExportAsync(Today, Today, ReportDetail.Summary, ExportFormat.Json, preferences.Current.RedactLocalAddressesInExports, cancellationToken);
}

/// <summary>Saves monitoring settings and tells the running engine to apply them.</summary>
public sealed class MonitoringSettingsService(
    IMonitoringConfigurationStore store, IMonitoringControl control, ILogger<MonitoringSettingsService> logger)
{
    public async Task<MonitoringConfiguration> LoadAsync(CancellationToken cancellationToken = default) =>
        (await store.LoadAsync(cancellationToken).ConfigureAwait(false)).Configuration;

    /// <summary>Throws <see cref="InvalidConfigurationException"/> when the configuration is invalid.</summary>
    public async Task SaveAsync(MonitoringConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var version = await store.SaveAsync(configuration, cancellationToken).ConfigureAwait(false);
        control.ReloadConfiguration();
        logger.LogInformation("Monitoring settings saved (version {Version}) and applied.", version);
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        var current = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (current.MonitoringEnabled != enabled)
        {
            await SaveAsync(current with { MonitoringEnabled = enabled }, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Saves preferences and keeps the Windows launch-at-login registration in step with them.</summary>
public sealed class PreferencesService(
    IAppPreferencesStore store, IStartupRegistration startup, ILogger<PreferencesService> logger)
{
    public async Task SaveAsync(AppPreferences preferences, CancellationToken cancellationToken = default)
    {
        await store.SaveAsync(preferences, cancellationToken).ConfigureAwait(false);
        ApplyStartup(preferences);
    }

    /// <summary>Re-applies the registration at launch so a moved executable or edited registry value is corrected.</summary>
    public void ApplyStartup(AppPreferences preferences)
    {
        try
        {
            if (preferences.LaunchAtLogin && Environment.ProcessPath is { } exe)
            {
                startup.Enable(exe, preferences.StartMinimized);
            }
            else
            {
                startup.Disable();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not update the launch-at-login setting.");
            throw;
        }
    }
}
