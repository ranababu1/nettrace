using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetTrace.Core.Configuration;

namespace NetTrace.Infrastructure.Configuration;

/// <summary>Stores <see cref="AppPreferences"/> as JSON in <c>AppSettings</c>. Unreadable or invalid values load as defaults.</summary>
public sealed class AppPreferencesStore(ISettingsRepository settings, ILogger<AppPreferencesStore> logger) : IAppPreferencesStore
{
    public const string Key = "app.preferences";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private AppPreferences _current = new();

    public AppPreferences Current => Volatile.Read(ref _current);

    public event EventHandler<AppPreferences>? Changed;

    public async Task<AppPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        var json = await settings.GetAsync(Key, cancellationToken).ConfigureAwait(false);
        var loaded = new AppPreferences();
        if (json is not null)
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<AppPreferences>(json, JsonOptions);
                if (parsed is not null && AppPreferences.Validate(parsed).Count == 0)
                {
                    loaded = parsed;
                }
                else
                {
                    logger.LogWarning("Stored preferences were invalid; defaults are in use.");
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Stored preferences could not be read; defaults are in use.");
            }
        }

        Volatile.Write(ref _current, loaded);
        return loaded;
    }

    public async Task SaveAsync(AppPreferences preferences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var errors = AppPreferences.Validate(preferences);
        if (errors.Count > 0)
        {
            throw new InvalidConfigurationException(errors);
        }

        var previous = Current;
        await settings.SetManyAsync(
            new Dictionary<string, string?>(StringComparer.Ordinal) { [Key] = JsonSerializer.Serialize(preferences, JsonOptions) },
            cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _current, preferences);

        if (previous.RetentionDays != preferences.RetentionDays)
        {
            logger.LogInformation(
                "Data retention changed from {PreviousDays} to {Days} days (0 = forever).", previous.RetentionDays, preferences.RetentionDays);
        }

        Changed?.Invoke(this, preferences);
    }
}
