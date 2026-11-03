namespace NetTrace.Core.Configuration;

/// <summary>
/// Per-user application preferences that are not part of the monitoring methodology. Defaults
/// are conservative: nothing starts with Windows, nothing is deleted, local addresses are
/// redacted from exports.
/// </summary>
public sealed record AppPreferences
{
    /// <summary>Retention choices in days; 0 means keep forever.</summary>
    public static IReadOnlyList<int> AllowedRetentionDays { get; } = [0, 30, 90, 180, 365];

    public bool LaunchAtLogin { get; init; }

    public bool StartMinimized { get; init; }

    /// <summary>When true, closing the window hides it to the tray instead of exiting.</summary>
    public bool CloseToTray { get; init; } = true;

    /// <summary>Raw observations older than this many days may be removed by an explicit cleanup. 0 = forever.</summary>
    public int RetentionDays { get; init; }

    public bool NotifyOnOutage { get; init; } = true;

    public bool NotifyOnRecovery { get; init; } = true;

    public bool NotifyOnMonitoringProblem { get; init; } = true;

    public bool RedactLocalAddressesInExports { get; init; } = true;

    public static IReadOnlyList<string> Validate(AppPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        return AllowedRetentionDays.Contains(preferences.RetentionDays)
            ? []
            : [$"Retention must be one of {string.Join(", ", AllowedRetentionDays.Select(d => d == 0 ? "forever" : d + " days"))}."];
    }
}

public interface IAppPreferencesStore
{
    Task<AppPreferences> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Validates and saves the preferences, then raises <see cref="Changed"/>.</summary>
    Task SaveAsync(AppPreferences preferences, CancellationToken cancellationToken = default);

    /// <summary>The latest saved or loaded preferences, available without I/O. Defaults until first load.</summary>
    AppPreferences Current { get; }

    event EventHandler<AppPreferences>? Changed;
}
