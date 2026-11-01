using TabloidBrowser;
using Xunit;

namespace TabloidBrowser.Tests;

public class SystemMemoryPressureTests
{
    [Fact]
    public void PressureThreshold_HasOneGigabyteFloor()
    {
        Assert.Equal(1024UL * 1024 * 1024, SystemMemoryPressure.PressureThresholdBytes(4UL * 1024 * 1024 * 1024));
    }

    [Fact]
    public void PressureThreshold_ScalesWithTotalMemoryAboveFloor()
    {
        var twentyFourGb = 24UL * 1024 * 1024 * 1024;
        Assert.Equal(twentyFourGb / 12, SystemMemoryPressure.PressureThresholdBytes(twentyFourGb));
    }

    [Fact]
    public void IsUnderPressure_ComparesAvailableAgainstThreshold()
    {
        var total = 24UL * 1024 * 1024 * 1024;
        var threshold = SystemMemoryPressure.PressureThresholdBytes(total);

        Assert.True(SystemMemoryPressure.IsUnderPressure(threshold - 1, total));
        Assert.False(SystemMemoryPressure.IsUnderPressure(threshold, total));
    }
}
