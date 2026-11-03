namespace NetTrace.Core.Probes;

public interface IDnsProbe
{
    /// <summary>
    /// Resolves <paramref name="hostname"/> with the system resolver. Never throws for network
    /// failures; they are reported in the result. Throws only for cancellation.
    /// </summary>
    Task<ProbeResult> ResolveAsync(string hostname, int timeoutMilliseconds, CancellationToken cancellationToken);
}

public interface IHttpsProbe
{
    /// <summary>
    /// Issues one HTTPS GET to <paramref name="endpoint"/> and measures time to response headers.
    /// Never throws for network failures; they are reported in the result. Throws only for cancellation.
    /// </summary>
    Task<ProbeResult> GetAsync(string endpoint, int timeoutMilliseconds, CancellationToken cancellationToken);
}
