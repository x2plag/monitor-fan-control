using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace MonitorFanControl;

internal sealed partial class HidFanController
{
    internal static IMenuTransport OpenMenuContextTransport() =>
        new MenuNativeTransport(MenuContextWorkerProcess.WorkerNonce ??
            throw new InvalidOperationException("The cleanup worker session is missing."), true);

    internal static IMenuTransport OpenMenuContextTransport(string nonce) => new MenuNativeTransport(nonce, false);
    // This purpose is explicit and never changes the default menu-only policy.
    internal static IMenuTransport OpenShortRampTransport(string nonce, ShortRampPermit permit, bool reservationOwner,
        ShortRampPurpose purpose = ShortRampPurpose.Mode2) => new MenuNativeTransport(nonce, reservationOwner, permit, purpose);
    internal static IMenuTransport OpenShortRampWriterTransport(string nonce, ShortRampPermit permit,
        ShortRampPurpose purpose = ShortRampPurpose.Mode2) => new MenuNativeTransport(nonce, false, permit, purpose);
    // Keep the original named semaphore object alive if its worker is killed.
    // A newly created count-one object cannot represent that stranded lease.
    internal static void RetainMenuContextHidReservation() => _ = InterprocessExchangeLock.SafeWaitHandle;

    // Every kernel mutex is acquired and released by this one OS thread. In
    // particular, an async continuation never inherits mutex ownership.
    private sealed class MenuNativeTransport : IMenuTransport, IMenuCleanupTakeover, IContextualRecoveryTransport
    {
        private sealed record Request(byte[]? Report, bool Reply, CancellationToken Token,
            TaskCompletionSource<byte[]> Completion, long? AdmissionDeadline = null, ContextualRecoveryAuthorization? Recovery = null);
        private readonly BlockingCollection<Request> _requests = new(1);
        private readonly CancellationTokenSource _dispose = new();
        private readonly TaskCompletionSource<MenuDeviceIdentity> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _reservationOwner;
        private readonly ShortRampPermit? _shortPermit;
        private readonly ShortRampPurpose _shortPurpose;
        private MenuFramePolicy _policy;
        public MenuDeviceIdentity Identity { get; }

        internal MenuNativeTransport(string nonce, bool reservationOwner, ShortRampPermit? shortPermit = null,
            ShortRampPurpose purpose = ShortRampPurpose.Mode2)
        {
            if (!Guid.TryParseExact(nonce, "N", out _)) throw new ArgumentException("Invalid menu session nonce.");
            ShortRampPurposePolicy.Validate(purpose);
            if (shortPermit is not null && shortPermit.Purpose != purpose)
                throw new ArgumentException("Short transport and permit purposes differ.");
            _reservationOwner = reservationOwner;
            _shortPermit = shortPermit;
            _shortPurpose = purpose;
            if (shortPermit is not null && shortPermit.Nonce != nonce)
                throw new ArgumentException("Short transport and permit nonces differ.");
            _policy = new MenuFramePolicy(cleanupOnly: reservationOwner);
            var thread = new Thread(() => Run(nonce)) { IsBackground = true, Name = "LG39 menu transport owner" };
            thread.Start();
            try { Identity = _ready.Task.WaitAsync(TimeSpan.FromSeconds(4)).GetAwaiter().GetResult(); }
            catch (TimeoutException)
            {
                // Synchronous Windows descriptor calls cannot be forcibly
                // canceled. Close queue admission; the background owner will
                // release reservations if/when that API returns.
                _dispose.Cancel();
                _requests.CompleteAdding();
                throw new MenuPreconditionException("Menu bridge setup exceeded its bounded readiness wait.");
            }
            catch (MenuPreconditionException) { throw; }
            catch (Exception) { throw new MenuPreconditionException("Worker preconditions could not be inspected."); }
        }

        public Task<byte[]> TransferAsync(byte[] report, bool reply, CancellationToken token) => TransferCore(report, reply, token, null);
        public Task<byte[]> TransferRecoveryAsync(byte[] report, bool reply, ContextualRecoveryAuthorization authorization, CancellationToken token)
            => TransferCore(report, reply, token, authorization);
        private Task<byte[]> TransferCore(byte[] report, bool reply, CancellationToken token, ContextualRecoveryAuthorization? authorization)
        {
            ObjectDisposedException.ThrowIf(_dispose.IsCancellationRequested, this);
            var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_shortPermit is null)
            {
                var queuedReport = (byte[])report.Clone();
                _policy.Validate(queuedReport, reply);
                _requests.Add(new Request(queuedReport, reply, token, completion), token);
            }
            else
            {
                // This no-HID constructible admission owns the exact snapshot
                // submitted to the actual native queue. Execute retains its
                // independent check after transaction acquisition.
                ShortRampNativeQueueAdmission.Enqueue(_shortPermit, _shortPurpose, report, reply, token,
                    (snapshot, admissionToken) => _requests.TryAdd(new Request(snapshot, reply, admissionToken, completion, Recovery: authorization), 0, admissionToken), authorization);
            }
            return completion.Task;
        }

        public async Task ReserveAfterWorkerExitAsync(CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(_dispose.IsCancellationRequested, this);
            if (_reservationOwner) throw new InvalidOperationException("The worker already owns the reservations.");
            var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_shortPermit is null)
            {
                _requests.Add(new Request(null, false, token, completion), token);
                await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            }
            else
            {
                _shortPermit.BeginCleanup();
                var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * ShortRampExecutionGate.TransactionWaitMs / 1000;
                if (!_requests.TryAdd(new Request(null, false, token, completion, deadline), 0, token))
                    throw new TimeoutException("Short cleanup reservation queue is busy.");
                await completion.Task.WaitAsync(TimeSpan.FromMilliseconds(ShortRampExecutionGate.TransactionWaitMs), token);
            }
        }

        private void Run(string nonce)
        {
            using var session = new Mutex(false, @"Local\MonitorFanControl.Menu39.Session");
            using var nvidia = new Mutex(false, @"Local\LG39GXFAN-DDC-DIAG");
            using var transaction = new Mutex(false, @"Local\MonitorFanControl.Menu39.Transaction." + nonce);
            using var releaseStarted = new EventWaitHandle(false, EventResetMode.ManualReset, MenuContextWorkerProcess.HidReleaseMarkerName(nonce));
            var hidReservation = new MenuHidReservation(InterprocessExchangeLock, releaseStarted);
            var ownsSession = false;
            var ownsNvidia = false;
            var ownsHid = false;
            try
            {
                if (_shortPermit is not null && !_reservationOwner)
                {
                    using var self = Process.GetCurrentProcess();
                    var current = new ShortRampOwnedProcessIdentity(Environment.ProcessId, self.StartTime.ToUniversalTime().Ticks, nonce);
                    if (_shortPermit.OwnedWriter != current)
                        throw new MenuPreconditionException("Short native IO belongs only to its authenticated owned writer process.");
                }
                if (_reservationOwner)
                {
                    ownsSession = Enter(session, 0);
                    if (!ownsSession) throw new MenuPreconditionException("Another menu diagnostic session is active.");
                    ownsHid = hidReservation.TryAcquire(2000);
                    if (!ownsHid) throw new MenuPreconditionException("Another HID diagnostic is active.");
                    ownsNvidia = Enter(nvidia, 2000);
                    if (!ownsNvidia) throw new MenuPreconditionException("Another NVIDIA/DDC diagnostic is active.");
                }
                var paths = EnumerateHidPaths().Where(path => path.Contains(
                    "vid_043e&pid_9c33&mi_00", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (paths.Length != 1) throw new MenuPreconditionException("The menu diagnostic requires exactly one LG 9C33 I2C bridge.");
                using var handle = Native.CreateFileW(paths[0], Native.GenericRead | Native.GenericWrite,
                    Native.FileShareRead | Native.FileShareWrite, IntPtr.Zero,
                    Native.OpenExisting, Native.FileFlagOverlapped, IntPtr.Zero);
                if (handle.IsInvalid) throw new MenuPreconditionException("Opening the menu diagnostic bridge failed; the device may be occupied or unavailable.");
                var attributes = new Native.HidAttributes { Size = (uint)Marshal.SizeOf<Native.HidAttributes>() };
                if (!Native.HidD_GetAttributes(handle, ref attributes) || attributes.VendorId != 0x043e ||
                    attributes.ProductId != 0x9c33 || attributes.VersionNumber != 0x0111)
                    throw new MenuPreconditionException("The menu diagnostic bridge identity changed.");
                if (!Native.HidD_GetPreparsedData(handle, out var preparsed)) throw new MenuPreconditionException("Reading the menu bridge descriptor failed.");
                MenuDeviceIdentity identity;
                try
                {
                    if (Native.HidP_GetCaps(preparsed, out var caps) != 0x00110000 ||
                        caps.UsagePage != 0xff00 || caps.Usage != 1 ||
                        caps.InputReportByteLength != 65 || caps.OutputReportByteLength != 65)
                        throw new MenuPreconditionException("The menu diagnostic bridge descriptor changed; no report sent.");
                    identity = new(1, attributes.VendorId, attributes.ProductId, attributes.VersionNumber,
                        caps.UsagePage, caps.Usage, caps.InputReportByteLength, caps.OutputReportByteLength);
                }
                finally { Native.HidD_FreePreparsedData(preparsed); }
                using var stream = new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: true);
                using var shortGate = _shortPermit is null ? null : new ShortRampExecutionGate(
                    _shortPermit, identity, nonce, cleanupOnly: _reservationOwner,
                    nativeIoTimeoutMs: _reservationOwner ? ShortRampExecutionGate.NativeIoTimeoutMs : ShortRampOwnedWriterProcess.NativeIoTimeoutMs,
                    purpose: _shortPurpose);
                _ready.SetResult(identity);
                foreach (var request in _requests.GetConsumingEnumerable())
                {
                    var ownsTransaction = false;
                    var possibleDdcWrite = false;
                    try
                    {
                        request.Token.ThrowIfCancellationRequested();
                        _dispose.Token.ThrowIfCancellationRequested();
                        if (request.Report is null)
                        {
                            // The caller has confirmed this worker's process exit.
                            // Ready proved it held the HID semaphore. The retained
                            // marker distinguishes its stranded lease from a lease
                            // it released normally before another program acquired it.
                            _policy = new MenuFramePolicy(cleanupOnly: true);
                            CheckTakeoverDeadline();
                            ownsSession = Enter(session, 0);
                            if (!ownsSession) throw new MenuPreconditionException("Another menu diagnostic session is active.");
                            ownsHid = hidReservation.TryTakeAfterWorkerExit(TakeoverWait());
                            if (!ownsHid) throw new MenuPreconditionException("Another HID diagnostic is active.");
                            CheckTakeoverDeadline();
                            ownsNvidia = Enter(nvidia, TakeoverWait());
                            if (!ownsNvidia) throw new MenuPreconditionException("Another NVIDIA/DDC diagnostic is active.");
                            CheckTakeoverDeadline();
                            request.Completion.TrySetResult([]);
                            continue;

                            int TakeoverWait()
                            {
                                if (request.AdmissionDeadline is not { } deadline) return 2000;
                                CheckTakeoverDeadline();
                                return (int)Math.Min(ShortRampExecutionGate.TransactionWaitMs,
                                    Math.Max(0, (deadline - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency));
                            }
                            void CheckTakeoverDeadline()
                            {
                                if (request.AdmissionDeadline is not { } deadline) return;
                                request.Token.ThrowIfCancellationRequested();
                                _dispose.Token.ThrowIfCancellationRequested();
                                if (Stopwatch.GetTimestamp() >= deadline)
                                    throw new TimeoutException("Short cleanup reservation admission expired.");
                            }
                        }
                        if (shortGate is not null)
                        {
                            // Execute owns the common transaction mutex. Its
                            // shared permit check/consumption is therefore after
                            // gate acquisition and immediately before this IO.
                            var result = shortGate.Execute(request.Report, request.Reply, request.Token, ioToken =>
                            {
                                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ioToken, _dispose.Token);
                                return MenuNativeStreamTransfer.Execute(stream, request.Report, request.Reply, bounded.Token,
                                    contextualGet4: request.Recovery is not null && request.Reply &&
                                        request.Report[4] == 4 && request.Report[5] == 16);
                            }, request.Recovery);
                            request.Completion.TrySetResult(result);
                            continue;
                        }
                        ownsTransaction = Enter(transaction, 250);
                        if (!ownsTransaction) throw new TimeoutException("The menu transaction lock is busy; cleanup cannot bypass it.");
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(request.Token, _dispose.Token);
                        timeout.CancelAfter(2000);
                        possibleDdcWrite = request.Report[4] == 3;
                        stream.WriteAsync(request.Report, timeout.Token).AsTask().GetAwaiter().GetResult();
                        var response = Array.Empty<byte>();
                        if (request.Reply)
                        {
                            response = new byte[65];
                            var count = stream.ReadAsync(response, timeout.Token).AsTask().GetAwaiter().GetResult();
                            if (count != response.Length) throw new InvalidDataException("Incomplete menu bridge response.");
                        }
                        // Keep the transaction gate during the mandatory MCU settling
                        // time, even when cancellation follows a potentially queued write.
                        if (possibleDdcWrite) Thread.Sleep(180);
                        possibleDdcWrite = false;
                        request.Completion.TrySetResult(response);
                    }
                    catch (Exception ex) { request.Completion.TrySetException(ex); }
                    finally
                    {
                        if (possibleDdcWrite) Thread.Sleep(180);
                        if (ownsTransaction) transaction.ReleaseMutex();
                    }
                }
            }
            catch (Exception ex)
            {
                _ready.TrySetException(ex);
                while (_requests.TryTake(out var pending)) pending.Completion.TrySetException(ex);
            }
            finally
            {
                if (ownsNvidia) nvidia.ReleaseMutex();
                if (ownsHid)
                {
                    hidReservation.Release();
                }
                if (ownsSession) session.ReleaseMutex();
                _ended.TrySetResult();
            }
        }

        private static bool Enter(Mutex mutex, int milliseconds)
        {
            try { return mutex.WaitOne(milliseconds); }
            catch (AbandonedMutexException) { return true; }
        }

        public async ValueTask DisposeAsync()
        {
            _dispose.Cancel();
            _requests.CompleteAdding();
            await _ended.Task.WaitAsync(TimeSpan.FromSeconds(4));
        }
    }
}
