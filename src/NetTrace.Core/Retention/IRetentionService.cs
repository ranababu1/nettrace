namespace NetTrace.Core.Retention;

/// <param name="CutoffUtc">Raw data before this instant would be removed; null when retention is Forever.</param>
public sealed record RetentionPreview(DateTimeOffset? CutoffUtc, int ObservationCount, int SnapshotCount)
{
    public static RetentionPreview Nothing { get; } = new(null, 0, 0);
}

public sealed record RetentionResult(DateTimeOffset? CutoffUtc, int ObservationsDeleted, int SnapshotsDeleted, int DaysSummarised);

/// <summary>
/// Explicit, user-initiated cleanup of old raw observations. Nothing is ever deleted
/// automatically or on upgrade. Before raw rows go, each affected day's summary is saved so
/// uptime and incident history survive. Incidents and monitoring gaps are never deleted.
/// </summary>
public interface IRetentionService
{
    /// <summary>What a cleanup would remove now. Keep-forever (0 days) always previews as nothing.</summary>
    Task<RetentionPreview> PreviewAsync(int retentionDays, CancellationToken cancellationToken = default);

    Task<RetentionResult> CleanupAsync(int retentionDays, CancellationToken cancellationToken = default);
}
