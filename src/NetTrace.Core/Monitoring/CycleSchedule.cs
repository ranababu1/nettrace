namespace NetTrace.Core.Monitoring;

/// <param name="NextScheduledAtUtc">When the next cycle should start.</param>
/// <param name="SkippedSlots">Whole intervals that passed without a cycle (overrun, stall or sleep).</param>
/// <param name="ClockMovedBackwards">The clock is earlier than the previous slot; the schedule was reset.</param>
public sealed record CycleSlot(DateTimeOffset NextScheduledAtUtc, int SkippedSlots, bool ClockMovedBackwards);

/// <summary>
/// Fixed-rate schedule without overlap or catch-up bursts: slots stay aligned to
/// previous + k x interval, and slots already in the past are skipped and counted, never run late
/// in a burst.
/// </summary>
public static class CycleSchedule
{
    public static CycleSlot Next(DateTimeOffset previousScheduledAtUtc, DateTimeOffset nowUtc, TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);

        if (nowUtc < previousScheduledAtUtc)
        {
            return new CycleSlot(nowUtc + interval, SkippedSlots: 0, ClockMovedBackwards: true);
        }

        var elapsedTicks = (nowUtc - previousScheduledAtUtc).Ticks;
        var steps = Math.Max(1, (elapsedTicks + interval.Ticks - 1) / interval.Ticks);
        return new CycleSlot(
            previousScheduledAtUtc + TimeSpan.FromTicks(steps * interval.Ticks),
            SkippedSlots: (int)Math.Min(int.MaxValue, steps - 1),
            ClockMovedBackwards: false);
    }
}
