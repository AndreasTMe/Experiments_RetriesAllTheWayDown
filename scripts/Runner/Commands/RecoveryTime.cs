namespace Runner.Commands;

// Time from the fault's removal until the backlog is back to what it was before the fault, and stays there. The backlog
// is messages sent but not yet completed or dead-lettered: counters, plus the Runner's dead-letter peek, since the
// emulator reports no queue counts. Measured on the 1 s samples, so it is good to about a second; a peek that stalls
// (about 12 s) leaves the dead-letter count behind, which can only make the recovery look later.
internal static class RecoveryTime
{
    private const string Definition = "Seconds from the removal of the fault until the backlog (producer.sent - "
                                      + "worker.completed - runner.dlq_peeked) is back to its warmup maximum "
                                      + "and stays there until the end of the run";

    public static JsonObject Compute(IReadOnlyList<Dictionary<string, string>> samples, double? faultRemovedAtSeconds)
    {
        var points = samples
            .Select(s => (Elapsed: ReadDouble(s, "elapsed_s"), Phase: s["phase"], Backlog: ReadBacklog(s)))
            .Where(p => p.Backlog is not null)
            .Select(p => (p.Elapsed, p.Phase, Backlog: p.Backlog!.Value))
            .ToList();

        var result = new JsonObject
        {
            ["definition"]            = Definition,
            ["faultRemovedAtSeconds"] = faultRemovedAtSeconds,
        };
        if (faultRemovedAtSeconds is not { } removedAt || points.Count == 0)
        {
            result["recoverySeconds"] = null;
            return result;
        }

        var warmup   = points.Where(p => p.Phase == "warmup").ToList();
        var baseline = warmup.Count == 0 ? 0 : warmup.Max(p => p.Backlog);
        var after    = points.Where(p => p.Elapsed >= removedAt).ToList();

        // Recovery is the sample after the last one above the baseline. If the last sample is still above it, the run
        // never recovered.
        var     lastAbove   = after.FindLastIndex(p => p.Backlog > baseline);
        double? recoveredAt = lastAbove + 1 < after.Count ? after[lastAbove + 1].Elapsed : null;

        result["baselineBacklog"]    = baseline;
        result["peakBacklog"]        = points.Max(p => p.Backlog);
        result["backlogAtRemoval"]   = after.Count == 0 ? null : after[0].Backlog;
        result["recoveredAtSeconds"] = recoveredAt;
        result["recoverySeconds"]    = recoveredAt is null ? null : Math.Round(recoveredAt.Value - removedAt, 1);
        return result;
    }

    private static long? ReadBacklog(Dictionary<string, string> sample)
    {
        if (!sample.TryGetValue("producer.sent", out var sent)
            || !sample.TryGetValue("worker.completed", out var completed)
            || !sample.TryGetValue("runner.dlq_peeked", out var deadLettered))
        {
            return null;
        }

        return long.Parse(sent, CultureInfo.InvariantCulture)
               - long.Parse(completed, CultureInfo.InvariantCulture)
               - long.Parse(deadLettered, CultureInfo.InvariantCulture);
    }

    private static double ReadDouble(Dictionary<string, string> sample, string key) =>
        double.Parse(sample[key], CultureInfo.InvariantCulture);
}