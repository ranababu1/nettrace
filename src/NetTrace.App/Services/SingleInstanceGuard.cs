namespace NetTrace.App.Services;

/// <summary>
/// Ensures one NetTrace instance per user session. A second launch signals the running
/// instance to bring its window forward and then exits.
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\NetTrace.SingleInstance";
    private const string ActivateEventName = @"Local\NetTrace.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activateEvent;
    private readonly RegisteredWaitHandle _registration;
    private bool _disposed;

    private SingleInstanceGuard(Mutex mutex)
    {
        _mutex = mutex;
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _activateEvent,
            (_, _) => ActivationRequested?.Invoke(),
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    /// <summary>Raised on a thread-pool thread when another launch asks this instance to activate.</summary>
    public event Action? ActivationRequested;

    public static SingleInstanceGuard? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }

        return new SingleInstanceGuard(mutex);
    }

    public static void SignalExistingInstance()
    {
        if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var activateEvent))
        {
            using (activateEvent)
            {
                activateEvent.Set();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _registration.Unregister(null);
        _activateEvent.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by the calling thread; the OS releases it when the process exits.
        }

        _mutex.Dispose();
    }
}
