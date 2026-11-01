using System;

namespace TabloidBrowser;

/// <summary>
/// Immutable view of a tab's sleep-relevant state, decoupled from the UI-bound
/// <see cref="BrowserTab"/> model so hibernation decisions are unit-testable.
/// </summary>
public sealed record TabSleepSnapshot(
    bool IsWeb,
    bool IsActive,
    bool IsSleeping,
    bool IsPlayingAudio,
    bool IsAiOwned,
    bool IsAiBusy,
    bool SuspendedForSleep,
    DateTimeOffset? SuspendedAtUtc,
    DateTimeOffset LastActiveUtc);

/// <summary>
/// Pure decisions for the tab sleep/discard loop. The caller (MainWindow's
/// sleep timer) maps tabs to snapshots and executes the resulting actions.
/// </summary>
public static class TabSleepPolicy
{
    /// <summary>How long a suspended tab is kept before it is discarded outright.</summary>
    public static readonly TimeSpan DiscardSuspendedAfter = TimeSpan.FromMinutes(30);

    /// <summary>A tab the sleep timer may try to hibernate this tick.</summary>
    public static bool IsSleepCandidate(TabSleepSnapshot tab)
    {
        return tab.IsWeb &&
            !tab.IsActive &&
            !tab.IsSleeping &&
            !tab.IsPlayingAudio &&
            !(tab.IsAiOwned && tab.IsAiBusy);
    }

    /// <summary>Hibernate under memory pressure regardless of age; otherwise only after the idle threshold.</summary>
    public static bool ShouldSleep(TimeSpan inactiveFor, TimeSpan sleepAfter, bool underMemoryPressure)
    {
        return underMemoryPressure || inactiveFor >= sleepAfter;
    }

    /// <summary>A suspended tab the discard pass may reclaim this tick.</summary>
    public static bool IsDiscardCandidate(TabSleepSnapshot tab)
    {
        return tab.IsSleeping && tab.SuspendedForSleep && !tab.IsActive;
    }

    /// <summary>Discard under memory pressure; otherwise only once the suspension has expired.</summary>
    public static bool ShouldDiscard(DateTimeOffset? suspendedAtUtc, DateTimeOffset now, bool underMemoryPressure)
    {
        if (underMemoryPressure)
        {
            return true;
        }

        return suspendedAtUtc is { } suspendedAt && now - suspendedAt >= DiscardSuspendedAfter;
    }
}
