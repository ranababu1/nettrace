using System.Globalization;
using NetTrace.Core.Statistics;

namespace NetTrace.Core.Tests;

public class DurationFormatterTests
{
    [Theory]
    [InlineData(0, "0 s")]
    [InlineData(-5, "0 s")]
    [InlineData(45, "45 s")]
    [InlineData(60, "1 min")]
    [InlineData(725, "12 min 5 s")]
    [InlineData(3600, "1 h")]
    [InlineData(12000, "3 h 20 min")]
    [InlineData(86400, "1 d")]
    [InlineData(100800, "1 d 4 h")]
    public void Format_UsesTheTwoLargestUnits(double seconds, string expected) =>
        Assert.Equal(expected, DurationFormatter.Format(seconds));

    [Fact]
    public void Percentage_IsTruncatedNotRounded_SoAnOutageNeverShowsAsPerfect()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal("99.99 %", DurationFormatter.FormatPercentage(99.9999));
            Assert.Equal("100.00 %", DurationFormatter.FormatPercentage(100));
            Assert.Equal("0.00 %", DurationFormatter.FormatPercentage(0));
            Assert.Equal("--", DurationFormatter.FormatPercentage(null));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
