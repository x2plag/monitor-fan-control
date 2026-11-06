using System.IO;
using static MonitorFanControl.MenuAudioAnalysis;

namespace MonitorFanControl;

// TimelineFrame is a monotonic clock estimate in capture frames. SampleCount is
// delivered PCM; substituting that count for the timeline silently loses latency.
public interface IMenuAudioSource
{
    int SampleRate { get; }
    long SampleCount { get; }
    long TimelineFrame { get; }
    bool Healthy { get; }
    short[] Snapshot(long startFrame, long endFrame);
}

public enum MenuAudioReferencePolicy { StrictBroadbandAndTones, FrozenStrongTonalReference }

public sealed record MenuAudioWindow(string Name, long StartMilliseconds, long EndMilliseconds,
    long StartFrame, long EndFrame, long ExpectedFrameCount, int ActualFrameCount,
    bool Valid, string Reason, double AcRmsDbfs, double RawRmsDbfs, double DcOffset,
    double Peak, int NearRailSamples, int LongestRailRun, int LongestZeroRun,
    int LongestConstantRun, IReadOnlyList<double> OneSecondDbfs, IReadOnlyList<double> QuarterSecondDbfs);

public sealed record MenuAudioBandComparison(string Name, double LowHz, double HighHz,
    double InitialMedianDbfs, double InitialMadDb, double InitialSpanDb,
    double PreparedDbfs, double PreparedSpanDb, double DeltaDb, double ToleranceDb, bool Comparable)
{
    public double InitialProminenceDb { get; init; } = double.NaN;
    public int InitialRecurringSeconds { get; init; }
    public bool Primary { get; init; }
}

public sealed record MenuAudioReadiness(bool Ready, string Reason,
    double InitialMedianDbfs, double InitialMadDb, double InitialSpanDb,
    double PreparedSpanDb, double DeltaDb, double ToleranceDb,
    IReadOnlyList<MenuAudioBandComparison> Bands)
{
    public MenuAudioReferencePolicy ReferencePolicy { get; init; }
    public string ReferenceBasis { get; init; } = "strict-broadband-and-tones";
    public IReadOnlyList<MenuAudioBandComparison> SelectedBands { get; init; } = [];
    public string BroadbandAssessment { get; init; } = "unmeasured";
}

public sealed record MenuAudioSessionResult(MenuSessionResult? Session, MenuAudioWindow? Initial,
    MenuAudioWindow? Prepared, IReadOnlyList<MenuAudioWindow> Phases,
    MenuAudioReadiness Readiness, bool CaptureHealthy);

public static class MenuAudioSession
{
    public const int BufferDrainMs = 250;

    public static async Task<MenuAudioSessionResult> RunAsync(
        Func<CancellationToken, Task<IMenuWorker>> startWorker, IMenuAudioSource source,
        IMenuClock clock, Action<MenuLogEntry> log, CancellationToken token = default,
        MenuAudioReferencePolicy referencePolicy = MenuAudioReferencePolicy.StrictBroadbandAndTones,
        int preCaptureSettleMs = 0)
    {
        MenuAudioWindow? initial = null;
        MenuAudioWindow? prepared = null;
        MenuSessionResult? session = null;
        var phases = new List<Anchor>();
        IReadOnlyList<FrozenTone> frozenTones = [];
        var readiness = Refused("Microphone baseline has not been measured.");
        try
        {
            RequireHealthy();
            if (!Enum.IsDefined(referencePolicy) || preCaptureSettleMs is < 0 or > 10000)
                throw new InvalidDataException("Invalid microphone reference policy or settling interval.");
            if (source.SampleRate != 48000)
                throw new InvalidDataException("Microphone source must provide mono PCM16 at 48000 frames/s.");
            if (preCaptureSettleMs > 0)
            {
                log(new(clock.Milliseconds, "audio-settle-start", $"Fixed {preCaptureSettleMs}ms recording-only settle; timeline-frame={source.TimelineFrame}", "settling"));
                await clock.DelayAsync(preCaptureSettleMs, token);
                RequireHealthy();
                log(new(clock.Milliseconds, "audio-settle-end", $"Fixed settle completed; timeline-frame={source.TimelineFrame}; no search or retry", "settling"));
            }
            initial = await Record("initial", 5000, token);
            if (!initial.Valid)
                return Result(Refused("Invalid initial microphone recording: " + initial.Reason));
            var initialMedian = Median(initial.OneSecondDbfs);
            var initialMad = Median(initial.OneSecondDbfs.Select(level => Math.Abs(level - initialMedian)).ToArray());
            var initialSpan = Span(initial.OneSecondDbfs);
            var initialTolerance = Math.Max(1, 3 * 1.4826 * initialMad);
            if (referencePolicy == MenuAudioReferencePolicy.StrictBroadbandAndTones &&
                (initial.OneSecondDbfs.Count != 5 || initialSpan > 2 || initialTolerance > 2))
                return Result(new(false, "Inconclusive initial baseline: unstable broadband levels; monitor worker not started.",
                    initialMedian, initialMad, initialSpan, double.NaN, double.NaN, initialTolerance, []));
            if (referencePolicy == MenuAudioReferencePolicy.FrozenStrongTonalReference)
            {
                frozenTones = FreezeStrongTones(initial, source);
                if (frozenTones.Count == 0)
                    return Result(Refused("Inconclusive initial tonal reference: no strong band recurred in all five seconds within stability limits; monitor worker not started."));
                readiness = CompareTone(initial, null, source, frozenTones);
                log(new(clock.Milliseconds, "microphone-reference",
                    $"{readiness.ReferenceBasis}; primary={readiness.SelectedBands[0].Name}; initial-prominence={frozenTones[0].ProminenceDb:R}dB; 5/5 seconds; fixed before worker/open", "initial"));
            }

            session = await MenuContextSession.RunAsync(async workerToken =>
                {
                    RequireHealthy();
                    return await startWorker(workerToken);
                }, clock, async preparedToken =>
                {
                    prepared = await Record("prepared", 1000, preparedToken);
                    readiness = referencePolicy == MenuAudioReferencePolicy.StrictBroadbandAndTones
                        ? Compare(initial, prepared, source) : CompareTone(initial, prepared, source, frozenTones);
                    log(new(clock.Milliseconds, "microphone-readiness", readiness.Reason, "preparation"));
                    // This is only the existing control-flow gate. No manual observation
                    // or physical-menu evidence is added to the returned session.
                    return readiness.Ready ? MenuObservation.Present : MenuObservation.Unclear;
                }, entry =>
                {
                    if (entry.Kind == "phase-start")
                    {
                        phases.Add(new(entry.Detail, entry.Milliseconds, source.TimelineFrame));
                        RequireHealthy();
                    }
                    else if (entry.Kind == "phase-end")
                    {
                        var index = phases.FindLastIndex(p => p.Name == entry.Detail);
                        phases[index] = phases[index] with { EndMs = entry.Milliseconds, EndFrame = source.TimelineFrame };
                        RequireHealthy();
                    }
                    log(entry);
                }, token);

            // Cleanup has already completed before this extra buffer delivery wait.
            await clock.DelayAsync(BufferDrainMs, CancellationToken.None);
            if (!source.Healthy)
                session = session with { State = MenuRunState.Error,
                    Detail = "Microphone capture failed; bounded monitor cleanup has completed or is separately recorded." };
            return Result(readiness);
        }
        catch (Exception ex)
        {
            var reason = ex is InvalidDataException ? ex.Message : ex is OperationCanceledException
                ? "Microphone observation interrupted before a usable baseline." : $"Microphone observation failed ({ex.GetType().Name}).";
            log(new(clock.Milliseconds, "microphone-error", reason, "audio"));
            return Result(Refused(reason));
        }

        void RequireHealthy()
        {
            if (!source.Healthy) throw new InvalidDataException("Microphone capture is unavailable or discontinuous.");
        }
        async Task<MenuAudioWindow> Record(string name, int milliseconds, CancellationToken recordToken)
        {
            RequireHealthy();
            var anchor = new Anchor(name, clock.Milliseconds, source.TimelineFrame);
            log(new(clock.Milliseconds, "audio-start", $"{name}; timeline-frame={anchor.StartFrame}", name));
            await clock.DelayAsync(milliseconds, recordToken);
            anchor = anchor with { EndMs = clock.Milliseconds, EndFrame = source.TimelineFrame };
            await clock.DelayAsync(BufferDrainMs, recordToken);
            RequireHealthy();
            var measured = Measure(anchor, source);
            log(new(clock.Milliseconds, "audio-end", $"{name}; frames={measured.ActualFrameCount}/{measured.ExpectedFrameCount}; AC={measured.AcRmsDbfs:R}dBFS; {measured.Reason}", name));
            return measured;
        }
        MenuAudioSessionResult Result(MenuAudioReadiness assessed) =>
            new(session, initial, prepared, phases.Select(p => Measure(p, source)).ToArray(),
                assessed with
                {
                    ReferencePolicy = referencePolicy,
                    ReferenceBasis = referencePolicy == MenuAudioReferencePolicy.FrozenStrongTonalReference && assessed.ReferenceBasis == "strict-broadband-and-tones"
                        ? "frozen-strong-tonal-reference; no qualifying primary selected" : assessed.ReferenceBasis,
                    BroadbandAssessment = referencePolicy == MenuAudioReferencePolicy.FrozenStrongTonalReference && assessed.BroadbandAssessment == "unmeasured"
                        ? "overall-noise interpretation inconclusive; tonal-policy readiness unconfirmed" : assessed.BroadbandAssessment
                }, source.Healthy);
    }

}
