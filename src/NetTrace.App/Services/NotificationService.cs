using Microsoft.Extensions.Logging;
using NetTrace.Core.Configuration;
using NetTrace.Core.Incidents;
using NetTrace.Core.Monitoring;
using NetTrace.Core.Notifications;
using NetTrace.Core.Time;

namespace NetTrace.App.Services;

/// <summary>Turns incident and status events into tray notifications using <see cref="NotificationPolicy"/>.</summary>
public sealed class NotificationService(
    IIncidentFeed incidentFeed,
    IMonitoringStatusSource statusSource,
    IAppPreferencesStore preferences,
    IClock clock,
    ILogger<NotificationService> logger) : IDisposable
{
    private readonly NotificationPolicy _policy = new();
    private readonly object _gate = new();
    private TrayIconService? _tray;

    public void Attach(TrayIconService tray)
    {
        _tray = tray;
        incidentFeed.IncidentChanged += OnIncident;
        statusSource.StatusChanged += OnStatus;
    }

    public void Dispose()
    {
        incidentFeed.IncidentChanged -= OnIncident;
        statusSource.StatusChanged -= OnStatus;
    }

    private void OnIncident(object? sender, IncidentChange change) => Show(() => _policy.Decide(change, preferences.Current, clock.UtcNow));

    private void OnStatus(object? sender, MonitoringStatus status) => Show(() => _policy.Decide(status, preferences.Current, clock.UtcNow));

    private void Show(Func<Notification?> decide)
    {
        Notification? notification;
        lock (_gate)
        {
            notification = decide();
        }

        if (notification is null || _tray is null)
        {
            return;
        }

        logger.LogInformation("Notification: {Kind}: {Title}", notification.Kind, notification.Title);
        _tray.ShowNotification(notification);
    }
}
