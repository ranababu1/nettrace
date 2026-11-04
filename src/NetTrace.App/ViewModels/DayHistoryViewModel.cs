using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using NetTrace.Core.Incidents;
using NetTrace.Core.Monitoring;
using NetTrace.Core.Statistics;
using NetTrace.Core.Time;

namespace NetTrace.App.ViewModels;

public sealed record DayRowViewModel(
    string Date, string Uptime, string Coverage, string Incidents, string Downtime, string Unmonitored, string Detail);

/// <summary>Per-day health for the last 7 or 30 days. Finished days are cached; today is recalculated.</summary>
public sealed class DayHistoryViewModel : ObservableObject
{
    private readonly IObservationRepository _observations;
    private readonly IIncidentRepository _incidents;
    private readonly IMonitoringGapRepository _gaps;
    private readonly IClock _clock;
    private readonly ILogger<DayHistoryViewModel> _logger;
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;
    private readonly Dictionary<DateTime, DailyStatistics> _finishedDays = [];
    private int _days = 7;
    private bool _busy;
    private string _message = "Press Refresh to load history.";

    public DayHistoryViewModel(
        IObservationRepository observations,
        IIncidentRepository incidents,
        IMonitoringGapRepository gaps,
        IClock clock,
        ILogger<DayHistoryViewModel> logger)
    {
        _observations = observations;
        _incidents = incidents;
        _gaps = gaps;
        _clock = clock;
        _logger = logger;
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => !Busy);
    }

    public ObservableCollection<DayRowViewModel> Rows { get; } = [];

    public IReadOnlyList<ChoiceItem> RangeChoices { get; } = [new("Last 7 days", 7), new("Last 30 days", 30)];

    public ICommand RefreshCommand { get; }

    public int Days { get => _days; set { if (SetProperty(ref _days, value)) { _ = RefreshAsync(); } } }

    public bool Busy { get => _busy; private set => SetProperty(ref _busy, value); }

    public string Message { get => _message; private set => SetProperty(ref _message, value); }

    public string UptimeDefinition =>
        "Uptime is healthy time divided by monitored time. Time when NetTrace was not monitoring counts as neither uptime nor downtime.";

    public async Task RefreshAsync()
    {
        if (Busy)
        {
            return;
        }

        Busy = true;
        Post(() => Message = "Loading...");
        try
        {
            var zone = _clock.LocalTimeZone;
            var now = _clock.UtcNow;
            var today = TimeZoneInfo.ConvertTime(now, zone).Date;
            var rows = new List<DayRowViewModel>();
            for (var offset = 0; offset < _days; offset++)
            {
                var day = today.AddDays(-offset);
                var stats = await StatsForAsync(day, today, zone, now).ConfigureAwait(false);
                rows.Add(ToRow(day, stats, zone));
            }

            Post(() =>
            {
                Rows.Clear();
                foreach (var row in rows)
                {
                    Rows.Add(row);
                }

                Message = rows.Count == 0 ? "No data." : string.Empty;
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to load day history.");
            Post(() => Message = "History could not be loaded. Details are in the log.");
        }
        finally
        {
            Post(() => Busy = false);
        }
    }

    private async Task<DailyStatistics> StatsForAsync(DateTime day, DateTime today, TimeZoneInfo zone, DateTimeOffset now)
    {
        if (day < today && _finishedDays.TryGetValue(day, out var cached))
        {
            return cached;
        }

        var start = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day, DateTimeKind.Unspecified), zone), TimeSpan.Zero);
        var end = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day.AddDays(1), DateTimeKind.Unspecified), zone), TimeSpan.Zero);
        var observations = await _observations.GetObservationsAsync(start, end).ConfigureAwait(false);
        var incidents = await _incidents.GetOverlappingAsync(start, end).ConfigureAwait(false);
        var gaps = await _gaps.GetOverlappingAsync(start, end).ConfigureAwait(false);
        var stats = DailyStatisticsCalculator.Calculate(start, end, now, observations, incidents, gaps);
        if (day < today)
        {
            _finishedDays[day] = stats;
        }

        return stats;
    }

    private static DayRowViewModel ToRow(DateTime day, DailyStatistics s, TimeZoneInfo zone)
    {
        var worst = s.Series
            .Where(x => x.PacketLossPercentage is > 0)
            .OrderByDescending(x => x.PacketLossPercentage)
            .FirstOrDefault();
        var detail = s.ObservedSeconds <= 0
            ? "Not monitored"
            : worst is null
                ? "No packet loss recorded"
                : string.Create(CultureInfo.CurrentCulture, $"Most loss: {worst.PacketLossPercentage:0.#} % ({worst.ProbeType} {worst.Target})");
        return new DayRowViewModel(
            day.ToString("ddd yyyy-MM-dd", CultureInfo.CurrentCulture),
            DurationFormatter.FormatPercentage(s.UptimePercentage),
            DurationFormatter.FormatPercentage(s.MonitoringCoveragePercentage),
            s.IncidentCount.ToString(CultureInfo.CurrentCulture),
            DurationFormatter.Format(s.OutageSeconds),
            DurationFormatter.Format(s.UnmonitoredSeconds),
            detail);
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
