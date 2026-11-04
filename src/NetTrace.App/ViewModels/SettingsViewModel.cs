using System.Globalization;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using NetTrace.App.Services;
using NetTrace.Core.Configuration;
using NetTrace.Core.Retention;
using NetTrace.Core.Statistics;

namespace NetTrace.App.ViewModels;

public sealed record ChoiceItem(string Label, int Value);

/// <summary>Monitoring, alert, startup, privacy and data-retention settings. Nothing applies until Save.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly MonitoringSettingsService _monitoring;
    private readonly PreferencesService _preferencesService;
    private readonly IAppPreferencesStore _preferencesStore;
    private readonly IRetentionService _retention;
    private readonly IApplicationShell _shell;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;

    private bool _monitoringEnabled = true;
    private int _intervalSeconds = 5;
    private string _externalTargets = string.Empty;
    private string _dnsHostname = string.Empty;
    private string _httpsEndpoints = string.Empty;
    private string _gatewayTimeout = string.Empty;
    private string _externalTimeout = string.Empty;
    private string _dnsTimeout = string.Empty;
    private string _httpsTimeout = string.Empty;
    private string _outageThreshold = string.Empty;
    private string _recoveryThreshold = string.Empty;
    private string _mergeWindow = string.Empty;
    private bool _launchAtLogin;
    private bool _startMinimized;
    private bool _closeToTray = true;
    private bool _notifyOutage = true;
    private bool _notifyRecovery = true;
    private bool _notifyMonitoring = true;
    private bool _redactExports = true;
    private int _retentionDays;
    private string _saveMessage = string.Empty;
    private bool _saveFailed;
    private string _retentionMessage = string.Empty;
    private bool _busy;

    public SettingsViewModel(
        MonitoringSettingsService monitoring,
        PreferencesService preferencesService,
        IAppPreferencesStore preferencesStore,
        IRetentionService retention,
        IApplicationShell shell,
        ILogger<SettingsViewModel> logger)
    {
        _monitoring = monitoring;
        _preferencesService = preferencesService;
        _preferencesStore = preferencesStore;
        _retention = retention;
        _shell = shell;
        _logger = logger;

        SaveCommand = new RelayCommand(() => _ = SaveAsync(), () => !Busy);
        ResetCommand = new RelayCommand(() => LoadMonitoring(new MonitoringConfiguration()));
        PreviewCleanupCommand = new RelayCommand(() => _ = PreviewCleanupAsync(), () => !Busy);
        CleanupCommand = new RelayCommand(() => _ = CleanupAsync(), () => !Busy);
        _ = LoadAsync();
    }

    public IReadOnlyList<int> IntervalChoices { get; } = MonitoringConfiguration.AllowedIntervalsSeconds;

    public IReadOnlyList<ChoiceItem> RetentionChoices { get; } =
        AppPreferences.AllowedRetentionDays.Select(d => new ChoiceItem(d == 0 ? "Keep forever" : d + " days", d)).ToList();

    public ICommand SaveCommand { get; }

    public ICommand ResetCommand { get; }

    public ICommand PreviewCleanupCommand { get; }

    public ICommand CleanupCommand { get; }

    public bool MonitoringEnabled { get => _monitoringEnabled; set => SetProperty(ref _monitoringEnabled, value); }

    public int IntervalSeconds { get => _intervalSeconds; set => SetProperty(ref _intervalSeconds, value); }

    public string ExternalTargets { get => _externalTargets; set => SetProperty(ref _externalTargets, value); }

    public string DnsHostname { get => _dnsHostname; set => SetProperty(ref _dnsHostname, value); }

    public string HttpsEndpoints { get => _httpsEndpoints; set => SetProperty(ref _httpsEndpoints, value); }

    public string GatewayTimeout { get => _gatewayTimeout; set => SetProperty(ref _gatewayTimeout, value); }

    public string ExternalTimeout { get => _externalTimeout; set => SetProperty(ref _externalTimeout, value); }

    public string DnsTimeout { get => _dnsTimeout; set => SetProperty(ref _dnsTimeout, value); }

    public string HttpsTimeout { get => _httpsTimeout; set => SetProperty(ref _httpsTimeout, value); }

    public string OutageThreshold { get => _outageThreshold; set => SetProperty(ref _outageThreshold, value); }

    public string RecoveryThreshold { get => _recoveryThreshold; set => SetProperty(ref _recoveryThreshold, value); }

    public string MergeWindow { get => _mergeWindow; set => SetProperty(ref _mergeWindow, value); }

    public bool LaunchAtLogin { get => _launchAtLogin; set => SetProperty(ref _launchAtLogin, value); }

    public bool StartMinimized { get => _startMinimized; set => SetProperty(ref _startMinimized, value); }

    public bool CloseToTray { get => _closeToTray; set => SetProperty(ref _closeToTray, value); }

    public bool NotifyOutage { get => _notifyOutage; set => SetProperty(ref _notifyOutage, value); }

    public bool NotifyRecovery { get => _notifyRecovery; set => SetProperty(ref _notifyRecovery, value); }

    public bool NotifyMonitoring { get => _notifyMonitoring; set => SetProperty(ref _notifyMonitoring, value); }

    public bool RedactExports { get => _redactExports; set => SetProperty(ref _redactExports, value); }

    public int RetentionDays { get => _retentionDays; set => SetProperty(ref _retentionDays, value); }

    public string SaveMessage { get => _saveMessage; private set => SetProperty(ref _saveMessage, value); }

    public bool SaveFailed { get => _saveFailed; private set => SetProperty(ref _saveFailed, value); }

    public string RetentionMessage { get => _retentionMessage; private set => SetProperty(ref _retentionMessage, value); }

    public bool Busy
    {
        get => _busy;
        private set => SetProperty(ref _busy, value);
    }

    private async Task LoadAsync()
    {
        try
        {
            var configuration = await _monitoring.LoadAsync().ConfigureAwait(false);
            var preferences = await _preferencesStore.LoadAsync().ConfigureAwait(false);
            Post(() =>
            {
                LoadMonitoring(configuration);
                LoadPreferences(preferences);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load settings.");
            Post(() => ReportResult("Settings could not be loaded.", failed: true));
        }
    }

    private void LoadMonitoring(MonitoringConfiguration c)
    {
        MonitoringEnabled = c.MonitoringEnabled;
        IntervalSeconds = c.IntervalSeconds;
        ExternalTargets = string.Join(Environment.NewLine, c.ExternalIcmpTargets);
        DnsHostname = c.DnsTestHostname;
        HttpsEndpoints = string.Join(Environment.NewLine, c.HttpsEndpoints);
        GatewayTimeout = Text(c.GatewayTimeoutMilliseconds);
        ExternalTimeout = Text(c.ExternalTimeoutMilliseconds);
        DnsTimeout = Text(c.DnsTimeoutMilliseconds);
        HttpsTimeout = Text(c.HttpsTimeoutMilliseconds);
        OutageThreshold = Text(c.OutageConfirmationThreshold);
        RecoveryThreshold = Text(c.RecoveryConfirmationThreshold);
        MergeWindow = Text(c.IncidentMergeWindowSeconds);
    }

    private void LoadPreferences(AppPreferences p)
    {
        LaunchAtLogin = p.LaunchAtLogin;
        StartMinimized = p.StartMinimized;
        CloseToTray = p.CloseToTray;
        NotifyOutage = p.NotifyOnOutage;
        NotifyRecovery = p.NotifyOnRecovery;
        NotifyMonitoring = p.NotifyOnMonitoringProblem;
        RedactExports = p.RedactLocalAddressesInExports;
        RetentionDays = p.RetentionDays;
    }

    private async Task SaveAsync()
    {
        if (!TryBuildConfiguration(out var configuration, out var parseErrors))
        {
            ReportResult(string.Join(" ", parseErrors), failed: true);
            return;
        }

        var errors = MonitoringConfigurationValidator.Validate(configuration);
        if (errors.Count > 0)
        {
            ReportResult(string.Join(" ", errors), failed: true);
            return;
        }

        var preferences = new AppPreferences
        {
            LaunchAtLogin = LaunchAtLogin,
            StartMinimized = StartMinimized,
            CloseToTray = CloseToTray,
            RetentionDays = RetentionDays,
            NotifyOnOutage = NotifyOutage,
            NotifyOnRecovery = NotifyRecovery,
            NotifyOnMonitoringProblem = NotifyMonitoring,
            RedactLocalAddressesInExports = RedactExports,
        };

        Busy = true;
        try
        {
            await _monitoring.SaveAsync(configuration).ConfigureAwait(false);
            await _preferencesService.SaveAsync(preferences).ConfigureAwait(false);
            Post(() => ReportResult("Settings saved and applied.", failed: false));
        }
        catch (InvalidConfigurationException ex)
        {
            Post(() => ReportResult(string.Join(" ", ex.Errors), failed: true));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings.");
            Post(() => ReportResult("Settings could not be saved. Details are in the log.", failed: true));
        }
        finally
        {
            Post(() => Busy = false);
        }
    }

    private async Task PreviewCleanupAsync()
    {
        Busy = true;
        try
        {
            var preview = await _retention.PreviewAsync(RetentionDays).ConfigureAwait(false);
            Post(() => RetentionMessage = DescribePreview(preview));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retention preview failed.");
            Post(() => RetentionMessage = "The preview could not be calculated.");
        }
        finally
        {
            Post(() => Busy = false);
        }
    }

    private async Task CleanupAsync()
    {
        Busy = true;
        try
        {
            var preview = await _retention.PreviewAsync(RetentionDays).ConfigureAwait(false);
            if (preview.CutoffUtc is null || preview.ObservationCount + preview.SnapshotCount == 0)
            {
                Post(() => RetentionMessage = DescribePreview(preview));
                return;
            }

            var confirmed = false;
            Send(() => confirmed = _shell.Confirm(
                "Delete old measurements",
                DescribePreview(preview) + "\n\nDaily summaries, incidents and monitoring gaps are kept. This cannot be undone. Continue?"));
            if (!confirmed)
            {
                return;
            }

            var result = await _retention.CleanupAsync(RetentionDays).ConfigureAwait(false);
            Post(() => RetentionMessage = string.Create(
                CultureInfo.CurrentCulture,
                $"Removed {result.ObservationsDeleted:N0} measurements and {result.SnapshotsDeleted:N0} network snapshots. {result.DaysSummarised} daily summaries were saved first."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retention cleanup failed.");
            Post(() => RetentionMessage = "The cleanup failed. Nothing was partially deleted. Details are in the log.");
        }
        finally
        {
            Post(() => Busy = false);
        }
    }

    private static string DescribePreview(RetentionPreview preview) =>
        preview.CutoffUtc is not { } cutoff
            ? "Retention is set to keep everything, so there is nothing to remove."
            : string.Create(
                CultureInfo.CurrentCulture,
                $"Measurements before {TimeZoneInfo.ConvertTime(cutoff, TimeZoneInfo.Local):yyyy-MM-dd HH:mm} would be removed: {preview.ObservationCount:N0} measurements and {preview.SnapshotCount:N0} network snapshots.");

    private bool TryBuildConfiguration(out MonitoringConfiguration configuration, out List<string> errors)
    {
        var found = new List<string>();
        int Parse(string label, string text)
        {
            if (int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            found.Add($"{label} must be a whole number.");
            return 0;
        }

        configuration = new MonitoringConfiguration
        {
            MonitoringEnabled = MonitoringEnabled,
            IntervalSeconds = IntervalSeconds,
            ExternalIcmpTargets = Lines(ExternalTargets),
            DnsTestHostname = DnsHostname.Trim(),
            HttpsEndpoints = Lines(HttpsEndpoints),
            GatewayTimeoutMilliseconds = Parse("Gateway timeout", GatewayTimeout),
            ExternalTimeoutMilliseconds = Parse("External timeout", ExternalTimeout),
            DnsTimeoutMilliseconds = Parse("DNS timeout", DnsTimeout),
            HttpsTimeoutMilliseconds = Parse("HTTPS timeout", HttpsTimeout),
            OutageConfirmationThreshold = Parse("Outage threshold", OutageThreshold),
            RecoveryConfirmationThreshold = Parse("Recovery threshold", RecoveryThreshold),
            IncidentMergeWindowSeconds = Parse("Merge window", MergeWindow),
        };
        errors = found;
        return found.Count == 0;
    }

    private static List<string> Lines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    private void ReportResult(string message, bool failed)
    {
        SaveMessage = message;
        SaveFailed = failed;
    }

    /// <summary>Runs on the UI thread and waits, for prompts whose answer is needed before continuing.</summary>
    private void Send(Action action)
    {
        if (_uiContext is null)
        {
            action();
        }
        else
        {
            _uiContext.Send(_ => action(), null);
        }
    }

    private void Post(Action action)
    {
        if (_uiContext is null)
        {
            action();
        }
        else
        {
            _uiContext.Post(_ => action(), null);
        }
    }
}
