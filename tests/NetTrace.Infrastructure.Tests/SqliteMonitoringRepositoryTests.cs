using Microsoft.Extensions.Logging.Abstractions;
using NetTrace.Core.Monitoring;
using NetTrace.Core.Network;
using NetTrace.Core.Probes;
using NetTrace.Infrastructure.Persistence;
using NetTrace.Infrastructure.Persistence.Migrations;

namespace NetTrace.Infrastructure.Tests;

public sealed class SqliteMonitoringRepositoryTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private SqliteConnectionFactory _factory = null!;
    private SqliteMonitoringRepository _repository = null!;

    public async Task InitializeAsync()
    {
        _factory = new SqliteConnectionFactory(Path.Combine(_temp.Path, "nettrace.db"));
        await new DatabaseMigrator(_factory, new FakeClock(T0), NullLogger<DatabaseMigrator>.Instance).MigrateAsync();
        _repository = new SqliteMonitoringRepository(_factory);
    }

    public Task DisposeAsync()
    {
        _temp.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SaveCycle_RoundTripsEveryOutcomeAndNullLatency()
    {
        ProbeResult[] results =
        [
            Result(ProbeType.Gateway, "192.168.1.1", ProbeOutcome.Success, 2.5),
            Result(ProbeType.ExternalIcmp, "1.1.1.1", ProbeOutcome.Timeout, null, "TimedOut"),
            Result(ProbeType.ExternalIcmp, "8.8.8.8", ProbeOutcome.Failure, null, "DestinationNetworkUnreachable"),
            Result(ProbeType.ExternalIcmp, "9.9.9.9", ProbeOutcome.Error, null, "PingFailed"),
            Result(ProbeType.Gateway, "(none)", ProbeOutcome.Skipped, null, "NoActiveConnection"),
        ];
        var cycle = Cycle(T0, results, version: 7);

        await _repository.SaveCycleAsync(cycle, recordNetworkSnapshot: false);
        var stored = await _repository.GetObservationsAsync(T0, T0.AddMinutes(1));

        Assert.Equal(results.Length, stored.Count);
        Assert.All(stored, s => Assert.Equal(cycle.CycleId, s.CycleId));
        Assert.All(stored, s => Assert.Equal(7, s.ConfigurationVersion));
        Assert.All(stored, s => Assert.Equal("nic-1", s.InterfaceId));
        Assert.Equal(results.Select(r => r.Outcome), stored.Select(s => s.Result.Outcome));
        Assert.Equal(results.Select(r => r.LatencyMs), stored.Select(s => s.Result.LatencyMs));
        Assert.Equal(results, stored.Select(s => s.Result));
    }

    [Fact]
    public async Task GetObservations_FiltersByHalfOpenRange()
    {
        await _repository.SaveCycleAsync(Cycle(T0, [Result(ProbeType.Gateway, "192.168.1.1", ProbeOutcome.Success, 1)]), false);
        await _repository.SaveCycleAsync(Cycle(T0.AddSeconds(5), [Result(ProbeType.Gateway, "192.168.1.1", ProbeOutcome.Success, 1, at: T0.AddSeconds(5))]), false);

        var firstOnly = await _repository.GetObservationsAsync(T0, T0.AddSeconds(5));

        Assert.Equal(T0, Assert.Single(firstOnly).Result.StartedAtUtc);
    }

    [Fact]
    public async Task SaveCycle_WithSnapshot_RecordsActiveInterface()
    {
        await _repository.SaveCycleAsync(Cycle(T0, [Result(ProbeType.Gateway, "192.168.1.1", ProbeOutcome.Success, 1)]), recordNetworkSnapshot: true);

        await using var connection = await _factory.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT InterfaceName, InterfaceType, GatewayAddress, Ipv4AddressesJson, Ssid FROM NetworkSnapshots;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Wi-Fi", reader.GetString(0));
        Assert.Equal("Wifi", reader.GetString(1));
        Assert.Equal("192.168.1.1", reader.GetString(2));
        Assert.Equal("[\"192.168.1.20\"]", reader.GetString(3));
        Assert.True(reader.IsDBNull(4));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task Gaps_AddCloseAndQueryOverlap()
    {
        var openId = await _repository.AddAsync(new MonitoringGap(T0, null, MonitoringGapReason.MonitoringDisabled, "off", "test"));
        await _repository.AddAsync(new MonitoringGap(T0.AddHours(-3), T0.AddHours(-2), MonitoringGapReason.SystemSleep, null, "test"));

        Assert.Equal(MonitoringGapReason.MonitoringDisabled, Assert.Single(await _repository.GetOverlappingAsync(T0.AddHours(1), T0.AddHours(2))).Reason);

        await _repository.CloseAsync(openId, T0.AddMinutes(30));
        await _repository.CloseAsync(openId, T0.AddHours(9)); // Closing twice keeps the first end.

        var gaps = await _repository.GetOverlappingAsync(T0.AddHours(-4), T0.AddHours(4));
        Assert.Equal(2, gaps.Count);
        Assert.Equal(T0.AddMinutes(30), gaps[1].EndedAtUtc);
        Assert.Empty(await _repository.GetOverlappingAsync(T0.AddHours(1), T0.AddHours(2)));
    }

    [Fact]
    public void ContextChange_DetectsMaterialDifferencesOnly()
    {
        var context = Context("192.168.1.1");

        Assert.True(NetworkContextChange.IsMaterial(null, context));
        Assert.False(NetworkContextChange.IsMaterial(context, Context("192.168.1.1") with { CapturedAtUtc = T0.AddMinutes(1) }));
        Assert.True(NetworkContextChange.IsMaterial(context, Context("10.0.0.1")));
        Assert.True(NetworkContextChange.IsMaterial(context, new NetworkContext(T0, [], null, null)));
        Assert.False(NetworkContextChange.IsMaterial(new NetworkContext(T0, [], null, null), new NetworkContext(T0, [], null, null)));
    }

    private static ProbeResult Result(
        ProbeType type, string target, ProbeOutcome outcome, double? latency, string? error = null, DateTimeOffset? at = null)
    {
        var start = at ?? T0;
        return new ProbeResult(type, target, "ICMP echo", start, start.AddMilliseconds(30), outcome, latency, 1000, error, error is null ? null : "message");
    }

    private static MonitoringCycle Cycle(DateTimeOffset at, IReadOnlyList<ProbeResult> results, int version = 1) =>
        new(Guid.NewGuid(), at, at, at.AddMilliseconds(40), version, Context("192.168.1.1"), results);

    private static NetworkContext Context(string gateway)
    {
        var nic = new NetworkInterfaceSnapshot(
            "nic-1", "Wi-Fi", "Test adapter", InterfaceKind.Wifi, InterfaceStatus.Up, 12, ["192.168.1.20"], [], [gateway], [gateway]);
        return new NetworkContext(T0, [nic], nic, gateway);
    }
}
