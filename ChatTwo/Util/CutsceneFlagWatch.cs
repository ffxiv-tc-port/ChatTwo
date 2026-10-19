namespace ChatTwo.Util;

/// <summary>What one cutscene told us about the game's own "chat is hidden" flag.</summary>
/// <param name="StartFlags">The flag word on the first frame the cutscene condition was seen.</param>
/// <param name="Flags">The flag word when the chat flag showed up, or the last one seen if it never did.</param>
/// <param name="ElapsedMs">Time until the flag showed up, or how long it was watched for in vain.</param>
/// <param name="Appeared">False when the cutscene ended without the flag ever being set.</param>
/// <param name="Context">Whatever the caller passed on the first frame, handed back untouched.</param>
/// <param name="IsLast">True on the final report this instance will ever produce.</param>
public readonly record struct CutsceneFlagReport(uint StartFlags, uint Flags, long ElapsedMs, bool Appeared, uint Context, bool IsLast);

/// <summary>
/// Turns the per-frame hide check into at most one report per cutscene.
/// </summary>
/// <remarks>
/// Not thread safe; use it from one thread. Allocates nothing. Pure on purpose (the clock is passed
/// in) so the sequencing can be checked offline. After <see cref="MaxReports"/> reports it stays silent.
/// </remarks>
public sealed class CutsceneFlagWatch
{
    public const int MaxReports = 40;

    private bool Active;
    private bool Reported;
    private long StartMs;
    private long LastMs;
    private uint StartFlags;
    private uint LastFlags;
    private uint Context;
    private int Reports;

    /// <summary>Call on every frame a cutscene condition holds and the chat is not hidden yet.</summary>
    public bool Observe(long nowMs, uint flags, bool chatFlag, uint context, out CutsceneFlagReport report)
    {
        report = default;
        if (!Active)
        {
            Active = true;
            Reported = false;
            StartMs = nowMs;
            StartFlags = flags;
            Context = context;
        }

        LastMs = nowMs;
        LastFlags = flags;
        if (!chatFlag || Reported)
            return false;

        Reported = true;
        return Emit(new CutsceneFlagReport(StartFlags, flags, nowMs - StartMs, true, Context, false), out report);
    }

    /// <summary>Call on every frame no cutscene condition holds.</summary>
    public bool ConditionEnded(out CutsceneFlagReport report)
    {
        report = default;
        if (!Active)
            return false;

        Active = false;
        if (Reported)
            return false;

        return Emit(new CutsceneFlagReport(StartFlags, LastFlags, LastMs - StartMs, false, Context, false), out report);
    }

    private bool Emit(CutsceneFlagReport candidate, out CutsceneFlagReport report)
    {
        report = default;
        if (Reports >= MaxReports)
            return false;

        Reports++;
        report = candidate with { IsLast = Reports == MaxReports };
        return true;
    }
}
