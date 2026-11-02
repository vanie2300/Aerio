using TabloidBrowser;
using Xunit;

namespace TabloidBrowser.Tests;

public class ChromeMotionTests
{
    [Theory]
    [InlineData(0, 0, 1, 0)]
    [InlineData(1, 0, 1, 0)]
    [InlineData(0, 1, 0, 0)]
    [InlineData(0, 1, 1, 1)]
    [InlineData(0, 1, -1, 0)]
    [InlineData(0, 1, 2, 1)]
    public void EaseOutCubic_ClampsProgressAndPreservesEndpoints(
        double from,
        double to,
        double progress,
        double expected)
    {
        Assert.Equal(expected, ChromeMotion.EaseOutCubic(from, to, progress), precision: 6);
    }

    [Fact]
    public void EaseOutCubic_InterpolatesTowardCurrentTarget()
    {
        var halfwayForward = ChromeMotion.EaseOutCubic(0, 1, 0.5);
        var halfwayReverse = ChromeMotion.EaseOutCubic(1, 0, 0.5);

        Assert.Equal(0.875, halfwayForward, precision: 6);
        Assert.Equal(0.125, halfwayReverse, precision: 6);
    }
}
