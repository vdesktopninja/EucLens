namespace EucLens.Core;

/// <summary>What one sample is to a rule: no usable value, within normal, matching the criterion, or outside the context the metric needs.</summary>
internal static class SampleClass
{
    public const byte Unusable = 0, Good = 1, Bad = 2, OutOfContext = 3;
}

/// <summary>One run of matching samples after grouping, before it is judged against the rule's duration and sample requirements.</summary>
internal struct Episode
{
    public int First, Last;          // sample indexes of the first and last matching sample
    public int Matching, Valid;      // matching samples; valid (good + matching) samples between First and Last
    public int Critical;             // matching samples at or beyond the critical threshold
    public double Peak;              // most extreme canonical value of a matching sample
    public ObservationEnd Ending;
    public long? RecoveredAt;        // time of the first normal sample after the run, only when nothing was missing in between
}

/// <summary>
/// Groups matching samples into episodes in one pass. Matching samples within the merge tolerance of each other (and never less than two sampling steps) are one
/// episode; an episode never crosses a segment boundary (gap, disconnect, session change). It ends in <see cref="ObservationEnd.Recovered"/> only when the next valid
/// sample is normal with nothing missing in between: missing data or the end of the context is coverage ending, never recovery.
/// </summary>
internal static class EpisodeDetector
{
    public static List<Episode> Detect(AnomalyTimeline timeline, ReadOnlySpan<byte> classes, ReadOnlySpan<double> canonical, bool higherIsWorse, double? critical, long mergeTicks, CancellationToken cancellationToken)
    {
        var episodes = new List<Episode>();
        var ticks = timeline.Ticks;
        var merge = Math.Max(mergeTicks, 2 * timeline.StepTicks);
        var open = false;
        Episode e = default;
        int goodAfterLastBad = 0, firstGoodAfter = -1; bool unusableBeforeGood = false;

        void Close(ObservationEnd fallback)
        {
            e.Valid -= goodAfterLastBad;
            if (firstGoodAfter >= 0 && !unusableBeforeGood) { e.Ending = ObservationEnd.Recovered; e.RecoveredAt = ticks[firstGoodAfter]; }
            else e.Ending = fallback;
            episodes.Add(e); open = false;
        }

        for (var i = 0; i < timeline.Count; i++)
        {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (open && timeline.Segment[i] != timeline.Segment[i - 1]) Close(ObservationEnd.CoverageEnded);
            switch (classes[i])
            {
                case SampleClass.Bad:
                    if (open && ticks[i] - ticks[e.Last] > merge) Close(ObservationEnd.CoverageEnded);
                    if (!open)
                    {
                        open = true; e = new() { First = i, Last = i, Peak = canonical[i] }; goodAfterLastBad = 0; firstGoodAfter = -1; unusableBeforeGood = false;
                    }
                    e.Last = i; e.Matching++; e.Valid++;
                    if (higherIsWorse ? canonical[i] > e.Peak : canonical[i] < e.Peak) e.Peak = canonical[i];
                    if (critical is { } c && (higherIsWorse ? canonical[i] >= c : canonical[i] < c)) e.Critical++;
                    goodAfterLastBad = 0; firstGoodAfter = -1; unusableBeforeGood = false;
                    break;
                case SampleClass.Good:
                    if (!open) break;
                    e.Valid++; goodAfterLastBad++;
                    if (firstGoodAfter < 0) firstGoodAfter = i;
                    if (ticks[i] - ticks[e.Last] > merge) Close(ObservationEnd.CoverageEnded);
                    break;
                case SampleClass.OutOfContext:
                    if (!open) break;
                    if (firstGoodAfter < 0) unusableBeforeGood = true;
                    if (ticks[i] - ticks[e.Last] > merge) Close(ObservationEnd.ContextEnded);
                    break;
                default:
                    if (!open) break;
                    if (firstGoodAfter < 0) unusableBeforeGood = true;
                    if (ticks[i] - ticks[e.Last] > merge) Close(ObservationEnd.CoverageEnded);
                    break;
            }
        }
        if (open) Close(ObservationEnd.CoverageEnded);
        return episodes;
    }
}
