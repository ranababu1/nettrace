using Microsoft.Win32;
using NetTrace.Core.Monitoring;

namespace NetTrace.Infrastructure.Power;

/// <summary>Bridges <see cref="SystemEvents.PowerModeChanged"/> to <see cref="IPowerEvents"/>.</summary>
public sealed class SystemPowerEvents : IPowerEvents, IDisposable
{
    public SystemPowerEvents() => SystemEvents.PowerModeChanged += OnPowerModeChanged;

    public event EventHandler? Suspending;

    public event EventHandler? Resumed;

    public void Dispose() => SystemEvents.PowerModeChanged -= OnPowerModeChanged;

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                Suspending?.Invoke(this, EventArgs.Empty);
                break;
            case PowerModes.Resume:
                Resumed?.Invoke(this, EventArgs.Empty);
                break;
        }
    }
}
