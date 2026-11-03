using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetTrace.Core.Configuration;

namespace NetTrace.Infrastructure.Configuration;

/// <summary>
/// Stores <see cref="MonitoringConfiguration"/> as JSON in <c>AppSettings</c>. Missing settings
/// load as defaults. Unreadable or invalid stored settings also load as defaults with a
/// warning, but they are left in the database so nothing the user saved is lost.
/// </summary>
public sealed class MonitoringConfigurationStore(
    ISettingsRepository settings,
    ILogger<MonitoringConfigurationStore> logger) : IMonitoringConfigurationStore
{
    public const string ConfigurationKey = "monitoring.configuration";
    public const string VersionKey = "monitoring.configuration.version";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public async Task<StoredMonitoringConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        var json = await settings.GetAsync(ConfigurationKey, cancellationToken).ConfigureAwait(false);
        var version = ParseVersion(await settings.GetAsync(VersionKey, cancellationToken).ConfigureAwait(false));
        if (json is null)
        {
            return new StoredMonitoringConfiguration(new MonitoringConfiguration(), version, LoadWarning: null);
        }

        MonitoringConfiguration? configuration;
        try
        {
            configuration = JsonSerializer.Deserialize<MonitoringConfiguration>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            return Fallback(version, $"Stored monitoring settings could not be read ({ex.Message}). Defaults are in use.");
        }

        if (configuration is null)
        {
            return Fallback(version, "Stored monitoring settings were empty. Defaults are in use.");
        }

        var errors = MonitoringConfigurationValidator.Validate(configuration);
        return errors.Count == 0
            ? new StoredMonitoringConfiguration(configuration, version, LoadWarning: null)
            : Fallback(version, "Stored monitoring settings are invalid (" + string.Join(" ", errors) + "). Defaults are in use.");
    }

    public async Task<int> SaveAsync(MonitoringConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var errors = MonitoringConfigurationValidator.Validate(configuration);
        if (errors.Count > 0)
        {
            throw new InvalidConfigurationException(errors);
        }

        await _saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = ParseVersion(await settings.GetAsync(VersionKey, cancellationToken).ConfigureAwait(false));
            var next = current + 1;
            await settings.SetManyAsync(
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    [ConfigurationKey] = JsonSerializer.Serialize(configuration, JsonOptions),
                    [VersionKey] = next.ToString(CultureInfo.InvariantCulture),
                },
                cancellationToken).ConfigureAwait(false);

            logger.LogInformation("Monitoring configuration saved as version {ConfigurationVersion}.", next);
            return next;
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private StoredMonitoringConfiguration Fallback(int version, string warning)
    {
        logger.LogWarning("{Warning}", warning);
        return new StoredMonitoringConfiguration(new MonitoringConfiguration(), version, warning);
    }

    private static int ParseVersion(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version) && version >= 0 ? version : 0;
}
