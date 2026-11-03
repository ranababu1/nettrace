using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using NetTrace.App.Services;
using NetTrace.Core.Monitoring;
using NetTrace.Core.Network;
using NetTrace.Core.Probes;
using NetTrace.Core.Time;
using NetTrace.Infrastructure.Storage;

namespace NetTrace.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly IMonitoringStatusSource _statusSource;
    private readonly IClock _clock;
    private readonly SynchronizationContext? _uiContext;
    private string _monitoringState = string.Empty;
    private string _monitoringStateDescription = string.Empty;
    private string _monitoringStateGlyph = string.Empty;
    private string _connectionText = "Not measured yet";
    private string _gatewayText = "Not measured yet";
    private string _lastSuccessText = "None yet";
    private string? _lastError;

    public MainViewModel(AppDataPaths paths, IClock clock, IApplicationShell shell, IMonitoringStatusSource statusSource)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(statusSource);

        _clock = clock;
        _statusSource = statusSource;
        // Created on the UI thread; status updates arrive on the thread pool and are posted back.
        _uiContext = SynchronizationContext.Current;

        DataDirectory = paths.RootDirectory;
        // DisplayName already carries the UTC offset in parentheses, e.g. "(UTC+05:30) Chennai, ...".
        StartedAtText = FormatLocal(clock.UtcNow) + "  " + clock.LocalTimeZone.DisplayName;
        ExitCommand = new RelayCommand(shell.RequestExit);
        OpenDataFolderCommand = new RelayCommand(() => shell.OpenFolder(DataDirectory));

        Apply(statusSource.Current);
        statusSource.StatusChanged += OnStatusChanged;
    }

    public string Title => "NetTrace";

    public string ApplicationVersion => ApplicationInfo.Version;

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

    public ObservableCollection<ProbeRowViewModel> LatestMeasurements { get; } = [];

    public ICommand ExitCommand { get; }

    public ICommand OpenDataFolderCommand { get; }

    public void Dispose() => _statusSource.StatusChanged -= OnStatusChanged;

    private void OnStatusChanged(object? sender, MonitoringStatus status)
    {
        if (_uiContext is null)
        {
            Apply(status);
        }
        else
        {
            _uiContext.Post(_ => Apply(status), null);
        }
    }

    private void Apply(MonitoringStatus status)
    {
        (MonitoringState, MonitoringStateGlyph, MonitoringStateDescription) = status.State switch
        {
            Core.Monitoring.MonitoringState.Running => (
                "Monitoring",
                "",
                "Checks run in the background. These are raw measurements; outage detection is not available in this build yet."),
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

        LastSuccessText = status.LastSuccessfulMeasurementAtUtc is { } success ? FormatLocal(success) : "None yet";
        LastError = status.LastError;

        if (status.LastCycle is not { } cycle)
        {
            return;
        }

        ConnectionText = DescribeConnection(cycle.NetworkContext.ActiveInterface);
        GatewayText = cycle.NetworkContext.DefaultGateway ?? "None found";

        LatestMeasurements.Clear();
        foreach (var result in cycle.Results)
        {
            LatestMeasurements.Add(ProbeRowViewModel.From(result, FormatLocal(result.StartedAtUtc)));
        }
    }

    private string FormatLocal(DateTimeOffset utc) =>
        TimeZoneInfo.ConvertTime(utc, _clock.LocalTimeZone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);

    private static string DescribeConnection(NetworkInterfaceSnapshot? nic) => nic is null
        ? "No active connection"
        : $"{DescribeKind(nic.Kind)}: {nic.Description}";

    private static string DescribeKind(InterfaceKind kind) => kind switch
    {
        InterfaceKind.Wifi => "Wi-Fi",
        InterfaceKind.Ethernet => "Ethernet",
        InterfaceKind.Cellular => "Cellular",
        _ => "Other",
    };
}

/// <summary>One probe's latest result, with text that never relies on colour alone.</summary>
public sealed record ProbeRowViewModel(string Check, string Target, string Result, string Glyph, string MeasuredAt)
{
    public static ProbeRowViewModel From(ProbeResult result, string measuredAtLocal)
    {
        var check = result.ProbeType switch
        {
            ProbeType.Gateway => "Router (gateway)",
            ProbeType.ExternalIcmp => "Internet (ping)",
            ProbeType.Dns => "DNS",
            ProbeType.Https => "HTTPS",
            _ => result.ProbeType.ToString(),
        };

        var (text, glyph) = result.Outcome switch
        {
            ProbeOutcome.Success when result.LatencyMs is { } ms =>
                (ms < 1 ? "Reachable, <1 ms" : string.Create(CultureInfo.CurrentCulture, $"Reachable, {ms:0} ms"), ""),
            ProbeOutcome.Success => ("Reachable", ""),
            ProbeOutcome.Timeout => (string.Create(CultureInfo.CurrentCulture, $"No reply within {result.TimeoutMilliseconds} ms"), ""),
            ProbeOutcome.Skipped => ("Not measured: " + (result.ErrorMessage ?? "unavailable"), ""),
            _ => ("Failed: " + (result.ErrorMessage ?? "unknown error"), ""),
        };

        return new ProbeRowViewModel(check, result.Target, text, glyph, measuredAtLocal);
    }
}
