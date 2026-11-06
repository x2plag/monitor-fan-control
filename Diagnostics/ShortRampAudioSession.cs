using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using static MonitorFanControl.MenuAudioAnalysis;

namespace MonitorFanControl;

public enum ShortRampAudioPolicy { ExistingReference, DescriptiveClosedHold }

// Factory ownership includes capture lifetime. No factory is invoked until the
// explicit OSD admission has been checked by the shared session boundary.
public interface IShortRampAudioCapture : IMenuAudioSource, IDisposable
{
    ShortRampCaptureMetadata Metadata { get; }
    void Stop();
}

public sealed record ShortRampCaptureMetadata(string WavePath, string DeviceName, uint DeviceId,
    DateTimeOffset StartedAtUtc, long StartedTimestamp, long StartCompletedTimestamp, long QpcFrequency,
    int SampleRate, int Channels, int BitsPerSample)
{
    public string Alignment => "frame=(QPC-StartedTimestamp)*SampleRate/QpcFrequency; native start bracket and driver latency remain uncertain";
}

public sealed class ShortRampYetiCapture : IShortRampAudioCapture
{
    private readonly MenuAudioCapture _capture;
    private ShortRampYetiCapture(MenuAudioCapture capture) => _capture = capture;
    public static IShortRampAudioCapture Start(string wavePath) => new ShortRampYetiCapture(MenuAudioCapture.StartYeti(wavePath));
    public ShortRampCaptureMetadata Metadata => new(_capture.WavePath, _capture.DeviceName, _capture.DeviceId,
        _capture.StartedAtUtc, _capture.StartedTimestamp, _capture.StartCompletedTimestamp, _capture.StopwatchFrequency,
        _capture.SampleRate, _capture.Channels, _capture.BitsPerSample);
    public int SampleRate => _capture.SampleRate;
    public long SampleCount => _capture.SampleCount;
    public long TimelineFrame => Math.Max(0, (long)Math.Floor((Stopwatch.GetTimestamp() - _capture.StartedTimestamp) *
        (double)SampleRate / Stopwatch.Frequency));
    public bool Healthy => _capture.Healthy;
    public short[] Snapshot(long startFrame, long endFrame) => _capture.Snapshot(startFrame, endFrame);
    public void Stop() => _capture.Stop();
    public void Dispose() => _capture.Dispose();
}

public sealed record ShortRampAudioPhase(MenuAudioWindow Window, long StartQpc, long EndQpc,
    IReadOnlyList<MenuAudioReadiness> CompleteSecondComparisons, string NumericAssessment)
{
    public ShortRampPulseRetention? PulseRetention { get; init; }
    public ShortRampPulseRetention? ControlRetention { get; init; }
    public ShortRampNativePhaseRetention? NativePhaseRetention { get; init; }
}

public sealed record ShortRampNativePhaseRetention(long OriginalStartQpc, long OriginalDeadlineQpc,
    long ObserverStartFrame, long ObserverEndFrame, long RetainedStartFrame, long RetainedEndFrame,
    long? EarliestCleanupBoundaryQpc, bool EarliestCleanupBoundaryEstablished, bool Truncated, string Classification);

public sealed record ShortRampPulseRetention(long RequestedDeadlineMilliseconds, long ObserverEndMilliseconds,
    long ObserverEndFrame, long RetainedEndFrame, long? EarliestObservedBoundaryQpc, bool Truncated,
    bool EarliestCleanupBoundaryEstablished, string Classification);

public sealed record ShortRampAudioResult(ShortRampSessionResult Session, MenuAudioWindow? Initial,
    MenuAudioWindow? Prepared, IReadOnlyList<MenuAudioWindow> Phases, MenuAudioReadiness Readiness,
    bool CaptureHealthy, bool CaptureStopped, bool CaptureDisposed)
{
    public ShortRampOsdAdmissionMetadata? OsdAdmission { get; init; }
    public IReadOnlyList<ShortRampAudioPhase> NumericPhases { get; init; } = [];
    public string TargetNoiseResult => "undetermined; numeric PCM does not identify the target sound";
    public string PermanentEffect => "unverified";
    public string PhysicalOsdState => "unconfirmed";
    public string? EvidenceError { get; init; }
    public MenuRunState? SoftwareSequenceState { get; init; }
    public IReadOnlyList<string> CaptureErrors { get; init; } = [];
    [JsonConverter(typeof(JsonStringEnumConverter<ShortRampAudioPolicy>))]
    public ShortRampAudioPolicy AudioPolicy { get; init; }
    public bool PositiveAcousticVerdict => false;
}

public static class ShortRampAudioSession
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    private static readonly JsonSerializerOptions JournalJson = new() {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    public static Task<ShortRampAudioResult> RunAsync(HumanOsdClosureObservation? observation,
        Func<CancellationToken, Task<IShortRampAudioCapture>> startCapture,
        Func<CancellationToken, Task<IShortRampWorker>> startWorker, IMenuClock clock,
        string evidenceDirectory, Action<MenuLogEntry> log, CancellationToken token = default,
        MenuAudioReferencePolicy referencePolicy = MenuAudioReferencePolicy.StrictBroadbandAndTones,
        int preCaptureSettleMs = 0, ShortRampPurpose purpose = ShortRampPurpose.Mode2,
        ShortRampAudioPolicy audioPolicy = ShortRampAudioPolicy.ExistingReference)
        => RunWithAdmissionAsync(ShortRampOsdAdmission.FromObservation(observation), startCapture, startWorker,
            clock, evidenceDirectory, log, token, referencePolicy, preCaptureSettleMs, purpose, audioPolicy);

    public static async Task<ShortRampAudioResult> RunWithAdmissionAsync(ShortRampOsdAdmission? admission,
        Func<CancellationToken, Task<IShortRampAudioCapture>> startCapture,
        Func<CancellationToken, Task<IShortRampWorker>> startWorker, IMenuClock clock,
        string evidenceDirectory, Action<MenuLogEntry> log, CancellationToken token = default,
        MenuAudioReferencePolicy referencePolicy = MenuAudioReferencePolicy.StrictBroadbandAndTones,
        int preCaptureSettleMs = 0, ShortRampPurpose purpose = ShortRampPurpose.Mode2,
        ShortRampAudioPolicy audioPolicy = ShortRampAudioPolicy.ExistingReference)
    {
        var descriptive = audioPolicy == ShortRampAudioPolicy.DescriptiveClosedHold;
        var session = new ShortRampSessionResult(MenuRunState.PreconditionFailed,
            "A valid explicit OSD admission is missing; no capture/worker started.", null, null, false) { Purpose = purpose };
        var readiness = Refused("No microphone baseline measured.") with { ReferencePolicy = referencePolicy };
        if (!Enum.IsDefined(purpose) || admission is not { IsValid: true })
            return new(session, null, null, [], readiness, false, false, false);
        session = session with { OsdAdmission = admission.Metadata };

        IShortRampAudioCapture? capture = null;
        StreamWriter? journal = null;
        MenuAudioWindow? initial = null, prepared = null;
        FrozenReference? reference = null;
        var anchors = new List<Anchor>();
        var numeric = new List<ShortRampAudioPhase>();
        var stopped = false; var disposed = false; var captureHealthy = false;
        string? evidenceError = null;
        MenuRunState? sequenceState = null;
        var captureErrors = new List<string>();
        using var captureMonitorStop = new CancellationTokenSource();
        using var captureFailureStop = descriptive ? CancellationTokenSource.CreateLinkedTokenSource(token) : null;
        var activeToken = captureFailureStop?.Token ?? token;
        IShortRampWorker? monitoredWorker = null;
        Task? captureMonitor = null;
        string? captureMonitorFailure = null;
        try
        {
            if (!Enum.IsDefined(audioPolicy) || !Enum.IsDefined(referencePolicy) || preCaptureSettleMs is < 0 or > 10000 ||
                descriptive && (purpose is not (ShortRampPurpose.BoundedClosedHold or ShortRampPurpose.ContextualClosedHold) || preCaptureSettleMs != 10000 ||
                    referencePolicy != MenuAudioReferencePolicy.StrictBroadbandAndTones) ||
                purpose is ShortRampPurpose.BoundedClosedHold or ShortRampPurpose.ContextualClosedHold && !descriptive)
                throw new InvalidDataException("Invalid fixed reference policy or settling interval.");
            var directory = Path.GetFullPath(evidenceDirectory);
            Directory.CreateDirectory(directory);
            if (Directory.EnumerateFileSystemEntries(directory).Any())
                throw new IOException("Short audio evidence requires a new empty directory; existing evidence is preserved.");
            journal = new StreamWriter(new FileStream(Path.Combine(directory, "events.jsonl"), FileMode.CreateNew,
                FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            Save("osd-admission.json", admission.Metadata);
            activeToken.ThrowIfCancellationRequested();
            capture = await startCapture(activeToken);
            var metadata = capture.Metadata;
            if (capture.SampleRate != 48000 || metadata.SampleRate != 48000 || metadata.Channels != 1 || metadata.BitsPerSample != 16 || metadata.QpcFrequency <= 0 || metadata.StartCompletedTimestamp < metadata.StartedTimestamp)
                throw new InvalidDataException("Capture must provide mono PCM16/48000 with a valid QPC anchor.");
            Save("capture-start.json", metadata);
            RequireHealthy();
            if (descriptive) captureMonitor = Task.Run(MonitorCaptureAsync);
            if (preCaptureSettleMs > 0)
            {
                Emit(new(clock.Milliseconds, "audio-settle-start", $"Fixed {preCaptureSettleMs}ms settle; no search or retry", "settling"));
                await clock.DelayAsync(preCaptureSettleMs, activeToken);
                RequireHealthy();
                Emit(new(clock.Milliseconds, "audio-settle-end", "Fixed settle completed", "settling"));
            }
            initial = await Record("initial", 5000, activeToken);
            if (!initial.Valid) throw new InvalidDataException("Invalid initial PCM: " + initial.Reason);
            reference = new(initial, capture, referencePolicy);
            // A fixed first initial second checks the same engine's unchanged
            // initial policy, and freezes strict bands before starting any worker.
            var baselineSecond = Measure(new("initial-reference-second", initial.StartMilliseconds, initial.StartFrame,
                initial.StartMilliseconds + 1000, initial.StartFrame + capture.SampleRate), capture);
            readiness = CompareReference(baselineSecond);
            if (!descriptive && !readiness.Ready) throw new InvalidDataException(readiness.Reason);
            Save("reference.json", readiness);
            Emit(new(clock.Milliseconds, "microphone-reference", readiness.ReferenceBasis + "; frozen immutable initial PCM before worker/open", "initial"));
            activeToken.ThrowIfCancellationRequested();
            session = await ShortRampSession.RunWithAdmissionAsync(admission, async workerToken =>
            {
                RequireHealthy();
                // Bound initial analysis/driver delivery as well as the fixed
                //15s before any new-purpose actor exists. A slow callback may
                // refuse this candidate, never consume its recovery reserve.
                if (purpose == ShortRampPurpose.ContextualClosedHold && capture.TimelineFrame * 1000L / capture.SampleRate > 20000)
                    throw new TimeoutException("Original capture pre-worker20s admission budget expired; no worker started.");
                var started = await startWorker(workerToken);
                if (descriptive)
                {
                    Volatile.Write(ref monitoredWorker, started);
                    // Cancellation may already have ended Session.WaitAsync,
                    // so it cannot adopt a late successful factory result.
                    if (workerToken.IsCancellationRequested || Volatile.Read(ref captureMonitorFailure) is not null)
                    {
                        try { started.Revoke(); }
                        finally
                        {
                            try { await started.FinishAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(started.RecoveryWaitMilliseconds)); }
                            finally { await started.DisposeAsync(); }
                        }
                        throw new OperationCanceledException("Capture session ended before worker ownership could be adopted.", workerToken);
                    }
                }
                return started;
            }, clock, async preparedToken =>
            {
                prepared = await Record("prepared", descriptive ? 500 : 1000, preparedToken);
                readiness = CompareReference(prepared);
                Save("readiness.json", readiness);
                Emit(new(clock.Milliseconds, "microphone-readiness", readiness.Reason, "preparation"));
                return descriptive ? prepared.Valid && capture.Healthy : readiness.Ready;
            }, Observe, activeToken, QualifyPhase, purpose);
            sequenceState = session.State;
            // All monitor cleanup and worker disposal precede delivery drain.
            await clock.DelayAsync(MenuAudioSession.BufferDrainMs, CancellationToken.None);
        }
        catch (Exception ex)
        {
            session = session with { State = ex is OperationCanceledException && token.IsCancellationRequested
                ? MenuRunState.Interrupted : session.PulseRequested || session.ControlClockArmed ? MenuRunState.Error : MenuRunState.PreconditionFailed,
                Detail = "Short microphone session stopped: " + ex.GetType().Name + ": " + ex.Message };
        }
        finally
        {
            // Stop the independent reader before capture.Stop/Dispose. Only
            // this serialized path writes the journal, anchors and error list.
            captureMonitorStop.Cancel();
            if (captureMonitor is not null)
                try { await captureMonitor; } catch (OperationCanceledException) { }
            if (Volatile.Read(ref captureMonitorFailure) is { } captureFailure)
                Failure("Independent capture monitor", new InvalidDataException(captureFailure));
            if (capture is not null)
            {
                for (var i = 0; i < anchors.Count; i++)
                    if (anchors[i].EndFrame is null)
                        anchors[i] = anchors[i] with { EndMs = clock.Milliseconds, EndFrame = capture.TimelineFrame, Complete = false };
                // Stop and Dispose are independent attempts. Their failure never
                // erases retained PCM/anchors or becomes a successful recording.
                try { capture.Stop(); stopped = true; }
                catch (Exception ex) { Failure("Capture Stop", ex); }
                foreach (var anchor in anchors)
                {
                    try { Retain(anchor); }
                    catch (Exception ex) { Failure("PCM evidence", ex); evidenceError = ex.GetType().Name + ": " + ex.Message; }
                }
                try { Save("capture-final.json", new { capture.Metadata, capture.SampleCount, capture.Healthy, CaptureStopped = stopped }); }
                catch (Exception ex) { Failure("Capture metadata", ex); evidenceError = ex.GetType().Name + ": " + ex.Message; }
                if (descriptive)
                {
                    try { captureHealthy = capture.Healthy; }
                    catch (Exception ex) { Failure("Capture health", ex); captureHealthy = false; }
                }
                else captureHealthy = capture.Healthy;
                try { capture.Dispose(); disposed = true; }
                catch (Exception ex) { Failure("Capture Dispose", ex); }
                if (!captureHealthy || !stopped || !disposed || numeric.Any(p => !p.Window.Valid))
                    session = session with { State = MenuRunState.Error, Detail = "PCM capture/phase/lifecycle failed; monitor cleanup and physical state remain separate." };
            }
            try { journal?.Dispose(); }
            catch (Exception ex) { Failure("Audio journal close", ex); evidenceError = ex.GetType().Name + ": " + ex.Message; }
        }
        var result = new ShortRampAudioResult(session, initial, prepared,
            numeric.Where(p => p.Window.Name is not ("initial" or "prepared")).Select(p => p.Window).ToArray(),
            readiness, captureHealthy && stopped && disposed, stopped, disposed)
            { OsdAdmission = admission.Metadata, NumericPhases = numeric, EvidenceError = evidenceError, SoftwareSequenceState = sequenceState, CaptureErrors = captureErrors, AudioPolicy = audioPolicy };
        try { if (journal is not null) Save("result.json", result); }
        catch (Exception ex) { result = result with { Session = session with { State = MenuRunState.Error,
            Detail = "Final audio evidence could not be saved." }, EvidenceError = ex.GetType().Name + ": " + ex.Message }; }
        return result;

        async Task MonitorCaptureAsync()
        {
            while (true)
            {
                captureMonitorStop.Token.ThrowIfCancellationRequested();
                string? failure = null;
                try { if (capture?.Healthy != true) failure = "PCM capture became unhealthy, unavailable or discontinuous."; }
                catch (Exception ex) { failure = "PCM capture health getter failed: " + ex.GetType().Name + ": " + ex.Message; }
                if (failure is not null)
                {
                    // Revocation is the first action. Cancellation callbacks
                    // can be arbitrary caller code; they must never sit ahead
                    // of the independent guardian's shared revoke event.
                    try { Volatile.Read(ref monitoredWorker)?.Revoke(); }
                    catch (Exception ex) { failure += " Worker revoke failed: " + ex.GetType().Name; }
                    Interlocked.CompareExchange(ref captureMonitorFailure, failure, null);
                    // CancelAsync publishes cancellation immediately and runs
                    // external callbacks asynchronously. The health observer
                    // itself therefore remains stoppable before capture exit.
                    var cancellation = captureFailureStop!.CancelAsync();
                    _ = cancellation.ContinueWith(static completed => { _ = completed.Exception; },
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    return;
                }
                await Task.Delay(50, captureMonitorStop.Token);
            }
        }
        void Failure(string operation, Exception ex)
        {
            captureErrors.Add(operation + ": " + ex.GetType().Name + ": " + ex.Message);
            session = session with { State = MenuRunState.Error,
                Detail = operation + " failed: " + ex.GetType().Name + "; monitor cleanup and physical state remain separate." };
        }
        void RequireHealthy()
        {
            if (capture?.Healthy != true) throw new InvalidDataException("PCM capture failed, stalled, or discontinuous.");
        }
        MenuAudioReadiness CompareReference(MenuAudioWindow measured)
        {
            var compared = reference!.Compare(measured);
            return descriptive ? compared with { Ready = false,
                Reason = "Descriptive raw PCM only; no positive acoustic verdict or protection claim. Numeric reference assessment: " + compared.Reason } : compared;
        }
        void Emit(MenuLogEntry entry)
        {
            // Raw start/end anchors are flushed before delegating to an observer,
            // retaining the boundary even if that observer throws or cancels.
            try
            {
                journal!.WriteLine(JsonSerializer.Serialize(new { Entry = entry, TimelineFrame = capture!.TimelineFrame,
                    DeliveredFrames = capture.SampleCount, Qpc = Qpc(capture.TimelineFrame) }, JournalJson));
                ((FileStream)journal.BaseStream).Flush(flushToDisk: true);
                log(entry);
            }
            catch (Exception ex)
            {
                Failure("Audio journal/observer", ex);
                evidenceError = ex.GetType().Name + ": " + ex.Message;
                throw;
            }
        }
        long Qpc(long frame) => checked(capture!.Metadata.StartedTimestamp +
            (long)Math.Round(frame * (double)capture.Metadata.QpcFrequency / capture.SampleRate));
        void Save(string name, object value)
        {
            var path = Path.Combine(Path.GetFullPath(evidenceDirectory), name);
            using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(file, value, Json);
            file.Flush(flushToDisk: true);
        }
        async Task<MenuAudioWindow> Record(string name, int milliseconds, CancellationToken recordToken)
        {
            RequireHealthy();
            var index = anchors.Count;
            anchors.Add(new(name, clock.Milliseconds, capture!.TimelineFrame));
            try
            {
                Emit(new(clock.Milliseconds, "audio-start", name, name));
                await clock.DelayAsync(milliseconds, recordToken);
            }
            catch
            {
                anchors[index] = anchors[index] with { EndMs = clock.Milliseconds, EndFrame = capture.TimelineFrame, Complete = false };
                Emit(new(clock.Milliseconds, "audio-aborted", name, name));
                throw;
            }
            anchors[index] = anchors[index] with { EndMs = clock.Milliseconds, EndFrame = capture.TimelineFrame };
            Emit(new(clock.Milliseconds, "audio-end", name, name));
            await clock.DelayAsync(MenuAudioSession.BufferDrainMs, recordToken);
            RequireHealthy();
            Retain(anchors[index]);
            return numeric.Single(p => p.Window.Name == name).Window;
        }
        void Observe(MenuLogEntry entry)
        {
            if (entry.Kind == "pulse-attempt-start")
                anchors.Add(new("pulse", entry.Milliseconds, capture!.TimelineFrame, Complete: false));
            else if (entry.Kind == "phase-start")
            {
                var pending = anchors.FindLastIndex(p => p.Name == entry.Detail && p.EndFrame is null);
                var stable = new Anchor(entry.Detail, entry.Milliseconds, capture!.TimelineFrame);
                if (pending >= 0) anchors[pending] = stable;
                else anchors.Add(stable);
            }
            else if (entry.Kind is "phase-end" or "phase-aborted")
            {
                var index = anchors.FindLastIndex(p => p.Name == entry.Detail);
                anchors[index] = anchors[index] with { EndMs = entry.Milliseconds, EndFrame = capture!.TimelineFrame, Complete = entry.Kind == "phase-end" };
            }
            Emit(entry);
            if (entry.Kind is "phase-start" or "phase-end" or "pulse-precondition" or "pulse-attempt-start" or "control-precondition" or "control-slot-start" or "control-slot-end" or "closed-hold-precondition") RequireHealthy();
        }
        async Task QualifyPhase(string name, CancellationToken phaseToken)
        {
            if (descriptive) { RequireHealthy(); return; }
            if (name is "closed-before" or "menu-only")
            {
                // Before arm only: drainage and the unchanged full-second policy
                // must not extend the active pulse deadline.
                await clock.DelayAsync(MenuAudioSession.BufferDrainMs, phaseToken);
                RequireHealthy();
                var phase = Retain(anchors.Last(p => p.Name == name));
                if (!phase.Window.Valid || phase.CompleteSecondComparisons.Count != 5 || phase.CompleteSecondComparisons.Any(p => !p.Ready))
                    throw new MenuPreconditionException(purpose == ShortRampPurpose.NoReductionControl
                        ? "Preparation/menu-only changed or obscured the frozen reference; control refused."
                        : "Preparation/menu-only changed or obscured the frozen reference; pulse refused.");
            }
        }
        ShortRampAudioPhase Retain(Anchor anchor)
        {
            ShortRampPulseRetention? retention = null;
            ShortRampNativePhaseRetention? nativeRetention = null;
            if (purpose is ShortRampPurpose.BoundedClosedHold or ShortRampPurpose.ContextualClosedHold && anchor.Name is "ramp" or "mode0" or "close" or "closed-hold")
            {
                var observedStart = anchor.StartFrame;
                var observedEnd = anchor.EndFrame ?? capture!.TimelineFrame;
                var native = session.NativePhaseEvidence;
                if (native is not null)
                {
                    if (native.QpcFrequency != capture!.Metadata.QpcFrequency || native.QpcFrequency <= 0)
                        throw new InvalidDataException("Native phase evidence has a foreign capture QPC domain.");
                    var originalStart = anchor.Name switch {
                        "mode0" => native.Mode0AttemptQpc, "close" => native.NormalCloseAttemptQpc,
                        "closed-hold" => native.NormalCloseSettledQpc, _ => native.ArmQpc };
                    var originalEnd = anchor.Name switch {
                        "ramp" => native.Mode0AttemptQpc > 0 ? native.Mode0AttemptQpc : native.RampDeadlineQpc,
                        "mode0" => native.Mode0SettledQpc > 0 ? native.Mode0SettledQpc : native.RampDeadlineQpc,
                        "close" => native.NormalCloseSettledQpc > 0 ? native.NormalCloseSettledQpc : native.HoldDeadlineQpc,
                        _ => native.HoldDeadlineQpc };
                    var start = Math.Max(observedStart, originalStart > 0 ? FrameAtQpc(originalStart) : observedStart);
                    var end = Math.Min(observedEnd, originalEnd > 0 ? FrameAtQpc(originalEnd) : start);
                    long? cutoff = null;
                    var observation = session.ClosedHoldObservation;
                    if (observation is not null)
                    {
                        if (observation.QpcFrequency != native.QpcFrequency)
                            throw new InvalidDataException("Parent native-window observation has a foreign QPC domain.");
                        foreach (var at in new[] { observation.RevokeRequestedQpc, observation.CleanupRequestedQpc })
                            if (at is { } value) cutoff = cutoff is { } prior ? Math.Min(prior, value) : value;
                        if (observation.WorkerObservation is { } independent)
                        {
                            if (independent.QpcFrequency != native.QpcFrequency)
                                throw new InvalidDataException("Independent native-window observation has a foreign QPC domain.");
                            foreach (var at in new[] { independent.RevokedQpc, independent.CleanupStartedQpc })
                                if (at is { } value) cutoff = cutoff is { } prior ? Math.Min(prior, value) : value;
                        }
                    }
                    if (cutoff is { } boundary) end = Math.Min(end, FrameAtQpc(boundary));
                    end = Math.Max(start, end);
                    var established = observation?.WorkerObservation is { EarliestCleanupBoundaryEstablished: true } known &&
                        (known.RevokedQpc is not null || known.CleanupStartedQpc is not null);
                    var truncated = start != observedStart || end != observedEnd;
                    nativeRetention = new(originalStart, originalEnd, observedStart, observedEnd, start, end, cutoff,
                        established, truncated, established
                            ? "Native QPC phase bounds and earliest independent cleanup boundary applied; physical timing remains unverified."
                            : "Inconclusive bounded raw PCM: earliest independent cleanup boundary unknown; may include adjacent context.");
                    anchor = anchor with { StartFrame = start, EndFrame = end,
                        StartMs = anchor.StartMs + (start - observedStart) * 1000 / capture.SampleRate,
                        EndMs = anchor.StartMs + (end - observedStart) * 1000 / capture.SampleRate };
                }
                long FrameAtQpc(long at) => checked((long)Math.Floor((at - capture!.Metadata.StartedTimestamp) *
                    (double)capture.SampleRate / capture.Metadata.QpcFrequency));
            }
            if (anchor.Name is "pulse" or "control-window" && (session.ControlTiming ?? session.PulseTiming) is { } timing)
            {
                var observerEndFrame = anchor.EndFrame ?? capture!.TimelineFrame;
                var observerEndMs = anchor.EndMs ?? clock.Milliseconds;
                var end = Math.Min(observerEndFrame, FrameAt(timing.RequestedDeadlineMilliseconds));
                foreach (var request in new[] { timing.ParentRevokeRequestedMilliseconds, timing.ParentCleanupRequestedMilliseconds })
                    if (request is { } at) end = Math.Min(end, FrameAt(at));
                var observation = timing.WorkerObservation;
                long? earliestObserved = null;
                if (observation is not null)
                {
                    if (observation.QpcFrequency != capture!.Metadata.QpcFrequency || observation.QpcFrequency <= 0)
                    throw new InvalidDataException("Independent active-window boundary has a foreign QPC frequency.");
                    foreach (var qpc in new[] { observation.RevokedQpc, observation.CleanupStartedQpc })
                        if (qpc is { } at) earliestObserved = earliestObserved is { } prior ? Math.Min(prior, at) : at;
                    if (earliestObserved is { } boundary)
                        end = Math.Min(end, checked((long)Math.Floor((boundary - capture.Metadata.StartedTimestamp) *
                            (double)capture.SampleRate / observation.QpcFrequency)));
                }
                end = Math.Max(anchor.StartFrame, end);
                var truncated = end < observerEndFrame;
                var established = observation is { EarliestCleanupBoundaryEstablished: true } && earliestObserved is not null;
                // A normal observer can wake just after the requested deadline.
                // Keep only the requested interval, and call it complete only
                // when independent revoke evidence excludes earlier cleanup.
                var completeRequested = anchor.Complete && established && end == FrameAt(timing.RequestedDeadlineMilliseconds);
                retention = new(timing.RequestedDeadlineMilliseconds, observerEndMs, observerEndFrame, end,
                    earliestObserved, truncated, established, (truncated || !anchor.Complete) && !established
                        ? "inconclusive bounded raw PCM: earliest independent cleanup boundary unknown; may include adjacent context"
                        : truncated && completeRequested ? "complete requested PCM retained; late observer frames excluded using independent revoke evidence"
                        : truncated ? "incomplete bounded PCM: original requested deadline and observed revoke/cleanup cutoff applied"
                        : "bounded numeric PCM; no physical timing inference; independent cleanup boundary " + (established ? "observed" : "unknown"));
                anchor = anchor with { EndFrame = end, EndMs = anchor.StartMs + (end - anchor.StartFrame) * 1000 / capture!.SampleRate,
                    Complete = anchor.Complete && (!truncated || completeRequested) };

                long FrameAt(long milliseconds) => checked(anchor.StartFrame +
                    (long)Math.Floor((milliseconds - anchor.StartMs) * (double)capture!.SampleRate / 1000));
            }
            // Never re-read an already measured window for its raw sidecar: an
            // intermittent second read could otherwise silently lose all raw PCM.
            // A failed read propagates with its type through the evidence failure.
            var samples = capture!.Snapshot(anchor.StartFrame, anchor.EndFrame ?? capture.TimelineFrame);
            var window = Measure(anchor, capture, samples);
            using (var pcm = new FileStream(Path.Combine(Path.GetFullPath(evidenceDirectory), anchor.Name + ".pcm"), FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                var bytes = new byte[samples.Length * 2];
                Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
                pcm.Write(bytes); pcm.Flush(flushToDisk: true);
            }
            var comparisons = new List<MenuAudioReadiness>();
            if (reference is not null && window.Valid && anchor.Name != "initial")
                for (var offset = 0; offset + capture.SampleRate <= window.ActualFrameCount; offset += capture.SampleRate)
                {
                    var second = Measure(new(anchor.Name + "-second-" + offset / capture.SampleRate,
                        anchor.StartMs + offset * 1000L / capture.SampleRate, anchor.StartFrame + offset,
                        anchor.StartMs + offset * 1000L / capture.SampleRate + 1000, anchor.StartFrame + offset + capture.SampleRate), capture);
                    comparisons.Add(CompareReference(second));
                }
            var phase = new ShortRampAudioPhase(window, Qpc(window.StartFrame), Qpc(window.EndFrame), comparisons,
                retention is { EarliestCleanupBoundaryEstablished: false } && !window.Valid ? retention.Classification :
                nativeRetention is { EarliestCleanupBoundaryEstablished: false } ? nativeRetention.Classification :
                !window.Valid ? "inconclusive: " + window.Reason : comparisons.Count == 0
                    ? "inconclusive reference comparison: less than one complete second; RMS is descriptive only"
                    : "numeric complete-second comparisons only; no physical source/target-noise/quiet inference")
            { PulseRetention = anchor.Name == "pulse" ? retention : null,
                ControlRetention = anchor.Name == "control-window" ? retention : null, NativePhaseRetention = nativeRetention };
            numeric.RemoveAll(p => p.Window.Name == anchor.Name); numeric.Add(phase);
            Save(anchor.Name + ".json", phase);
            return phase;
        }
    }
}
