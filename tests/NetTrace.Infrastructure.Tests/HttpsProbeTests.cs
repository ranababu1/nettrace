using System.Net;
using NetTrace.Core.Probes;
using NetTrace.Infrastructure.Probes;
using NetTrace.Infrastructure.Time;

namespace NetTrace.Infrastructure.Tests;

public class HttpsProbeTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK, ProbeOutcome.Success, null)]
    [InlineData(HttpStatusCode.NoContent, ProbeOutcome.Success, null)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ProbeOutcome.Failure, "HttpStatus503")]
    public async Task Get_MapsStatusToOutcome(HttpStatusCode status, ProbeOutcome outcome, string? code)
    {
        using var probe = new HttpsProbe(new SystemClock(), new HttpClient(new StubHandler(_ => new HttpResponseMessage(status))));

        var result = await probe.GetAsync("https://example.com/", 1000, CancellationToken.None);

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(code, result.ErrorCode);
        Assert.Equal(outcome == ProbeOutcome.Success, result.LatencyMs is not null);
    }

    [Fact]
    public async Task Get_ConnectionFailure_IsFailureWithSanitizedMessage()
    {
        using var probe = new HttpsProbe(new SystemClock(), new HttpClient(new StubHandler(
            _ => throw new HttpRequestException(HttpRequestError.NameResolutionError, "secret host details"))));

        var result = await probe.GetAsync("https://example.com/", 1000, CancellationToken.None);

        Assert.Equal(ProbeOutcome.Failure, result.Outcome);
        Assert.Equal("NameResolutionError", result.ErrorCode);
        Assert.DoesNotContain("secret", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_SlowServer_TimesOut()
    {
        using var probe = new HttpsProbe(new SystemClock(), new HttpClient(new StubHandler(null)));

        var result = await probe.GetAsync("https://example.com/", 50, CancellationToken.None);

        Assert.Equal(ProbeOutcome.Timeout, result.Outcome);
        Assert.Null(result.LatencyMs);
    }

    [Fact]
    public async Task Get_NonHttpsTarget_IsError()
    {
        using var probe = new HttpsProbe(new SystemClock(), new HttpClient(new StubHandler(_ => new HttpResponseMessage())));

        var result = await probe.GetAsync("http://example.com/", 1000, CancellationToken.None);

        Assert.Equal(ProbeOutcome.Error, result.Outcome);
    }

    [Fact]
    public async Task Dns_UnresolvableName_IsFailureNotThrown()
    {
        var result = await new DnsProbe(new SystemClock()).ResolveAsync("nonexistent.invalid", 5000, CancellationToken.None);

        Assert.NotEqual(ProbeOutcome.Success, result.Outcome);
        Assert.Null(result.LatencyMs);
        Assert.NotNull(result.ErrorCode);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage>? respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (respond is null)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return respond!(request);
        }
    }
}
