namespace NetTrace.Core.Configuration;

/// <summary>Persistent key/value application settings.</summary>
public interface ISettingsRepository
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, string?>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes all values in one transaction: either every key is updated or none is.</summary>
    Task SetManyAsync(IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken = default);
}

/// <summary>A loaded monitoring configuration and where it came from.</summary>
/// <param name="Version">Incremented on every save; recorded with each observation. 0 means never saved.</param>
/// <param name="LoadWarning">Set when stored settings could not be used and defaults were substituted.</param>
public sealed record StoredMonitoringConfiguration(
    MonitoringConfiguration Configuration,
    int Version,
    string? LoadWarning);

public interface IMonitoringConfigurationStore
{
    Task<StoredMonitoringConfiguration> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Validates and saves the configuration, returning the new version.</summary>
    Task<int> SaveAsync(MonitoringConfiguration configuration, CancellationToken cancellationToken = default);
}

public sealed class InvalidConfigurationException(IReadOnlyList<string> errors)
    : Exception("The configuration is invalid: " + string.Join(" ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
