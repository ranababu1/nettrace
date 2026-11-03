using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using NetTrace.Core.Configuration;
using NetTrace.Infrastructure.Configuration;
using NetTrace.Infrastructure.Persistence;
using NetTrace.Infrastructure.Persistence.Migrations;
using NetTrace.Infrastructure.Startup;

namespace NetTrace.Infrastructure.Tests;

public sealed class AppPreferencesAndStartupTests : IAsyncLifetime
{
    private readonly TempDirectory _temp = new();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero));
    private SqliteSettingsRepository _repository = null!;
    private AppPreferencesStore _store = null!;

    public async Task InitializeAsync()
    {
        var factory = new SqliteConnectionFactory(Path.Combine(_temp.Path, "nettrace.db"));
        await new DatabaseMigrator(factory, _clock, NullLogger<DatabaseMigrator>.Instance).MigrateAsync();
        _repository = new SqliteSettingsRepository(factory, _clock);
        _store = new AppPreferencesStore(_repository, NullLogger<AppPreferencesStore>.Instance);
    }

    public Task DisposeAsync()
    {
        _temp.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Defaults_AreConservative()
    {
        var p = await _store.LoadAsync();

        Assert.False(p.LaunchAtLogin);
        Assert.True(p.CloseToTray);
        Assert.Equal(0, p.RetentionDays);
        Assert.True(p.RedactLocalAddressesInExports);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsInANewStore_AndRaisesChanged()
    {
        AppPreferences? raised = null;
        _store.Changed += (_, p) => raised = p;
        var prefs = new AppPreferences { LaunchAtLogin = true, StartMinimized = true, RetentionDays = 90, NotifyOnRecovery = false };

        await _store.SaveAsync(prefs);

        Assert.Equal(prefs, raised);
        Assert.Equal(prefs, _store.Current);
        var reloaded = await new AppPreferencesStore(_repository, NullLogger<AppPreferencesStore>.Instance).LoadAsync();
        Assert.Equal(prefs, reloaded);
    }

    [Fact]
    public async Task InvalidRetention_IsRejectedAndNothingChanges()
    {
        await Assert.ThrowsAsync<InvalidConfigurationException>(() => _store.SaveAsync(new AppPreferences { RetentionDays = 45 }));

        Assert.Equal(0, (await _store.LoadAsync()).RetentionDays);
    }

    [Fact]
    public async Task CorruptStoredPreferences_LoadAsDefaults()
    {
        await _repository.SetManyAsync(new Dictionary<string, string?> { [AppPreferencesStore.Key] = "{not json" });

        Assert.Equal(new AppPreferences(), await _store.LoadAsync());
    }

    [Fact]
    public void StartupRegistration_EnableDisable_IsIdempotentAndNeverDuplicates()
    {
        var name = "NetTraceTest-" + Guid.NewGuid().ToString("N");
        var registration = new RegistryStartupRegistration(NullLogger<RegistryStartupRegistration>.Instance, name);
        var exe = Path.Combine(_temp.Path, "NetTrace.App.exe");
        try
        {
            Assert.False(registration.IsEnabled);

            registration.Enable(exe, startMinimized: false);
            registration.Enable(exe, startMinimized: true);

            Assert.True(registration.IsEnabled);
            using (var key = Registry.CurrentUser.OpenSubKey(RegistryStartupRegistration.RunKeyPath))
            {
                Assert.Equal($"\"{exe}\" --minimized", key!.GetValue(name));
                Assert.Equal(1, key.GetValueNames().Count(n => n == name));
            }

            registration.Disable();
            registration.Disable();
            Assert.False(registration.IsEnabled);
        }
        finally
        {
            registration.Disable();
        }
    }

    [Fact]
    public void StartupRegistration_RelativePath_IsRejected()
    {
        var registration = new RegistryStartupRegistration(NullLogger<RegistryStartupRegistration>.Instance, "NetTraceTest-unused");

        Assert.Throws<ArgumentException>(() => registration.Enable("NetTrace.App.exe", false));
    }
}
