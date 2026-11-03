using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using NetTrace.App.Services;
using NetTrace.Core.Monitoring;
using NetTrace.Core.Network;
using NetTrace.Core.Probes;
using NetTrace.Core.Throughput;
using NetTrace.Core.Time;
using NetTrace.Infrastructure.Storage;

namespace NetTrace.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const string NotMeasured = "--";

    private readonly IMonitoringStatusSource _statusSource;
    private readonly IThroughputSource _throughput;
    private readonly IClock _clock;
    private readonly SynchronizationContext? _uiContext;
    private string _monitoringState = string.Empty;
    private string _monitoringStateDescription = string.Empty;
    private string _monitoringStateGlyph = string.Empty;
    private string _connectionText = "Looking for a connection";
    private string _connectionGlyph = "";
    private string _gatewayText = "Not measured yet";
    private string _lastSuccessText = "None yet";
    private string? _lastError;
    private string _downloadValue = NotMeasured;
    private string _downloadUnit = string.Empty;
    private string _uploadValue = NotMeasured;
    private string _uploadUnit = string.Empty;
    private string _throughputCaption = "Measuring";
    private string _routerLatency = NotMeasured;
    private string _routerCaption = "Not measured yet";
    private string _internetLatency = NotMeasured;
    private string _internetCaption = "Not measured yet";

    public MainViewModel(
        AppDataPaths paths,
        IClock clock,
        IApplicationShell shell,
        IMonitoringStatusSource statusSource,
        IThroughputSource throughput,
        HistoryViewModel history)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(statusSource);
        ArgumentNullException.ThrowIfNull(throughput);
        ArgumentNullException.ThrowIfNull(history);

        _clock = clock;
        _statusSource = statusSource;
        _throughput = throughput;
        History = history;
        // Created on the UI thread; updates arrive on the thread pool and are posted back.
        _uiContext = SynchronizationContext.Current;

        DataDirectory = paths.RootDirectory;
        // DisplayName already carries the UTC offset in parentheses, e.g. "(UTC+05:30) Chennai, ...".
        StartedAtText = FormatLocal(clock.UtcNow) + "  " + clock.LocalTimeZone.DisplayName;
        ExitCommand = new RelayCommand(shell.RequestExit);
        OpenDataFolderCommand = new RelayCommand(() => shell.OpenFolder(DataDirectory));

        Apply(statusSource.Current);
        if (throughput.Latest is { } sample)
        {
            Apply(sample);
        }

        statusSource.StatusChanged += OnStatusChanged;
        throughput.SampleAvailable += OnSampleAvailable;
    }

    public HistoryViewModel History { get; }

    public string Title => "NetTrace";

    public string ApplicationVersion => "v" + ApplicationInfo.Version;

    public string DataDirectory { get; }

    public string StartedAtText { get; }

    public string MonitoringState { get => _monitoringState; private set => SetProperty(ref _monitoringState, value); }

    public string MonitoringStateDescription
    {
        get => _monitoringStateDescription;
        private set => SetProperty(ref _monitoringStateDescription, value);
    }

    public string MonitoringStateGlyph { get => _monitoringStateGlyph; private set => SetProperty(ref _monitoringStateGlyph, value); }

    public string ConnectionText { get => _connectionText; private set => SetProperty(ref _connectionText, value); }

    public string ConnectionGlyph { get => _connectionGlyph; private set => SetProperty(ref _connectionGlyph, value); }

    public string GatewayText { get => _gatewayText; private set => SetProperty(ref _gatewayText, value); }

    public string LastSuccessText { get => _lastSuccessText; private set => SetProperty(ref _lastSuccessText, value); }

    public string? LastError
    {
        get => _lastError;
        private set
        {
            if (SetProperty(ref _lastError, value))
            {
                OnPropertyChanged(nameof(HasLastError));
            }
        }
    }

    public bool HasLastError => !string.IsNullOrEmpty(LastError);

    public string DownloadValue { get => _downloadValue; private set => SetProperty(ref _downloadValue, value); }

    public string DownloadUnit { get => _downloadUnit; private set => SetProperty(ref _downloadUnit, value); }

    public string UploadValue { get => _uploadValue; private set => SetProperty(ref _uploadValue, value); }

    public string UploadUnit { get => _uploadUnit; private set => SetProperty(ref _uploadUnit, value); }

    public string ThroughputCaption { get => _throughputCaption; private set => SetProperty(ref _throughputCaption, value); }

    public string RouterLatency { get => _routerLatency; private set => SetProperty(ref _routerLatency, value); }

    public string RouterCaption { get => _routerCaption; private set => SetProperty(ref _routerCaption, value); }

    public string InternetLatency { get => _internetLatency; private set => SetProperty(ref _internetLatency, value); }

    public string InternetCaption { get => _internetCaption; private set => SetProperty(ref _internetCaption, value); }

    public ObservableCollection<ProbeRowViewModel> LatestMeasurements { get; } = [];

    public ICommand ExitCommand { get; }

    public ICommand OpenDataFolderCommand { get; }

    public void Dispose()
    {
        _statusSource.StatusChanged -= OnStatusChanged;
        _throughput.SampleAvailable -= OnSampleAvailable;
        History.Dispose();
    }

    private void OnStatusChanged(object? sender, MonitoringStatus status) => Post(() => Apply(status));

    private void OnSampleAvailable(object? sender, ThroughputSample sample) => Post(() => Apply(sample));

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

    private void Apply(ThroughputSample sample)
    {
        var unit = _throughput.Configuration.Unit;
        (DownloadValue, DownloadUnit) = SplitRate(sample.IsAvailable ? sample.DownloadBytesPerSecond : null, unit);
        (UploadValue, UploadUnit) = SplitRate(sample.IsAvailable ? sample.UploadBytesPerSecond : null, unit);
        ThroughputCaption = sample.IsAvailable
            ? "Current usage on " + RateFormatter.DescribeKind(sample.InterfaceKind)
            : sample.UnavailableReason ?? "Unavailable";
    }

    private string DescribeIncident(Core.Incidents.Incident incident)
    {
        var what = incident.Classification switch
        {
            Core.Incidents.IncidentClassification.LocalInterfaceDisconnected => "This computer has no connected network.",
            Core.Incidents.IncidentClassification.GatewayUnreachable => "Your router is not responding.",
            Core.Incidents.IncidentClassification.ExternalConnectivityUnavailable => "Your router responds but the internet does not.",
            Core.Incidents.IncidentClassification.DnsResolutionFailure => "The internet is reachable but website names are not resolving.",
            Core.Incidents.IncidentClassification.HttpsConnectivityFailure => "The internet is reachable but websites are not loading.",
            _ => "Connectivity problem.",
        };
        return what + " Since " + FormatLocal(incident.StartedAtUtc) + ".";
    }

    private void Apply(MonitoringStatus status)
    {
        (MonitoringState, MonitoringStateGlyph, MonitoringStateDescription) = status.State switch
        {
            Core.Monitoring.MonitoringState.Running => (
                "Monitoring",
                "",
                "Checking your router and the internet in the background. No problems detected."),
            Core.Monitoring.MonitoringState.Disabled => (
                "Monitoring paused",
                "",
                "Monitoring is turned off in settings. This period is recorded as unmonitored."),
            Core.Monitoring.MonitoringState.Faulted => (
                "Monitoring stopped",
                "",
                "The monitoring engine stopped because of an error. Restart NetTrace; details are in the log."),
            Core.Monitoring.MonitoringState.Stopped => ("Stopped", "", "Monitoring has stopped."),
            _ => ("Starting", "", "Preparing the first measurement."),
        };

        if (status.State == Core.Monitoring.MonitoringState.Running)
        {
            if (status.OpenIncident is { } incident)
            {
                MonitoringState = "Problem detected";
                MonitoringStateDescription = DescribeIncident(incident);
            }
            else if (status.IncidentSuspected)
            {
                MonitoringState = "Checking a possible problem";
                MonitoringStateDescription = "Some measurements failed. NetTrace waits for several in a row before calling it an outage.";
            }
        }

        LastSuccessText = status.LastSuccessfulMeasurementAtUtc is { } success ? FormatLocal(success) : "None yet";
        LastError = status.LastError;

        if (status.LastCycle is not { } cycle)
        {
            return;
        }

        var nic = cycle.NetworkContext.ActiveInterface;
        ConnectionText = nic is null ? "No active connection" : RateFormatter.DescribeKind(nic.Kind) + " · " + nic.Description;
        ConnectionGlyph = nic?.Kind switch
        {
            InterfaceKind.Wifi => "",
            InterfaceKind.Ethernet => "",
            InterfaceKind.Cellular => "",
            null => "",
            _ => "",
        };
        GatewayText = cycle.NetworkContext.DefaultGateway ?? "None found";

        var gateway = cycle.Results.FirstOrDefault(r => r.ProbeType == ProbeType.Gateway);
        RouterLatency = gateway is { Succeeded: true, LatencyMs: { } routerMs } ? FormatMs(routerMs) : NotMeasured;
        RouterCaption = gateway switch
        {
            null => "Not measured",
            { Succeeded: true } => "ms · " + gateway.Target,
            { Outcome: ProbeOutcome.Skipped } => "No router found",
            _ => "No reply from router",
        };

        var external = cycle.Results.Where(r => r.ProbeType == ProbeType.ExternalIcmp).ToList();
        var replies = external.Where(r => r is { Succeeded: true, LatencyMs: not null }).Select(r => r.LatencyMs!.Value).ToList();
        InternetLatency = replies.Count > 0 ? FormatMs(replies.Average()) : NotMeasured;
        InternetCaption = external.Count == 0
            ? "Not measured"
            : string.Create(CultureInfo.CurrentCulture, $"ms avg · {replies.Count} of {external.Count} replied");

        LatestMeasurements.Clear();
        foreach (var result in cycle.Results)
        {
            LatestMeasurements.Add(ProbeRowViewModel.From(result, FormatLocal(result.StartedAtUtc)));
        }
    }

    /// <summary>Splits "30k" into ("30", "KB/s"); unavailable stays "--" rather than zero.</summary>
    internal static (string Value, string Unit) SplitRate(double? bytesPerSecond, RateUnit unit)
    {
        var text = RateFormatter.Format(bytesPerSecond, unit);
        if (text == RateFormatter.UnavailableText)
        {
            return (NotMeasured, string.Empty);
        }

        var bits = unit == RateUnit.BitsPerSecond;
        return text[^1] == 'm'
            ? (text[..^1], bits ? "Mbps" : "MB/s")
            : (text[..^1], bits ? "Kbps" : "KB/s");
    }

    private static string FormatMs(double ms) =>
        ms < 1 ? "<1" : Math.Round(ms).ToString("0", CultureInfo.CurrentCulture);

    private string FormatLocal(DateTimeOffset utc) =>
        TimeZoneInfo.ConvertTime(utc, _clock.LocalTimeZone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
}

/// <summary>One probe's latest result, with text that never relies on colour alone.</summary>
public sealed record ProbeRowViewModel(string Check, string Target, string Result, string Glyph, string MeasuredAt)
{
    public static ProbeRowViewModel From(ProbeResult result, string measuredAtLocal)
    {
        var check = result.ProbeType switch
        {
            ProbeType.Gateway => "Router",
            ProbeType.ExternalIcmp => "Internet",
            ProbeType.Dns => "DNS",
            ProbeType.Https => "HTTPS",
            _ => result.ProbeType.ToString(),
        };

        var (text, glyph) = result.Outcome switch
        {
            ProbeOutcome.Success when result.LatencyMs is { } ms =>
                (ms < 1 ? "Reachable · <1 ms" : string.Create(CultureInfo.CurrentCulture, $"Reachable · {ms:0} ms"), ""),
            ProbeOutcome.Success => ("Reachable", ""),
            ProbeOutcome.Timeout => (string.Create(CultureInfo.CurrentCulture, $"No reply within {result.TimeoutMilliseconds} ms"), ""),
            ProbeOutcome.Skipped => ("Not measured: " + (result.ErrorMessage ?? "unavailable"), ""),
            _ => ("Failed: " + (result.ErrorMessage ?? "unknown error"), ""),
        };

        return new ProbeRowViewModel(check, result.Target, text, glyph, measuredAtLocal);
    }
}
