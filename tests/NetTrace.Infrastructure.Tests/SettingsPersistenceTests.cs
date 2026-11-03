using Microsoft.Extensions.Logging.Abstractions;
using NetTrace.Core.Configuration;
using NetTrace.Infrastructure.Configuration;
using NetTrace.Infrastructure.Persistence;
using NetTrace.Infrastructure.Persistence.Migrations;

namespace NetTrace.Infrastructure.Tests;

public sealed class SettingsPersistenceTests : IAsyncLifetime
{
    private readonly TempDirectory _temp = new();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero));
    private SqliteConnectionFactory _factory = null!;
    private SqliteSettingsRepository _repository = null!;
    private MonitoringConfigurationStore _store = null!;

    public async Task InitializeAsync()
    {
        _factory = new SqliteConnectionFactory(Path.Combine(_temp.Path, "nettrace.db"));
        await new DatabaseMigrator(_factory, _clock, NullLogger<DatabaseMigrator>.Instance).MigrateAsync();
        _repository = new SqliteSettingsRepository(_factory, _clock);
        _store = new MonitoringConfigurationStore(_repository, NullLogger<MonitoringConfigurationStore>.Instance);
    }

    public Task DisposeAsync()
    {
        _temp.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Repository_MissingKey_ReturnsNull()
    {
        Assert.Null(await _repository.GetAsync("missing"));
    }

    [Fact]
    public async Task Repository_SetManyThenGet_RoundTripsAndUpserts()
    {
        await _repository.SetManyAsync(new Dictionary<string, string?> { ["a"] = "1", ["b"] = null });
        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        await _repository.SetManyAsync(new Dictionary<string, string?> { ["a"] = "2" });

        var all = await _repository.GetAllAsync();

        Assert.Equal("2", all["a"]);
        Assert.True(all.ContainsKey("b"));
        Assert.Null(all["b"]);
        Assert.Equal("2", await _repository.GetAsync("a"));
    }

    [Fact]
    public async Task Repository_RejectsBlankKeysWithoutWriting()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _repository.SetManyAsync(new Dictionary<string, string?> { ["ok"] = "1", [" "] = "2" }));

        Assert.Null(await _repository.GetAsync("ok"));
    }

    [Fact]
    public async Task Store_NothingSaved_LoadsDefaultsAtVersionZero()
    {
        var loaded = await _store.LoadAsync();

        Assert.Equal(0, loaded.Version);
        Assert.Null(loaded.LoadWarning);
        Assert.Equal(5, loaded.Configuration.IntervalSeconds);
    }

    [Fact]
    public async Task Store_SaveThenLoad_RoundTripsAndIncrementsVersion()
    {
        var custom = new MonitoringConfiguration
        {
            IntervalSeconds = 15,
            ExternalIcmpTargets = ["9.9.9.9", "1.0.0.1"],
            HttpsEndpoints = ["https://example.org/"],
            PublicIpLookupEnabled = true,
        };

        Assert.Equal(1, await _store.SaveAsync(custom));
        Assert.Equal(2, await _store.SaveAsync(custom with { IntervalSeconds = 10 }));
        var loaded = await _store.LoadAsync();

        Assert.Equal(2, loaded.Version);
        Assert.Equal(10, loaded.Configuration.IntervalSeconds);
        Assert.Equal(["9.9.9.9", "1.0.0.1"], loaded.Configuration.ExternalIcmpTargets);
        Assert.Equal(["https://example.org/"], loaded.Configuration.HttpsEndpoints);
        Assert.True(loaded.Configuration.PublicIpLookupEnabled);
    }

    [Fact]
    public async Task Store_StoresStableSnakeCaseJson()
    {
        await _store.SaveAsync(new MonitoringConfiguration());

        var json = await _repository.GetAsync(MonitoringConfigurationStore.ConfigurationKey);

        Assert.Contains("\"interval_seconds\":5", json, StringComparison.Ordinal);
        Assert.Contains("\"external_icmp_targets\":[\"1.1.1.1\",\"8.8.8.8\"]", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Store_InvalidConfiguration_IsRejectedAndNotSaved()
    {
        var ex = await Assert.ThrowsAsync<InvalidConfigurationException>(() =>
            _store.SaveAsync(new MonitoringConfiguration { IntervalSeconds = 7 }));

        Assert.NotEmpty(ex.Errors);
        Assert.Equal(0, (await _store.LoadAsync()).Version);
    }

    [Fact]
    public async Task Store_PartialJson_FillsMissingValuesWithDefaults()
    {
        await _repository.SetManyAsync(new Dictionary<string, string?>
        {
            [MonitoringConfigurationStore.ConfigurationKey] = "{\"interval_seconds\":30,\"unknown_future_setting\":true}",
            [MonitoringConfigurationStore.VersionKey] = "4",
        });

        var loaded = await _store.LoadAsync();

        Assert.Null(loaded.LoadWarning);
        Assert.Equal(4, loaded.Version);
        Assert.Equal(30, loaded.Configuration.IntervalSeconds);
        Assert.Equal(new MonitoringConfiguration().ExternalIcmpTargets, loaded.Configuration.ExternalIcmpTargets);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("null")]
    [InlineData("{\"interval_seconds\":7}")]
    public async Task Store_UnusableStoredSettings_FallBackToDefaultsWithoutOverwriting(string storedJson)
    {
        await _repository.SetManyAsync(new Dictionary<string, string?>
        {
            [MonitoringConfigurationStore.ConfigurationKey] = storedJson,
        });

        var loaded = await _store.LoadAsync();

        Assert.NotNull(loaded.LoadWarning);
        Assert.Equal(new MonitoringConfiguration().IntervalSeconds, loaded.Configuration.IntervalSeconds);
        Assert.Equal(storedJson, await _repository.GetAsync(MonitoringConfigurationStore.ConfigurationKey));
    }
}
