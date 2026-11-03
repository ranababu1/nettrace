using System.Diagnostics;
using System.Net.Http;
using NetTrace.Core.Probes;
using NetTrace.Core.Time;

namespace NetTrace.Infrastructure.Probes;

/// <summary>HTTPS GET probe measuring time to response headers. Redirects are not followed.</summary>
public sealed class HttpsProbe : IHttpsProbe, IDisposable
{
    public const string Method = "HTTPS GET, time to response headers";

    private readonly IClock _clock;
    private readonly HttpClient _client;

    public HttpsProbe(IClock clock)
        : this(clock, new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            // A fresh connection per probe so DNS, TCP and TLS setup are part of the measurement.
            PooledConnectionLifetime = TimeSpan.Zero,
            PooledConnectionIdleTimeout = TimeSpan.Zero,
        }))
    {
    }

    internal HttpsProbe(IClock clock, HttpClient client)
    {
        _clock = clock;
        _client = client;
        _client.Timeout = Timeout.InfiniteTimeSpan;
    }

    public void Dispose() => _client.Dispose();

    public async Task<ProbeResult> GetAsync(string endpoint, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeoutMilliseconds, 1);

        var startedAt = _clock.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return Result(ProbeOutcome.Error, null, "InvalidTarget");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMilliseconds);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            var elapsed = stopwatch.Elapsed.TotalMilliseconds;
            var status = (int)response.StatusCode;
            return status < 400
                ? Result(ProbeOutcome.Success, elapsed, null)
                : Result(ProbeOutcome.Failure, null, "HttpStatus" + status);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result(ProbeOutcome.Timeout, null, "RequestTimedOut");
        }
        catch (HttpRequestException ex)
        {
            return Result(ProbeOutcome.Failure, null, ex.HttpRequestError.ToString());
        }

        ProbeResult Result(ProbeOutcome outcome, double? latencyMs, string? errorCode) =>
            new(ProbeType.Https, endpoint, Method, startedAt, startedAt + stopwatch.Elapsed, outcome, latencyMs,
                timeoutMilliseconds, errorCode, errorCode is null ? null : Describe(errorCode));
    }

    internal static string Describe(string errorCode) => errorCode switch
    {
        "RequestTimedOut" => "No response was received before the timeout.",
        "InvalidTarget" => "The endpoint is not a valid https:// URL.",
        nameof(HttpRequestError.NameResolutionError) => "The server name could not be resolved.",
        nameof(HttpRequestError.ConnectionError) => "A connection to the server could not be made.",
        nameof(HttpRequestError.SecureConnectionError) => "A secure connection could not be established.",
        _ when errorCode.StartsWith("HttpStatus", StringComparison.Ordinal) => "The server returned an error status.",
        _ => "The request failed.",
    };
}
