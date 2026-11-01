using System;
using TabloidBrowser;
using Xunit;

namespace TabloidBrowser.Tests;

public class TabSleepPolicyTests
{
    private static TabSleepSnapshot Snapshot(
        bool isWeb = true,
        bool isActive = false,
        bool isSleeping = false,
        bool isPlayingAudio = false,
        bool isAiOwned = false,
        bool isAiBusy = false,
        bool suspendedForSleep = false,
        DateTimeOffset? suspendedAtUtc = null)
    {
        return new TabSleepSnapshot(
            isWeb, isActive, isSleeping, isPlayingAudio, isAiOwned, isAiBusy,
            suspendedForSleep, suspendedAtUtc, DateTimeOffset.UtcNow.AddMinutes(-10));
    }

    [Fact]
    public void IsSleepCandidate_SkipsActiveNonWebSleepingAndAudioTabs()
    {
        Assert.False(TabSleepPolicy.IsSleepCandidate(Snapshot(isActive: true)));
        Assert.False(TabSleepPolicy.IsSleepCandidate(Snapshot(isWeb: false)));
        Assert.False(TabSleepPolicy.IsSleepCandidate(Snapshot(isSleeping: true)));
        Assert.False(TabSleepPolicy.IsSleepCandidate(Snapshot(isPlayingAudio: true)));
        Assert.True(TabSleepPolicy.IsSleepCandidate(Snapshot()));
    }

    [Fact]
    public void IsSleepCandidate_SkipsBusyAiTabsButAllowsIdleOnes()
    {
        Assert.False(TabSleepPolicy.IsSleepCandidate(Snapshot(isAiOwned: true, isAiBusy: true)));
        Assert.True(TabSleepPolicy.IsSleepCandidate(Snapshot(isAiOwned: true, isAiBusy: false)));
    }

    [Fact]
    public void ShouldSleep_UnderMemoryPressureOverridesInactivity()
    {
        Assert.True(TabSleepPolicy.ShouldSleep(TimeSpan.Zero, TimeSpan.FromHours(1), underMemoryPressure: true));
    }

    [Fact]
    public void ShouldSleep_RequiresIdleThresholdWithoutPressure()
    {
        Assert.True(TabSleepPolicy.ShouldSleep(TimeSpan.FromMinutes(31), TimeSpan.FromMinutes(30), underMemoryPressure: false));
        Assert.False(TabSleepPolicy.ShouldSleep(TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(30), underMemoryPressure: false));
        Assert.True(TabSleepPolicy.ShouldSleep(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30), underMemoryPressure: false));
    }

    [Fact]
    public void IsDiscardCandidate_RequiresSuspendedInactiveTab()
    {
        Assert.True(TabSleepPolicy.IsDiscardCandidate(Snapshot(isSleeping: true, suspendedForSleep: true)));
        Assert.False(TabSleepPolicy.IsDiscardCandidate(Snapshot(isSleeping: true, suspendedForSleep: false)));
        Assert.False(TabSleepPolicy.IsDiscardCandidate(Snapshot(isSleeping: false, suspendedForSleep: true)));
        Assert.False(TabSleepPolicy.IsDiscardCandidate(Snapshot(isSleeping: true, suspendedForSleep: true, isActive: true)));
    }

    [Fact]
    public void ShouldDiscard_UnderMemoryPressureDiscardsImmediately()
    {
        var suspendedAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        Assert.True(TabSleepPolicy.ShouldDiscard(suspendedAt, DateTimeOffset.UtcNow, underMemoryPressure: true));
    }

    [Fact]
    public void ShouldDiscard_RequiresSuspensionExpiryWithoutPressure()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(TabSleepPolicy.ShouldDiscard(now - TabSleepPolicy.DiscardSuspendedAfter, now, underMemoryPressure: false));
        Assert.False(TabSleepPolicy.ShouldDiscard(now - TimeSpan.FromMinutes(29), now, underMemoryPressure: false));
    }

    [Fact]
    public void ShouldDiscard_NeverExpiresTabsWithoutSuspensionTimestamp()
    {
        Assert.False(TabSleepPolicy.ShouldDiscard(null, DateTimeOffset.UtcNow, underMemoryPressure: false));
    }
}
