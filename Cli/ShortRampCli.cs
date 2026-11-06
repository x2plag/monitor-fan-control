using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace MonitorFanControl;

internal static class ShortRampCli
{
    private static readonly MenuDeviceIdentity Identity = new(1, 0x043e, 0x9c33, 0x0111, 0xff00, 1, 65, 65);
    internal static async Task<int> RunAsync(string observationPath, ShortRampPurpose purpose = ShortRampPurpose.Mode2)
    {
        var profile = ShortRampSoftwareProfile.ForPurpose(purpose);
        if (!profile.SoftwareQualified) { Console.Error.WriteLine("This short-ramp software profile is unqualified; no capture/worker/HID started."); return 1; }
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            purpose switch
            {
                ShortRampPurpose.NoReductionControl => "MonitorFanControl.ShortControl",
                ShortRampPurpose.BoundedClosedHold => "MonitorFanControl.ClosedHold",
                ShortRampPurpose.ContextualClosedHold => "MonitorFanControl.ContextualHold",
                _ => "MonitorFanControl.ShortRamp"
            },
            "sessions", Guid.NewGuid().ToString("N"));
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var manifestWritten = false;
            var result = await ShortRampQualifiedSession.RunFromObservationFileAsync(profile, observationPath,
                _ =>
                {
                    WriteBuildManifest(); manifestWritten = true;
                    return Task.FromResult(ShortRampYetiCapture.Start(Path.Combine(directory, "capture.wav")));
                },
                token => StartNativeAsync(token, purpose), new MenuSystemClock(), directory, _ => { }, stop.Token);
            if (manifestWritten) WriteBuildManifest(result);
            Console.WriteLine($"Purpose: {result.Purpose}; software profile: {result.SoftwareProfile}; qualified={result.SoftwareQualified}; OSD admission={result.OsdAdmissionAccepted}.");
            if (result.OsdAdmission is { } admission)
                Console.WriteLine($"OSD admission kind: {admission.Kind}; original timestamp: {admission.OriginalTimestampUtc:O}; basis: {admission.Basis}.");
            Console.WriteLine($"ControlClockArmed={result.ControlClockArmed}; Mode2Requested={result.Mode2Requested}; NativeMode2Count={result.NativeMode2Count?.ToString() ?? "unknown"}.");
            if (purpose is ShortRampPurpose.BoundedClosedHold or ShortRampPurpose.ContextualClosedHold)
                Console.WriteLine($"NativeMode0Count={result.NativeMode0Count?.ToString() ?? "unknown"}; NativeNormalCloseCount={result.NativeNormalCloseCount?.ToString() ?? "unknown"}; phase deadlines are retained in build/result evidence.");
            if (result.Audio is null) Console.Error.WriteLine(result.Detail);
            else Console.WriteLine("Physical OSD closure after cleanup and permanent quiet remain unconfirmed. Evidence: " + directory);
            return result.Audio is { Session.State: MenuRunState.Completed, CaptureHealthy: true } ? 0 : 1;

            void WriteBuildManifest(ShortRampQualifiedResult? observed = null) =>
                File.WriteAllText(Path.Combine(directory, "build.json"), JsonSerializer.Serialize(new
                {
                    Purpose = ShortRampPurposePolicy.Token(profile.Purpose), SoftwareProfile = profile.Id,
                    profile.SoftwareQualified, profile.FaultModel,
                    ControlClockArmed = observed?.ControlClockArmed ?? false,
                    Mode2Requested = observed?.Mode2Requested ?? false,
                    NativeMode2Count = observed?.NativeMode2Count,
                    NativeMode0Count = observed?.NativeMode0Count,
                    NativeNormalCloseCount = observed?.NativeNormalCloseCount,
                    NativePhaseEvidence = observed?.NativePhaseEvidence,
                    AutomaticProgress = observed?.Audio?.Session.AutomaticProgress,
                    ContextualProgress = observed?.Audio?.Session.ContextualProgress,
                    ContextualRecoveryEvidence = observed?.Audio?.Session.ContextualEvidence,
                    ContextualTimingEnvelope = purpose == ShortRampPurpose.ContextualClosedHold ? ContextualHoldTimingQualification.Evaluate() : null,
                    Cleanup = observed?.Audio?.Session.Cleanup,
                    ClosedHoldObservation = observed?.Audio?.Session.ClosedHoldObservation,
                    NativeMode2CountMeaning = "observed entries into the native mode2 IO delegate; null means not observed",
                    NativeAttemptCountMeaning = "mode2, mode0 and normal-close counts describe entries into native IO; unknown remains null; submission is not physical completion",
                    Window = purpose switch { ShortRampPurpose.NoReductionControl => "control-window", ShortRampPurpose.BoundedClosedHold or ShortRampPurpose.ContextualClosedHold => "closed-hold", _ => "pulse" },
                    TimingEnvelope = purpose is ShortRampPurpose.BoundedClosedHold or ShortRampPurpose.ContextualClosedHold ? null : ShortRampTimingQualification.Evaluate(),
                    ControlTimingEnvelope = purpose == ShortRampPurpose.NoReductionControl ? ShortRampTimingQualification.EvaluateControl() : null,
                    ClosedHoldTimingEnvelope = purpose == ShortRampPurpose.BoundedClosedHold ? ClosedHoldTimingQualification.Evaluate() : null,
                    ClosedHoldCleanupLimits = purpose == ShortRampPurpose.BoundedClosedHold ? new
                    {
                        ShortRampOwnedWriterProcess.NativeIoTimeoutMs, ShortRampOwnedWriterProcess.ActiveWriteRpcMs,
                        ShortRampOwnedWriterProcess.ActiveReadRpcMs, ShortRampOwnedWriterProcess.CooperativeDrainMs,
                        ShortRampOwnedWriterProcess.TerminateAndReapMs,
                        GuardianNativeIoTimeoutMs = ShortRampExecutionGate.NativeIoTimeoutMs,
                        MinimumNativeQuietMs = ShortRampExecutionGate.NativeQuietGapMs,
                        MinimumOrphanQuietMs = 180, PreparedTakeoverRpcMs = 450,
                        FullRecoveryReserveMs = ShortRampPermit.ClosedHoldFullRecoveryReserveMs
                    } : null,
                    Build = System.Reflection.Assembly.GetExecutingAssembly().GetCustomAttributesData()
                        .FirstOrDefault(a => a.AttributeType == typeof(System.Reflection.AssemblyInformationalVersionAttribute))?.ConstructorArguments[0].Value,
                    OsdAdmission = observed?.OsdAdmission,
                    AdmissionMetadataStatus = observed is null
                        ? "accepted before capture; original provenance retained in osd-admission.json; final result pending"
                        : "original provenance from the accepted admission; no timestamp refresh or physical verification inferred",
                    PhysicalClosureAfterCleanupConfirmed = false,
                    PermanentQuiet = "unverified"
                }));
        }
        finally { Console.CancelKeyPress -= cancel; }
    }
    internal static async Task<IShortRampWorker> StartNativeAsync(CancellationToken token, ShortRampPurpose purpose)
    {
        // Preserve the old production process-conflict refusal. This is a
        // read-only inventory; services and foreign applications are untouched.
        await MenuContextPlatformProcesses.EnsureCompatibleAsync(token);
        HidFanController.RetainMenuContextHidReservation();
        var nonce = Guid.NewGuid().ToString("N");
        return await ShortRampOwnedSession.StartAsync(nonce, Identity, Child("--short39-writer", nonce, purpose),
            Child("--short39-guardian", nonce, purpose), _ => { }, token, purpose: purpose);
    }
    private static ProcessStartInfo Child(string role, string nonce, ShortRampPurpose purpose)
    {
        var child = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Application path missing."))
        { UseShellExecute = false, CreateNoWindow = true };
        child.ArgumentList.Add(role); child.ArgumentList.Add(nonce); child.ArgumentList.Add(Environment.ProcessId.ToString());
        child.ArgumentList.Add(ShortRampPurposePolicy.Token(purpose)); return child;
    }
    internal static async Task<int> RunChildAsync(string role, string nonce, int parentPid,
        ShortRampPurpose purpose = ShortRampPurpose.Mode2)
    {
        ShortRampPurposePolicy.Validate(purpose);
        if (role is not ("--short39-writer" or "--short39-guardian") ||
            !Guid.TryParseExact(nonce, "N", out _) || parentPid <= 0 || parentPid == Environment.ProcessId) return 2;
        using var permit = ShortRampPermit.Open(nonce, Identity, purpose: purpose);
        return role == "--short39-writer"
            ? await ShortRampOwnedWriterProcess.RunAsync(nonce, permit, () => HidFanController.OpenShortRampWriterTransport(nonce, permit, purpose), _ => { }, purpose: purpose)
            : await ShortRampGuardianProcess.RunAsync(nonce, parentPid, permit, () => HidFanController.OpenShortRampTransport(nonce, permit, true, purpose), _ => { }, purpose: purpose);
    }
}
