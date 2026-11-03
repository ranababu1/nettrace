namespace NetTrace.Core.Monitoring;

/// <summary>Operating system sleep/resume notifications.</summary>
public interface IPowerEvents
{
    /// <summary>Raised when the system is about to sleep or hibernate.</summary>
    event EventHandler? Suspending;

    /// <summary>Raised when the system has resumed from sleep or hibernation.</summary>
    event EventHandler? Resumed;
}
