using NetTrace.Core.Configuration;

namespace NetTrace.Core.Tests;

public class MonitoringConfigurationValidatorTests
{
    private static readonly MonitoringConfiguration Defaults = new();

    [Fact]
    public void Defaults_AreValid()
    {
        Assert.Empty(MonitoringConfigurationValidator.Validate(Defaults));
    }

    [Fact]
    public void Defaults_MatchPlan()
    {
        Assert.Equal(5, Defaults.IntervalSeconds);
        Assert.Equal(30, Defaults.DnsIntervalSeconds);
        Assert.Equal(30, Defaults.HttpsIntervalSeconds);
        Assert.Equal(3, Defaults.OutageConfirmationThreshold);
        Assert.Equal(5, Defaults.PublicIpLookupIntervalMinutes);
        Assert.False(Defaults.PublicIpLookupEnabled);
        Assert.True(Defaults.ExternalIcmpTargets.Count >= 2);
        Assert.True(Defaults.HttpsEndpoints.Count >= 2);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(60)]
    public void AllowedIntervals_AreValid(int interval)
    {
        Assert.Empty(MonitoringConfigurationValidator.Validate(Defaults with { IntervalSeconds = interval, DnsIntervalSeconds = 60, HttpsIntervalSeconds = 60 }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(120)]
    public void OtherIntervals_AreRejected(int interval)
    {
        AssertError(Defaults with { IntervalSeconds = interval }, "Monitoring interval");
    }

    [Fact]
    public void TimeoutNotShorterThanCycle_IsRejected()
    {
        AssertError(Defaults with { ExternalTimeoutMilliseconds = 5000 }, "External timeout must be shorter");
    }

    [Theory]
    [InlineData(50)]
    [InlineData(60_000)]
    public void TimeoutOutOfRange_IsRejected(int timeout)
    {
        AssertError(Defaults with { GatewayTimeoutMilliseconds = timeout }, "Gateway timeout must be between");
    }

    [Fact]
    public void SlowProbeFasterThanCycle_IsRejected()
    {
        AssertError(Defaults with { IntervalSeconds = 30, DnsIntervalSeconds = 10, DnsTimeoutMilliseconds = 3000 }, "DNS interval");
    }

    [Fact]
    public void IcmpTargetMustBeIpAddress()
    {
        AssertError(Defaults with { ExternalIcmpTargets = ["1.1.1.1", "dns.google"] }, "'dns.google' must be an IP address");
    }

    [Fact]
    public void IcmpTargetsRequired()
    {
        AssertError(Defaults with { ExternalIcmpTargets = [] }, "At least one External ICMP target");
    }

    [Fact]
    public void DuplicateTargets_AreRejected()
    {
        AssertError(Defaults with { ExternalIcmpTargets = ["1.1.1.1", "1.1.1.1"] }, "listed only once");
    }

    [Theory]
    [InlineData("http://example.com/")]
    [InlineData("https://user:secret@example.com/")]
    [InlineData("example.com")]
    public void HttpsEndpointsMustBeHttpsWithoutCredentials(string endpoint)
    {
        AssertError(Defaults with { HttpsEndpoints = [endpoint] }, "must be an absolute https:// URL");
    }

    [Theory]
    [InlineData("")]
    [InlineData("localhost")]
    [InlineData("not a host")]
    [InlineData("1.1.1.1")]
    public void DnsHostnameMustBeFullyQualified(string hostname)
    {
        AssertError(Defaults with { DnsTestHostname = hostname }, "DNS test hostname");
    }

    [Fact]
    public void SingleFailureConfirmation_IsRejected()
    {
        // A single failed probe must never confirm an outage.
        AssertError(Defaults with { OutageConfirmationThreshold = 1 }, "Outage confirmation threshold");
    }

    [Fact]
    public void FrequentPublicIpLookup_IsRejected()
    {
        AssertError(Defaults with { PublicIpLookupIntervalMinutes = 1 }, "Public IP lookup interval");
    }

    private static void AssertError(MonitoringConfiguration configuration, string expectedFragment)
    {
        var errors = MonitoringConfigurationValidator.Validate(configuration);
        Assert.Contains(errors, e => e.Contains(expectedFragment, StringComparison.Ordinal));
    }
}
