using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitorFanControl;

// A transport proxy, never a native IO owner. The exact spawned child's handle
// is retained until its exit is observed. Only that identity may be terminated.
public sealed class ShortRampOwnedWriterProcess : IMenuTransport, IMenuCleanupTakeover, IContextualRecoveryTransport
{
    public const int StopDeadlineMs = 1300;
    public const int TerminateAndReapMs = 300;
    public const int NativeIoTimeoutMs = 60;
    public const int ActiveWriteRpcMs = 280; //60 native +180 quiet +40 pipe/scheduling allowance.
    public const int ActiveReadRpcMs = 100; //60 native +40 pipe/scheduling allowance.
    public const int CooperativeDrainMs = 280; //60 native +180 quiet +40 release/dispatch allowance.
    private const int ReadyMs = 5000;
    private const int TransferMs = 450;
    // Latest original-arm mode0 attempt + complete pre-Close tail + bounded
    // receipt dispatch. Native admission still uses its original arm/ready caps.
    private const int ReferenceActivationAcknowledgmentMs = ShortRampPermit.ReferenceMode0AttemptMs +
        ShortRampPermit.PersistentPreCloseMs + 460;
    private sealed record Message(string Kind, string Nonce, ShortRampOwnedProcessIdentity? Process = null,
        MenuDeviceIdentity? Identity = null, byte[]? Report = null, bool Reply = false, byte[]? Response = null,
        string? Error = null, long Deadline = 0, long RequestId = 0, ShortRampPurpose? Purpose = null,
        ContextualRecoveryAuthorization? Recovery = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Contract = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReferenceActivationReceipt? Receipt = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReferenceReleasePreparation? Release = null);
    private readonly Process _process;
    private readonly NamedPipeServerStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly ShortRampPermit _permit;
    private readonly SemaphoreSlim _rpc = new(1, 1);
    private bool _disposed;
    private bool _poisoned;
    private bool _nativeReady;
    private long _requestId;
    public ShortRampOwnedProcessIdentity ProcessIdentity { get; }
    public MenuDeviceIdentity Identity => _permit.Identity;
    public ShortRampPurpose Purpose => _permit.Purpose;
    public int NativeMode2Count => _permit.NativeMode2Count;
    public int NativeMode0Count => _permit.NativeMode0Count;
    public int NativeNormalCloseCount => _permit.NativeNormalCloseCount;
    public ShortRampNativePhaseEvidence? NativePhaseEvidence => _permit.NativePhaseEvidence;

    private ShortRampOwnedWriterProcess(Process process, NamedPipeServerStream pipe, StreamReader reader,
        StreamWriter writer, ShortRampPermit permit, ShortRampOwnedProcessIdentity identity)
    { _process = process; _pipe = pipe; _reader = reader; _writer = writer; _permit = permit;
      ProcessIdentity = identity; }

    public static async Task<ShortRampOwnedWriterProcess> StartAsync(ProcessStartInfo child, string nonce,
        ShortRampPermit permit, Action<MenuLogEntry> log, CancellationToken token,
        Func<ShortRampOwnedWriterProcess, CancellationToken, Task>? afterBootRegistered = null,
        ShortRampPurpose purpose = ShortRampPurpose.Mode2)
    {
        ShortRampPurposePolicy.Validate(purpose);
        if (permit.Purpose != purpose) throw new InvalidDataException("Foreign owned writer purpose.");
        if (permit.Nonce != nonce || !Guid.TryParseExact(nonce, "N", out _)) throw new InvalidDataException("Foreign writer nonce.");
        child.UseShellExecute = false; child.CreateNoWindow = true;
        var pipe = new NamedPipeServerStream(PipeName(nonce), PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Process? process = null;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(ReadyMs);
            process = Process.Start(child) ?? throw new IOException("Owned writer could not start.");
            _ = process.Handle;
            await pipe.WaitForConnectionAsync(limit.Token);
            var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            var boot = await ReadAsync(reader, limit.Token, purpose);
            var identity = new ShortRampOwnedProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks, nonce);
            if (boot.Kind != "boot" || boot.Nonce != nonce || boot.Process != identity || process.HasExited)
                throw new InvalidDataException("Owned writer PID/start-time/nonce handshake failed.");
            permit.RegisterOwnedWriter(identity);
            var registered = new ShortRampOwnedWriterProcess(process, pipe, reader, writer, permit, identity);
            // The registered proxy supplies identity to guardian preparation,
            // while native setup remains blocked on the authenticated start.
            if (afterBootRegistered is not null) await afterBootRegistered(registered, limit.Token);
            await WriteAsync(writer, new("start", nonce), limit.Token, purpose);
            var ready = await ReadAsync(reader, limit.Token, purpose);
            if (ready.Kind != "ready" || ready.Nonce != nonce || ready.Identity != permit.Identity || process.HasExited)
                throw new InvalidDataException("Owned writer native transport was not ready.");
            registered._nativeReady = true;
            return registered;
        }
        catch
        {
            permit.Revoke();
            await pipe.DisposeAsync();
            if (process is not null)
            {
                try { using var reap = new CancellationTokenSource(TerminateAndReapMs); await TerminateExactAsync(process, reap.Token); await process.WaitForExitAsync(reap.Token); }
                catch (Exception) { SafeLog(log, "owned-writer-start-exit-unconfirmed", "Spawned child exit was not confirmed."); }
                process.Dispose();
            }
            throw;
        }
    }

    // No public application dispatch is added here. Child callers must explicitly
    // select the production native adapter or an offline injected IO transport.
    public static async Task<int> RunAsync(string nonce, ShortRampPermit permit, Func<IMenuTransport> transportFactory,
        Action<MenuLogEntry> log, Func<CancellationToken, Task>? beforeDispatch = null,
        ShortRampPurpose purpose = ShortRampPurpose.Mode2)
    {
        if (permit.Purpose != purpose || !Enum.IsDefined(purpose) || permit.Nonce != nonce) return 2;
        IMenuTransport? transport = null;
        Process? guardian = null;
        var exitCode = 0;
        using var released = new CancellationTokenSource();
        using var actorLifetime = new CancellationTokenSource(purpose == ShortRampPurpose.ContextualClosedHold ? 120000 : Timeout.Infinite);
        var releaseObserver = permit.ObserveReferenceReleaseAsync(released, actorLifetime.Token);
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName(nonce), PipeDirection.InOut, PipeOptions.Asynchronous);
            using var startup = new CancellationTokenSource(ReadyMs);
            await pipe.ConnectAsync(startup.Token);
            using var self = Process.GetCurrentProcess();
            var identity = new ShortRampOwnedProcessIdentity(Environment.ProcessId, self.StartTime.ToUniversalTime().Ticks, nonce);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            await WriteAsync(writer, new("boot", nonce, Process: identity), startup.Token, purpose);
            var start = await ReadAsync(reader, startup.Token, purpose);
            if (start.Kind != "start" || start.Nonce != nonce || permit.OwnedWriter != identity)
                throw new InvalidDataException("Owned writer launch was not authenticated.");
            if (permit.IsReference)
            {
                permit.ValidateReferenceBinding(true);
                if (!permit.GuardianAlive()) throw new InvalidDataException("Reference native factory requires the exact ready independent guardian.");
            }
            transport = transportFactory();
            if (transport.Identity != permit.Identity) throw new InvalidDataException("Owned writer transport identity changed.");
            await WriteAsync(writer, new("ready", nonce, Identity: transport.Identity), startup.Token, purpose);
            long lastReferenceRequest = 0;
            while (true)
            {
                // A finite lifetime also closes a forgotten unarmed fixture.
                using var receive = CancellationTokenSource.CreateLinkedTokenSource(actorLifetime.Token, released.Token);
                if (!permit.UsesContextualRecovery) receive.CancelAfter(60000);
                var request = await ReadAsync(reader, receive.Token, purpose);
                if (permit.IsReference)
                {
                    permit.ValidateReferenceBinding(false);
                    if (request.Nonce != nonce || request.RequestId <= lastReferenceRequest)
                        throw new InvalidDataException("Reference writer request token is foreign or replayed.");
                    lastReferenceRequest = request.RequestId;
                }
                if (permit.IsReference && request.Kind == "reference-activate" && request.Nonce == nonce && request.RequestId > 0)
                {
                    try
                    {
                        if (beforeDispatch is not null) await beforeDispatch(receive.Token);
                        var receipt = await ReferencePersistentActivation.RunWorkerLoopAsync(permit, transport, new MenuSystemClock(), receive.Token);
                        await WriteAsync(writer, new("reference-activated", nonce, RequestId: request.RequestId, Receipt: receipt), receive.Token, purpose);
                    }
                    catch (Exception ex)
                    { permit.Revoke(); await WriteAsync(writer, new("reference-activated", nonce, RequestId: request.RequestId,
                        Error: ex.GetType().Name + ": " + ex.Message.Split(['\r', '\n'])[0][..Math.Min(ex.Message.Split(['\r', '\n'])[0].Length, 512)]), receive.Token, purpose); }
                    continue;
                }
                if (permit.IsPersistent && request.Kind == "health" && request.Nonce == nonce && request.RequestId > 0)
                {
                    // This dispatch uses the same serialized actor loop as IO;
                    // a hung native call cannot keep acknowledging health.
                    await WriteAsync(writer, new("healthy", nonce, RequestId: request.RequestId), receive.Token, purpose);
                    continue;
                }
                if (request.Kind == "release-prepare" && request.Nonce == nonce && request.Release is not null &&
                    request.RequestId == lastReferenceRequest)
                {
                    lastReferenceRequest = request.RequestId;
                    permit.AcknowledgeReferenceRelease(request.Release, writer: true);
                    await WriteAsync(writer, new("release-ready", nonce, Process: identity, RequestId: request.RequestId, Release: request.Release), receive.Token, purpose);
                    continue;
                }
                if (request.Kind == "takeover" && request.Nonce == nonce)
                {
                    if (beforeDispatch is not null) await beforeDispatch(receive.Token);
                    var remaining = (request.Deadline - Stopwatch.GetTimestamp()) * 1000d / Stopwatch.Frequency;
                    if (remaining < 150 || remaining > 450) throw new TimeoutException("Prepared takeover request expired before reservation admission.");
                    if (guardian is null || !guardian.HasExited || transport is not IMenuCleanupTakeover takeover)
                        throw new IOException("Exact guardian exit was not confirmed by the prepared writer.");
                    permit.Revoke();
                    using var reserve = new CancellationTokenSource(150);
                    await takeover.ReserveAfterWorkerExitAsync(reserve.Token).WaitAsync(TimeSpan.FromMilliseconds(150), reserve.Token);
                    if (Stopwatch.GetTimestamp() >= request.Deadline) throw new TimeoutException("Prepared takeover reservation acknowledgment was late.");
                    permit.ConfirmWriterCleanupTakeover(identity);
                    await WriteAsync(writer, new("takeover-ready", nonce, RequestId: request.RequestId), receive.Token, purpose);
                    continue;
                }
                if (request.Nonce != nonce || request.Kind != "transfer" || request.Report is null || request.RequestId <= 0)
                    throw new InvalidDataException("Unsupported owned writer request.");
                if (guardian is null)
                {
                    var owner = permit.GuardianIdentity ?? throw new IOException("Guardian identity missing before first native request.");
                    guardian = Process.GetProcessById(owner.Pid); _ = guardian.Handle;
                    if (guardian.StartTime.ToUniversalTime().Ticks != owner.StartUtcTicks || guardian.HasExited)
                        throw new InvalidDataException("Guardian identity changed before handle retention.");
                }
                ShortRampExecutionGate.ValidateForQueue(request.Report, request.Reply, permit.Purpose);
                permit.ValidateQueuedFrame(request.Report, request.Reply, request.Recovery);
                try
                {
                    if (beforeDispatch is not null) await beforeDispatch(receive.Token);
                    var remaining = (request.Deadline - Stopwatch.GetTimestamp()) * 1000d / Stopwatch.Frequency;
                    if (remaining <= 0 || remaining > TransferMs) throw new TimeoutException("Owned writer queued request expired.");
                    if (permit.Snapshot.State is not ShortRampPermitState.Unarmed)
                    {
                        var nativeEnvelope = NativeIoTimeoutMs + (request.Report[4] == 3 ? ShortRampExecutionGate.NativeQuietGapMs : 0);
                        if (remaining < nativeEnvelope) throw new TimeoutException("Active RPC lacks its complete native/settling reserve; no write admitted.");
                    }
                    using var transfer = new CancellationTokenSource(TimeSpan.FromMilliseconds(remaining));
                    var response = request.Recovery is { } route
                        ? transport is IContextualRecoveryTransport contextual
                            ? await contextual.TransferRecoveryAsync(request.Report, request.Reply, route, transfer.Token)
                            : throw new IOException("Writer transport lacks authenticated contextual recovery.")
                        : await transport.TransferAsync(request.Report, request.Reply, transfer.Token);
                    if (Stopwatch.GetTimestamp() >= request.Deadline)
                    { permit.Revoke(); throw new TimeoutException("Native/settling response exceeded its absolute RPC deadline."); }
                    await WriteAsync(writer, new("response", nonce, Response: response, RequestId: request.RequestId), receive.Token, purpose);
                }
                catch (Exception ex)
                { await WriteAsync(writer, new("response", nonce, Error: permit.IsReference
                    ? ex.GetType().Name + ": " + ex.Message.Split(['\r', '\n'])[0][..Math.Min(ex.Message.Split(['\r', '\n'])[0].Length, 512)]
                    : ex.GetType().Name, RequestId: request.RequestId), receive.Token, purpose); }
            }
        }
        catch (OperationCanceledException) when (permit.ReferenceReleased) { }
        catch (EndOfStreamException) { }
        catch (Exception ex) { permit.Revoke(); SafeLog(log, "owned-writer-failed", ex.GetType().Name); exitCode = 1; }
        finally
        {
            actorLifetime.Cancel();
            await releaseObserver;
            guardian?.Dispose();
            if (transport is not null)
                try { await transport.DisposeAsync(); } catch (Exception ex) { exitCode = 1; SafeLog(log, "owned-writer-dispose-failed", ex.GetType().Name); }
            if (exitCode == 0) permit.ConfirmReferenceFinalized(writer: true);
        }
        return exitCode;
    }

    public Task<byte[]> TransferAsync(byte[] report, bool reply, CancellationToken token) => TransferCoreAsync(report, reply, token, null);
    public async Task<ReferenceActivationReceipt> ActivateReferenceAsync(CancellationToken token)
    {
        if (!_permit.IsReference || !_nativeReady || _disposed || _poisoned || _process.HasExited)
            throw new IOException("Reference writer is unavailable.");
        using var cancellation = token.UnsafeRegister(static value => ((ShortRampPermit)value!).Revoke(), _permit);
        token.ThrowIfCancellationRequested();
        if (!await _rpc.WaitAsync(0, token)) throw new TimeoutException("Reference writer is busy.");
        var synchronized = false;
        using var limit = new CancellationTokenSource(ReferenceActivationAcknowledgmentMs);
        try
        {
            var id = Interlocked.Increment(ref _requestId);
            await WriteAsync(_writer, new("reference-activate", _permit.Nonce, RequestId: id), limit.Token, _permit.Purpose);
            var result = await ReadAsync(_reader, limit.Token, _permit.Purpose);
            if (result.Kind != "reference-activated" || result.Nonce != _permit.Nonce || result.RequestId != id)
                throw new InvalidDataException("Foreign reference receipt.");
            synchronized = true;
            if (result.Error is not null || result.Receipt is null || result.Receipt != null &&
                (result.Receipt.Nonce != _permit.Nonce || result.Receipt.Writer != ProcessIdentity ||
                    result.Receipt.Guardian != _permit.GuardianIdentity || result.Receipt.Contract != ShortRampPermit.ReferenceContract ||
                    result.Receipt.Profile != ShortRampSoftwareProfile.ReferencePersistentQuiet.Id ||
                    result.Receipt.Samples is not { Length: > 0 and <= ShortRampPermit.ReferenceSampleCapacity }))
                throw new IOException("Reference native activation is unconfirmed: " + result.Error);
            token.ThrowIfCancellationRequested(); return result.Receipt!;
        }
        catch { if (!synchronized) _poisoned = true; _permit.Revoke(); throw; }
        finally { _rpc.Release(); }
    }
    internal bool ExactProcessAlive => !_disposed && !_poisoned && !_process.HasExited &&
        _permit.OwnedWriter == ProcessIdentity && _process.StartTime.ToUniversalTime().Ticks == ProcessIdentity.StartUtcTicks;
    internal async Task PrepareReleaseAsync(ReferenceReleasePreparation preparation, CancellationToken token)
    {
        if (!ExactProcessAlive || !_nativeReady) throw new IOException("Exact writer unavailable for release.");
        using var cancellation = token.UnsafeRegister(static value => ((ShortRampPermit)value!).Revoke(), _permit);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(500);
        await _rpc.WaitAsync(limit.Token);
        try
        {
            var id = Interlocked.Increment(ref _requestId);
            await WriteAsync(_writer, new("release-prepare", _permit.Nonce, RequestId: id, Release: preparation), limit.Token, Purpose);
            var response = await ReadAsync(_reader, limit.Token, Purpose);
            if (response.Kind != "release-ready" || response.Nonce != _permit.Nonce || response.RequestId != id ||
                response.Process != ProcessIdentity || response.Release != preparation || !ExactProcessAlive)
                throw new InvalidDataException("Foreign writer release acknowledgment.");
        }
        catch { _poisoned = true; _permit.Revoke(); throw; }
        finally { _rpc.Release(); }
    }
    internal async Task CheckHealthAsync(CancellationToken token)
    {
        if (_permit.ReferenceReleased) return;
        if (!_permit.IsPersistent || !_nativeReady || _disposed || _poisoned || _process.HasExited)
            throw new IOException("Owned writer health is unavailable.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(250);
        var entered = false;
        try
        {
            await _rpc.WaitAsync(limit.Token); entered = true;
            var id = Interlocked.Increment(ref _requestId);
            await WriteAsync(_writer, new("health", _permit.Nonce, RequestId: id), limit.Token, _permit.Purpose);
            var response = await ReadAsync(_reader, limit.Token, _permit.Purpose);
            if (response.Kind != "healthy" || response.Nonce != _permit.Nonce || response.RequestId != id || _process.HasExited)
                throw new InvalidDataException("Owned writer health acknowledgment changed.");
        }
        catch { _poisoned = true; _permit.Revoke(); throw; }
        finally { if (entered) _rpc.Release(); }
    }
    public Task<byte[]> TransferRecoveryAsync(byte[] report, bool reply, ContextualRecoveryAuthorization authorization, CancellationToken token)
        => TransferCoreAsync(report, reply, token, authorization);
    private async Task<byte[]> TransferCoreAsync(byte[] report, bool reply, CancellationToken token, ContextualRecoveryAuthorization? authorization)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ShortRampExecutionGate.ValidateForQueue(report, reply, _permit.Purpose);
        _permit.ValidateQueuedFrame(report, reply, authorization);
        var kind = ShortRampExecutionGate.Classify(report, reply);
        using var cancellation = token.UnsafeRegister(static value => ((ShortRampPermit)value!).Revoke(), _permit);
        token.ThrowIfCancellationRequested();
        if (!_nativeReady) throw new IOException("Owned writer native setup is not ready; no request queued.");
        if (_poisoned || _process.HasExited) throw new IOException("Owned writer is unavailable; no request queued.");
        if (authorization is null && _permit.State is ShortRampPermitState.Revoked or ShortRampPermitState.Cleanup &&
            kind is not (ShortRampFrame.Automatic or ShortRampFrame.Close))
            throw new IOException("Short execution was revoked; no normal request queued.");
        var bounded = _permit.Snapshot.State is not ShortRampPermitState.Unarmed;
        var transferMs = bounded ? report[4] == 3 ? ActiveWriteRpcMs : ActiveReadRpcMs : TransferMs;
        // Caller cancellation revokes admission immediately, but cannot abandon
        // a possibly dispatched frame. Reader ownership lasts to its original
        // deadline, so takeover cannot consume a stale transfer response.
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * transferMs / 1000;
        using var limit = new CancellationTokenSource(transferMs);
        if (!await _rpc.WaitAsync(0, token)) throw new TimeoutException("Owned writer request is busy; no queue admitted.");
        var synchronized = false;
        var dispatched = false;
        try
        {
            token.ThrowIfCancellationRequested();
            var requestId = Interlocked.Increment(ref _requestId);
            dispatched = true;
            await WriteAsync(_writer, new("transfer", _permit.Nonce, Report: (byte[])report.Clone(), Reply: reply,
                Deadline: deadline, RequestId: requestId, Recovery: authorization), limit.Token, _permit.Purpose);
            var result = await ReadAsync(_reader, limit.Token, _permit.Purpose);
            if (result.Kind != "response" || result.Nonce != _permit.Nonce || result.RequestId != requestId)
                throw new IOException("Owned writer response was not authenticated.");
            synchronized = true;
            if (Stopwatch.GetTimestamp() >= deadline) throw new TimeoutException("Owned writer response arrived after its absolute RPC deadline.");
            if (result.Error is not null)
                throw new IOException("Owned native transfer unconfirmed: " + result.Error);
            token.ThrowIfCancellationRequested();
            return result.Response ?? [];
        }
        catch { if (dispatched && !synchronized) _poisoned = true; _permit.Revoke(); throw; }
        finally { _rpc.Release(); }
    }

    public async Task ReserveAfterWorkerExitAsync(CancellationToken token)
    {
        _permit.ThrowIfReferenceReleased();
        _permit.Revoke();
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * TransferMs / 1000;
        if (!_poisoned && !_process.HasExited)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TransferMs);
            // Any previous transfer is already bounded by its original280/100ms
            // drain. Its remaining time is part of this450ms, including150ms
            // native reservation; recovery receives no fresh timer after waiting.
            await _rpc.WaitAsync(limit.Token);
            try
            {
                if (_poisoned || _process.HasExited || RemainingMs(deadline) < 150)
                    throw new IOException("Prepared writer response drain left no authenticated reservation budget.");
                var requestId = Interlocked.Increment(ref _requestId);
                await WriteAsync(_writer, new("takeover", _permit.Nonce, Deadline: deadline, RequestId: requestId), limit.Token, _permit.Purpose);
                var response = await ReadAsync(_reader, limit.Token, _permit.Purpose);
                if (Stopwatch.GetTimestamp() >= deadline) throw new TimeoutException("Prepared takeover reply was late.");
                if (response.Kind != "takeover-ready" || response.Nonce != _permit.Nonce || response.RequestId != requestId || !_permit.WriterTookCleanup)
                    throw new IOException("Prepared writer takeover was not confirmed.");
                return;
            }
            catch { _poisoned = true; throw; }
            finally { _rpc.Release(); }
        }
        throw new IOException("No cooperative prepared writer remains; combined-fault recovery is unconfirmed.");
    }
    private Task? _disposeTask;
    private readonly object _disposeLock = new();
    public ValueTask DisposeAsync()
    {
        lock (_disposeLock) return new(_disposeTask ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        if (_disposed) return; _disposed = true;
        _permit.Revoke();
        await _pipe.DisposeAsync();
        try
        {
            if (_permit.ReferenceReleased)
            {
                try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (TimeoutException) { /* Exact bounded resource reap below; final mark stays required. */ }
            }
            using var reap = new CancellationTokenSource(TerminateAndReapMs);
            await TerminateExactAsync(_process, reap.Token);
            await _process.WaitForExitAsync(reap.Token);
            if (_permit.OwnedWriter == ProcessIdentity) _permit.ConfirmOwnedWriterExit(ProcessIdentity);
            if (_permit.ReferenceReleased && _process.ExitCode != 0)
                throw new IOException("Released writer exited unsuccessfully after resource disposal.");
        }
        finally { _reader.Dispose(); try { _writer.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { } _process.Dispose(); }
    }
    private static string PipeName(string nonce) => "MonitorFanControl.ShortRamp.Writer." + nonce;
    private static double RemainingMs(long deadline) => (deadline - Stopwatch.GetTimestamp()) * 1000d / Stopwatch.Frequency;
    private static async Task<Message> ReadAsync(StreamReader reader, CancellationToken token, ShortRampPurpose purpose)
    {
        var line = await reader.ReadLineAsync(token) ?? throw new EndOfStreamException("Owned writer pipe closed.");
        // Only the immutable v5 reference receipt carries the expanded bounded
        // sample ledger. Purposes0..4 retain their original framing limit.
        if (line.Length > (purpose == ShortRampPurpose.ReferencePersistentQuiet ? 8192 : 4096))
            throw new InvalidDataException("Oversized writer message.");
        var message = JsonSerializer.Deserialize<Message>(line) ?? throw new InvalidDataException("Empty writer message.");
        if (message.Purpose != purpose) throw new InvalidDataException("Owned writer message purpose was not authenticated.");
        if (purpose == ShortRampPurpose.ReferencePersistentQuiet && message.Contract != ShortRampPermit.ReferenceContract)
            throw new InvalidDataException("Owned writer reference contract/profile ABI was not authenticated.");
        return message;
    }
    private static async Task WriteAsync(StreamWriter writer, Message message, CancellationToken token, ShortRampPurpose purpose)
    { await writer.WriteLineAsync(JsonSerializer.Serialize(message with { Purpose = purpose,
        Contract = purpose == ShortRampPurpose.ReferencePersistentQuiet ? ShortRampPermit.ReferenceContract : null }).AsMemory(), token); await writer.FlushAsync(token); }
    private static void SafeLog(Action<MenuLogEntry> log, string kind, string detail)
    { try { log(new(0, kind, detail)); } catch (Exception) { } }
    private static Task TerminateExactAsync(Process process, CancellationToken token, Action<Process>? terminate = null) =>
        Task.Run(() => { if (!process.HasExited) (terminate ?? (p => p.Kill()))(process); }).WaitAsync(token);

    internal sealed class OwnedWriterHandle : IDisposable
    {
        private readonly Process _process;
        private readonly ShortRampPermit _permit;
        private readonly ShortRampOwnedProcessIdentity _identity;
        internal bool HasExited => _process.HasExited;
        private OwnedWriterHandle(Process process, ShortRampPermit permit, ShortRampOwnedProcessIdentity identity)
        { _process = process; _permit = permit; _identity = identity; }
        internal static OwnedWriterHandle Retain(ShortRampPermit permit)
        {
            var identity = permit.OwnedWriter ?? throw new InvalidDataException("Owned writer identity missing.");
            if (identity.Pid == Environment.ProcessId || identity.Nonce != permit.Nonce) throw new InvalidDataException("Foreign owned writer.");
            var process = Process.GetProcessById(identity.Pid);
            try
            {
                _ = process.Handle;
                if (process.StartTime.ToUniversalTime().Ticks != identity.StartUtcTicks)
                    throw new InvalidDataException("Owned writer PID was reused; no termination allowed.");
                return new(process, permit, identity);
            }
            catch { process.Dispose(); throw; }
        }
        internal async Task<bool> StopAsync(Action<MenuLogEntry> log, Action<Process>? requestTermination = null)
        {
            _permit.Revoke();
            using var limit = new CancellationTokenSource(TerminateAndReapMs);
            try
            {
                await TerminateExactAsync(_process, limit.Token, requestTermination);
                await _process.WaitForExitAsync(limit.Token);
                if (!_process.HasExited) throw new IOException("Owned writer exit was not observed.");
                _permit.ConfirmOwnedWriterExit(_identity);
                SafeLog(log, "owned-writer-exit-confirmed", _identity.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return true;
            }
            catch (Exception ex) { SafeLog(log, "owned-writer-exit-unconfirmed", ex.GetType().Name); return false; }
        }
        internal async Task<bool> PrepareCleanupAsync(Action<MenuLogEntry> log, Action<Process>? requestTermination = null)
        {
            _permit.Revoke();
            if (_process.HasExited) return await StopAsync(log, requestTermination);
            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * CooperativeDrainMs / 1000;
            var retired = await Task.Run(() =>
            {
                // Mutex ownership is thread-affine. Wait, retirement and release
                // are synchronous on this same thread, never across an await.
                using var transaction = new Mutex(false, _permit.TransactionName);
                var remaining = Math.Max(0, (int)Math.Floor(RemainingMs(deadline)));
                if (!ShortRampPermit.TryEnter(transaction, remaining)) return false;
                try
                {
                    if (_process.HasExited) return false;
                    _permit.RetireCooperativeWriter();
                    return true;
                }
                finally { transaction.ReleaseMutex(); }
            });
            if (retired)
            {
                SafeLog(log, "owned-writer-retired", _identity.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return true;
            }
            return await StopAsync(log, requestTermination);
        }
        public void Dispose() => _process.Dispose();
    }
}
