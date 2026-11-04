using System.Windows.Input;
using Microsoft.Extensions.Logging;
using NetTrace.App.Services;
using NetTrace.Core.Configuration;
using NetTrace.Reporting;

namespace NetTrace.App.ViewModels;

public sealed record DetailChoice(string Label, ReportDetail Value);

public sealed record FormatChoice(string Label, ExportFormat Value);

/// <summary>Create a report for a day or date range and verify a report folder's checksums.</summary>
public sealed class ReportsViewModel : ObservableObject
{
    private readonly ReportService _reports;
    private readonly IApplicationShell _shell;
    private readonly ILogger<ReportsViewModel> _logger;
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;
    private DateTime _first;
    private DateTime _last;
    private ReportDetail _detail = ReportDetail.Summary;
    private ExportFormat _format = ExportFormat.Json;
    private bool _redact;
    private string _message = string.Empty;
    private bool _failed;
    private bool _busy;
    private string? _lastFolder;

    public ReportsViewModel(ReportService reports, IAppPreferencesStore preferences, IApplicationShell shell, ILogger<ReportsViewModel> logger)
    {
        _reports = reports;
        _shell = shell;
        _logger = logger;
        var today = reports.Today.ToDateTime(TimeOnly.MinValue);
        _first = today;
        _last = today;
        _redact = preferences.Current.RedactLocalAddressesInExports;
        preferences.Changed += (_, p) => Post(() => Redact = p.RedactLocalAddressesInExports);

        ExportCommand = new RelayCommand(() => _ = ExportAsync(), () => !Busy);
        VerifyCommand = new RelayCommand(() => _ = VerifyAsync(), () => !Busy && _lastFolder is not null);
        OpenFolderCommand = new RelayCommand(() => _shell.OpenFolder(_lastFolder ?? _reports.ExportsDirectory));
        TodayCommand = new RelayCommand(() => SetRange(0));
        Last7Command = new RelayCommand(() => SetRange(6));
        Last30Command = new RelayCommand(() => SetRange(29));
    }

    public IReadOnlyList<DetailChoice> DetailChoices { get; } =
    [
        new("Summary (statistics, incidents, gaps)", ReportDetail.Summary),
        new("Incidents only", ReportDetail.IncidentsOnly),
        new("Detailed (includes every measurement)", ReportDetail.Detailed),
    ];

    public IReadOnlyList<FormatChoice> FormatChoices { get; } =
    [
        new("JSON (one file)", ExportFormat.Json),
        new("CSV (several files)", ExportFormat.Csv),
    ];

    public ICommand ExportCommand { get; }

    public ICommand VerifyCommand { get; }

    public ICommand OpenFolderCommand { get; }

    public ICommand TodayCommand { get; }

    public ICommand Last7Command { get; }

    public ICommand Last30Command { get; }

    public DateTime First { get => _first; set => SetProperty(ref _first, value); }

    public DateTime Last { get => _last; set => SetProperty(ref _last, value); }

    public ReportDetail Detail { get => _detail; set => SetProperty(ref _detail, value); }

    public ExportFormat Format { get => _format; set => SetProperty(ref _format, value); }

    public bool Redact { get => _redact; set => SetProperty(ref _redact, value); }

    public string Message { get => _message; private set => SetProperty(ref _message, value); }

    public bool Failed { get => _failed; private set => SetProperty(ref _failed, value); }

    public bool Busy { get => _busy; private set => SetProperty(ref _busy, value); }

    private void SetRange(int daysBack)
    {
        var today = _reports.Today.ToDateTime(TimeOnly.MinValue);
        Last = today;
        First = today.AddDays(-daysBack);
    }

    private async Task ExportAsync()
    {
        if (Last.Date < First.Date)
        {
            Show("The last day cannot be before the first day.", failed: true);
            return;
        }

        Busy = true;
        Show("Creating the report...", failed: false);
        try
        {
            var result = await _reports.ExportAsync(
                DateOnly.FromDateTime(First), DateOnly.FromDateTime(Last), Detail, Format, Redact).ConfigureAwait(false);
            _lastFolder = result.Directory;
            Post(() => Show($"Report saved to {result.Directory} ({result.Files.Count} files, checksums in manifest.json).", failed: false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Report export failed.");
            Post(() => Show("The report could not be created. Details are in the log.", failed: true));
        }
        finally
        {
            Post(() => Busy = false);
        }
    }

    private async Task VerifyAsync()
    {
        if (_lastFolder is not { } folder)
        {
            return;
        }

        Busy = true;
        try
        {
            var results = await ExportManifestFile.VerifyAsync(folder).ConfigureAwait(false);
            var bad = results.Where(r => !r.Matches).ToList();
            Post(() => Show(
                bad.Count == 0
                    ? $"All {results.Count} files match their recorded SHA-256 checksums. (This shows the files are unchanged since export; it does not prove the measurements themselves.)"
                    : "Changed or missing: " + string.Join(", ", bad.Select(b => $"{b.Name} ({b.Problem})")),
                failed: bad.Count > 0));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Export verification failed.");
            Post(() => Show("The report could not be verified.", failed: true));
        }
        finally
        {
            Post(() => Busy = false);
        }
    }

    /// <summary>Called by the tray's "Export today's report".</summary>
    public void NotifyExternalExport(ExportResult result)
    {
        _lastFolder = result.Directory;
        Post(() => Show($"Report saved to {result.Directory}.", failed: false));
    }

    private void Show(string message, bool failed)
    {
        Message = message;
        Failed = failed;
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
