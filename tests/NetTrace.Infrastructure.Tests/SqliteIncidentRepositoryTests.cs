using Microsoft.Extensions.Logging.Abstractions;
using NetTrace.Core.Incidents;
using NetTrace.Core.Monitoring;
using NetTrace.Core.Network;
using NetTrace.Core.Probes;
using NetTrace.Infrastructure.Persistence;
using NetTrace.Infrastructure.Persistence.Migrations;

namespace NetTrace.Infrastructure.Tests;

public sealed class SqliteIncidentRepositoryTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private SqliteConnectionFactory _factory = null!;
    private SqliteIncidentRepository _repository = null!;

    public async Task InitializeAsync()
    {
        _factory = new SqliteConnectionFactory(Path.Combine(_temp.Path, "nettrace.db"));
        await new DatabaseMigrator(_factory, new FakeClock(T0), NullLogger<DatabaseMigrator>.Instance).MigrateAsync();
        _repository = new SqliteIncidentRepository(_factory);
    }

    public Task DisposeAsync()
    {
        _temp.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Save_RoundTripsIncidentIncludingEvidence()
    {
        var incident = Incident(T0, null);

        await _repository.SaveAsync(incident);

        var stored = Assert.Single(await _repository.GetOverlappingAsync(T0.AddHours(-1), T0.AddHours(1)));
        Assert.Equal(incident, stored with { FailedProbes = incident.FailedProbes, WorkingProbes = incident.WorkingProbes });
        Assert.Equal(incident.FailedProbes, stored.FailedProbes);
        Assert.Equal(incident.WorkingProbes, stored.WorkingProbes);
        Assert.Null(stored.EndedAtUtc);
    }

    [Fact]
    public async Task Save_SameIdTwice_UpdatesInsteadOfDuplicating_IncludingReopen()
    {
        var open = Incident(T0, null);
        await _repository.SaveAsync(open);
        await _repository.SaveAsync(open with { EndedAtUtc = T0.AddMinutes(2) });
        await _repository.SaveAsync(open with { EndedAtUtc = null });

        var stored = Assert.Single(await _repository.GetOverlappingAsync(T0.AddHours(-1), T0.AddHours(1)));
        Assert.Null(stored.EndedAtUtc);

        await _repository.SaveAsync(open with { EndedAtUtc = T0.AddMinutes(5) });
        stored = Assert.Single(await _repository.GetOverlappingAsync(T0.AddHours(-1), T0.AddHours(1)));
        Assert.Equal(T0.AddMinutes(5), stored.EndedAtUtc);
    }

    [Fact]
    public async Task GetOverlapping_FiltersByRange_AndTreatsOpenIncidentsAsOngoing()
    {
        await _repository.SaveAsync(Incident(T0, T0.AddMinutes(1)));
        await _repository.SaveAsync(Incident(T0.AddHours(2), null));

        Assert.Empty(await _repository.GetOverlappingAsync(T0.AddMinutes(30), T0.AddMinutes(90)));
        Assert.Single(await _repository.GetOverlappingAsync(T0.AddHours(5), T0.AddHours(6)));
    }

    [Fact]
    public async Task CloseOrphaned_EndsOpenIncidentsAtLatestObservation_NotLater()
    {
        await _repository.SaveAsync(Incident(T0, null));
        await new SqliteMonitoringRepository(_factory).SaveCycleAsync(Cycle(T0.AddMinutes(3)), false);

        var closed = await _repository.CloseOrphanedAsync();

        Assert.Equal(1, closed);
        var stored = Assert.Single(await _repository.GetOverlappingAsync(T0.AddHours(-1), T0.AddHours(1)));
        Assert.Equal(T0.AddMinutes(3), stored.EndedAtUtc);
        Assert.Equal(0, await _repository.CloseOrphanedAsync());
    }

    [Fact]
    public async Task CloseOrphaned_WithNoObservations_EndsAtDetectionTime()
    {
        await _repository.SaveAsync(Incident(T0, null));

        await _repository.CloseOrphanedAsync();

        var stored = Assert.Single(await _repository.GetOverlappingAsync(T0.AddHours(-1), T0.AddHours(1)));
        Assert.Equal(T0.AddSeconds(15), stored.EndedAtUtc);
    }

    private static Incident Incident(DateTimeOffset start, DateTimeOffset? end) =>
        new(Guid.NewGuid(), start, start.AddSeconds(15), end, IncidentClassification.ExternalConnectivityUnavailable,
            ClassificationConfidence.High, "The gateway responded but external targets did not.",
            ["ExternalIcmp 1.1.1.1", "ExternalIcmp 8.8.8.8"], ["Gateway 192.168.1.1"]);

    private static MonitoringCycle Cycle(DateTimeOffset at)
    {
        var result = new ProbeResult(ProbeType.Gateway, "192.168.1.1", "fake", at, at.AddMilliseconds(5), ProbeOutcome.Success, 5, 1000, null, null);
        return new MonitoringCycle(Guid.NewGuid(), at, at, at.AddMilliseconds(5), 1, new NetworkContext(at, [], null, null), [result]);
    }
}
