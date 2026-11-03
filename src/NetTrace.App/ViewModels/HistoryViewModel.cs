using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.Extensions.Logging;
using NetTrace.Core.Incidents;
using NetTrace.Core.Monitoring;
using NetTrace.Core.Statistics;
using NetTrace.Core.Time;

namespace NetTrace.App.ViewModels;

/// <summary>Today's summary and the recent incident list, refreshed from stored data.</summary>
public sealed class HistoryViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);
    private const int IncidentHistoryDays = 7;

    private readonly IObservationRepository _observations;
    private readonly IIncidentRepository _incidents;
    private readonly IMonitoringGapRepository _gaps;
    private readonly IMonitoringStatusSource _statusSource;
    private readonly IClock _clock;
    private readonly ILogger<HistoryViewModel> _logger;
    private readonly SynchronizationContext? _uiContext;
    private readonly CancellationTokenSource _disposed = new();
    private int _refreshing;
    private DateTimeOffset _lastRefreshAt = DateTimeOffset.MinValue;
    private Guid? _lastIncidentId;
    private bool _lastSuspected;

    private string _uptime = DurationFormatter.NotAvailable;
    private string _coverage = DurationFormatter.NotAvailable;
    private string _incidentCount = DurationFormatter.NotAvailable;
    private string _outageTime = DurationFormatter.NotAvailable;
    private string _unmonitoredTime = DurationFormatter.NotAvailable;

    public HistoryViewModel(
        IObservationRepository observations,
        IIncidentRepository incidents,
        IMonitoringGapRepository gaps,
        IMonitoringStatusSource statusSource,
        IClock clock,
        ILogger<HistoryViewModel> logger)
    {
        _observations = observations;
        _incidents = incidents;
        _gaps = gaps;
        _statusSource = statusSource;
        _clock = clock;
        _logger = logger;
        // Created on the UI thread; refresh results are posted back to it.
        _uiContext = SynchronizationContext.Current;

        statusSource.StatusChanged += OnStatusChanged;
        _ = RefreshAsync();
    }

    public string Uptime { get => _uptime; private set => SetProperty(ref _uptime, value); }

    public string Coverage { get => _coverage; private set => SetProperty(ref _coverage, value); }

    public string IncidentCount { get => _incidentCount; private set => SetProperty(ref _incidentCount, value); }

    public string OutageTime { get => _outageTime; private set => SetProperty(ref _outageTime, value); }

    public string UnmonitoredTime { get => _unmonitoredTime; private set => SetProperty(ref _unmonitoredTime, value); }

    public ObservableCollection<IncidentRowViewModel> Incidents { get; } = [];

    public bool HasIncidents => Incidents.Count > 0;

    public string UptimeDefinition =>
        "Uptime is healthy time divided by monitored time. Time when NetTrace was not monitoring (app closed, "
        + "computer asleep, monitoring off) counts as neither uptime nor downtime.";

    public void Dispose()
    {
        _statusSource.StatusChanged -= OnStatusChanged;
        _disposed.Cancel();
        _disposed.Dispose();
    }

    private void OnStatusChanged(object? sender, MonitoringStatus status)
    {
        var incidentChanged = status.OpenIncident?.Id != _lastIncidentId || status.IncidentSuspected != _lastSuspected;
        _lastIncidentId = status.OpenIncident?.Id;
        _lastSuspected = status.IncidentSuspected;
        if (incidentChanged || _clock.UtcNow - _lastRefreshAt >= RefreshInterval)
        {
            _ = RefreshAsync();
        }
    }

    private async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        try
        {
            var token = _disposed.Token;
            var now = _clock.UtcNow;
            var localNow = TimeZoneInfo.ConvertTime(now, _clock.LocalTimeZone);
            var dayStart = new DateTimeOffset(localNow.Date, _clock.LocalTimeZone.GetUtcOffset(localNow.Date));
            var dayEnd = dayStart.AddDays(1);
            var historyStart = dayStart.AddDays(-(IncidentHistoryDays - 1));

            var observations = await _observations.GetObservationsAsync(dayStart, dayEnd, token).ConfigureAwait(false);
            var gaps = await _gaps.GetOverlappingAsync(dayStart, dayEnd, token).ConfigureAwait(false);
            var incidents = await _incidents.GetOverlappingAsync(historyStart, dayEnd, token).ConfigureAwait(false);

            var stats = DailyStatisticsCalculator.Calculate(dayStart, dayEnd, now, observations, incidents, gaps);
            var rows = incidents
                .OrderByDescending(i => i.StartedAtUtc)
                .Select(i => IncidentRowViewModel.From(i, now, _clock.LocalTimeZone))
                .ToList();

            _lastRefreshAt = now;
            Post(() => Apply(stats, rows));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh the history summary.");
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private void Apply(DailyStatistics stats, List<IncidentRowViewModel> rows)
    {
        Uptime = DurationFormatter.FormatPercentage(stats.UptimePercentage);
        Coverage = DurationFormatter.FormatPercentage(stats.MonitoringCoveragePercentage);
        IncidentCount = stats.IncidentCount.ToString(CultureInfo.CurrentCulture);
        OutageTime = DurationFormatter.Format(stats.OutageSeconds);
        UnmonitoredTime = DurationFormatter.Format(stats.UnmonitoredSeconds);

        Incidents.Clear();
        foreach (var row in rows)
        {
            Incidents.Add(row);
        }

        OnPropertyChanged(nameof(HasIncidents));
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

/// <summary>One incident for the list. State is spelled out in text, not conveyed by colour alone.</summary>
public sealed record IncidentRowViewModel(
    string Title, string Explanation, string When, string Duration, string Status, string Confidence, string Evidence)
{
    public static IncidentRowViewModel From(Incident incident, DateTimeOffset nowUtc, TimeZoneInfo timeZone)
    {
        string Local(DateTimeOffset utc) =>
            TimeZoneInfo.ConvertTime(utc, timeZone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);

        var ongoing = incident.EndedAtUtc is null;
        var end = incident.EndedAtUtc ?? nowUtc;
        var when = ongoing
            ? Local(incident.StartedAtUtc) + " – now"
            : Local(incident.StartedAtUtc) + " – " + Local(end);
        var failed = incident.FailedProbes.Count > 0 ? "Not responding: " + string.Join(", ", incident.FailedProbes) + ". " : string.Empty;
        var working = incident.WorkingProbes.Count > 0 ? "Responding: " + string.Join(", ", incident.WorkingProbes) + "." : string.Empty;

        return new IncidentRowViewModel(
            IncidentText.Title(incident.Classification),
            IncidentText.Explanation(incident.Classification),
            when,
            DurationFormatter.Format((end - incident.StartedAtUtc).TotalSeconds),
            ongoing ? "Ongoing" : "Resolved",
            incident.Confidence + " confidence",
            (failed + working).Trim());
    }
}
