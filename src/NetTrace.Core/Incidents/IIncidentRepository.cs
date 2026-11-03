namespace NetTrace.Core.Incidents;

public interface IIncidentRepository
{
    /// <summary>Inserts the incident, or updates it when an incident with the same id is already stored.</summary>
    Task SaveAsync(Incident incident, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends incidents left open by a previous run (crash or shutdown). Each ends at the latest
    /// recorded observation, never later, so the unmonitored time afterwards is not claimed as an outage.
    /// Returns how many were closed.
    /// </summary>
    Task<int> CloseOrphanedAsync(CancellationToken cancellationToken = default);

    /// <summary>Incidents overlapping [<paramref name="fromUtc"/>, <paramref name="toUtc"/>); open incidents overlap everything after their start.</summary>
    Task<IReadOnlyList<Incident>> GetOverlappingAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default);
}
