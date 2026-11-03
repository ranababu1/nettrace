using System.Net.NetworkInformation;
using NetTrace.Core.Probes;
using NetTrace.Infrastructure.Probes;

namespace NetTrace.Infrastructure.Tests;

public class PingProbeTests
{
    private readonly PingProbe _probe = new(new FakeClock(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero)));

    [Theory]
    [InlineData(IPStatus.Success, ProbeOutcome.Success)]
    [InlineData(IPStatus.TimedOut, ProbeOutcome.Timeout)]
    [InlineData(IPStatus.DestinationHostUnreachable, ProbeOutcome.Failure)]
    [InlineData(IPStatus.DestinationNetworkUnreachable, ProbeOutcome.Failure)]
    [InlineData(IPStatus.TtlExpired, ProbeOutcome.Failure)]
    [InlineData(IPStatus.HardwareError, ProbeOutcome.Error)]
    [InlineData(IPStatus.Unknown, ProbeOutcome.Error)]
    public void MapStatus_ClassifiesReplies(IPStatus status, ProbeOutcome expected)
    {
        Assert.Equal(expected, PingProbe.MapStatus(status));
    }

    [Fact]
    public async Task InvalidTarget_IsErrorWithoutLatency()
    {
        var result = await _probe.PingAsync(ProbeType.ExternalIcmp, "not-an-ip", 1000, CancellationToken.None);

        Assert.Equal(ProbeOutcome.Error, result.Outcome);
        Assert.Equal("InvalidTarget", result.ErrorCode);
        Assert.Null(result.LatencyMs);
        Assert.Equal(1000, result.TimeoutMilliseconds);
        Assert.Equal(PingProbe.Method, result.Method);
    }

    // Loopback ICMP stays on this machine: no internet access is needed.
    [Fact]
    public async Task Loopback_Succeeds()
    {
        var result = await _probe.PingAsync(ProbeType.Gateway, "127.0.0.1", 1000, CancellationToken.None);

        Assert.Equal(ProbeOutcome.Success, result.Outcome);
        Assert.NotNull(result.LatencyMs);
        Assert.True(result.LatencyMs >= 0);
        Assert.Null(result.ErrorCode);
        Assert.Equal("127.0.0.1", result.Target);
        Assert.Equal(ProbeType.Gateway, result.ProbeType);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _probe.PingAsync(ProbeType.ExternalIcmp, "127.0.0.1", 1000, cts.Token));
    }

    [Fact]
    public void ErrorDescriptions_NeverEchoUnknownCodes()
    {
        Assert.Equal("The probe failed.", PingProbe.DescribeError("SomethingWith C:\\Users\\secret"));
    }
}
