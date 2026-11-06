using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MonitorFanControl;

public sealed record MenuAudioDevice(uint Id, string Name);

/// <summary>
/// Bounded local PCM capture from the explicitly selected Yeti X input only.
/// Frame timestamps are derived from a monotonic start anchor and the PCM sample
/// rate, not callback arrival times. The start-call bracket is retained because
/// winmm does not provide a hardware timestamp for the first captured sample.
/// This records uncalibrated PCM; dBFS must not be described as sound pressure.
/// </summary>
public sealed class MenuAudioCapture : IDisposable
{
    private const int Rate = 48000;
    private const int BufferFrames = Rate / 10;
    private const int BufferCount = 8;
    private const uint CallbackEvent = 0x50000;
    private const uint HeaderDone = 1;
    private const uint StillPlaying = 33;
    private static readonly int HeaderSize = Marshal.SizeOf<WaveHeader>();
    private static readonly int HeaderFlagsOffset = Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.Flags)).ToInt32();
    // If a faulty driver refuses reset/unprepare/close, keep every native buffer
    // and its event alive rather than free memory that it may still reference.
    private static readonly List<MenuAudioCapture> RetainedNativeOwners = [];
    private readonly object _samplesLock = new();
    private readonly object _stopLock = new();
    private readonly EventWaitHandle _dataReady = new(false, EventResetMode.AutoReset);
    private readonly short[] _samples;
    private readonly List<NativeBuffer> _buffers = [];
    private readonly FileStream _wave;
    private IntPtr _handle;
    private Thread? _pump;
    private int _sampleCount;
    private int _nextBuffer;
    private int _running;
    private int _stopRequested;
    private long _lastDataTimestamp;
    private string? _error;
    private bool _nativeMemoryRetained;
    private bool _disposed;

    private MenuAudioCapture(string wavePath, int maxSeconds)
    {
        _samples = new short[checked(maxSeconds * Rate)];
        WavePath = Path.GetFullPath(wavePath);
        try
        {
            _wave = new FileStream(WavePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            WriteWaveHeader(_wave, 0);
        }
        catch
        {
            _wave?.Dispose();
            _dataReady.Dispose();
            throw;
        }
    }

    public int SampleRate => Rate;
    public int Channels => 1;
    public int BitsPerSample => 16;
    public int MaxFrames => _samples.Length;
    public string WavePath { get; }
    public string DeviceName { get; private set; } = "";
    public uint DeviceId { get; private set; }
    public DateTimeOffset StartedAtUtc { get; private set; }
    public long StartedTimestamp { get; private set; }
    public long StartCompletedTimestamp { get; private set; }
    public long StopwatchFrequency => Stopwatch.Frequency;
    public bool IsRunning => Volatile.Read(ref _running) != 0;
    public long SampleCount { get { lock (_samplesLock) return _sampleCount; } }
    public string? Error => Volatile.Read(ref _error) ??
        (IsRunning && Stopwatch.GetElapsedTime(Volatile.Read(ref _lastDataTimestamp)).TotalSeconds > 2
            ? "No complete PCM block received for over two seconds." : null);
    public bool Healthy => Error is null;

    public static IReadOnlyList<MenuAudioDevice> EnumerateDevices()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows microphone capture is required.");
        var devices = new List<MenuAudioDevice>();
        var count = WaveInGetNumDevs();
        for (uint i = 0; i < count; i++)
        {
            Check(WaveInGetDevCaps((UIntPtr)i, out var caps, (uint)Marshal.SizeOf<WaveInCaps>()), "waveInGetDevCaps");
            devices.Add(new(i, caps.Name));
        }
        return devices;
    }

    public static MenuAudioCapture StartYeti(string wavePath, int maxSeconds = 120)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wavePath);
        if (maxSeconds is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(maxSeconds), "Capture must be bounded to 1..120 seconds.");
        var matches = EnumerateDevices().Where(d => IsYetiXName(d.Name)).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException($"Exactly one Yeti X capture input is required; found {matches.Length}. No default input was selected.");
        var capture = new MenuAudioCapture(wavePath, maxSeconds);
        try
        {
            capture.DeviceId = matches[0].Id;
            capture.DeviceName = matches[0].Name;
            capture.Start();
            return capture;
        }
        catch (Exception startFailure)
        {
            try
            {
                capture.ReleaseNative();
                capture.SaveWave();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Microphone start and cleanup failed.", startFailure, cleanupFailure);
            }
            finally
            {
                capture._wave.Dispose();
                if (capture._handle == IntPtr.Zero) capture._dataReady.Dispose();
            }
            throw;
        }
    }

    private static bool IsYetiXName(string name)
    {
        var normalized = new string(name.Where(char.IsLetterOrDigit).ToArray());
        return normalized.Contains("YetiX", StringComparison.OrdinalIgnoreCase);
    }

    public short[] Snapshot(long startFrame, long endFrame)
    {
        lock (_samplesLock)
        {
            if (startFrame < 0 || endFrame < startFrame || endFrame > _sampleCount)
                throw new ArgumentOutOfRangeException(nameof(endFrame), "Requested PCM interval has not been captured.");
            return _samples.AsSpan(checked((int)startFrame), checked((int)(endFrame - startFrame))).ToArray();
        }
    }

    public long TimestampForFrame(long frame)
    {
        if (frame < 0 || frame > _samples.Length) throw new ArgumentOutOfRangeException(nameof(frame));
        return StartedTimestamp + checked((long)Math.Round(frame * (double)Stopwatch.Frequency / Rate));
    }

    public long FrameForTimestamp(long timestamp)
    {
        var frame = (long)Math.Floor((timestamp - StartedTimestamp) * (double)Rate / Stopwatch.Frequency);
        return Math.Clamp(frame, 0, SampleCount);
    }

    private void Start()
    {
        var format = new WaveFormat
        {
            FormatTag = 1, Channels = 1, SamplesPerSecond = Rate,
            AverageBytesPerSecond = Rate * 2, BlockAlign = 2, BitsPerSample = 16, ExtraSize = 0
        };
        // An explicit device id is used. Neither WAVE_MAPPER nor a system default
        // is permitted. The driver may convert its hardware format to this PCM.
        Check(WaveInOpen(out _handle, DeviceId, ref format,
            _dataReady.SafeWaitHandle.DangerousGetHandle(), UIntPtr.Zero, CallbackEvent), "waveInOpen (48 kHz / 16-bit / mono)");
        Check(WaveInGetDevCaps((UIntPtr)_handle, out var openedCaps, (uint)Marshal.SizeOf<WaveInCaps>()), "waveInGetDevCaps (opened input)");
        if (!IsYetiXName(openedCaps.Name) || !string.Equals(openedCaps.Name, DeviceName, StringComparison.Ordinal))
            throw new IOException("Capture device identity changed while opening; recording was not started.");
        for (var i = 0; i < BufferCount; i++)
        {
            var buffer = new NativeBuffer(BufferFrames * 2);
            _buffers.Add(buffer);
            Check(WaveInPrepareHeader(_handle, buffer.Header, (uint)HeaderSize), "waveInPrepareHeader");
            buffer.Prepared = true;
            Check(WaveInAddBuffer(_handle, buffer.Header, (uint)HeaderSize), "waveInAddBuffer");
            buffer.Queued = true;
        }
        StartedAtUtc = DateTimeOffset.UtcNow;
        StartedTimestamp = Stopwatch.GetTimestamp();
        _lastDataTimestamp = StartedTimestamp;
        Check(WaveInStart(_handle), "waveInStart");
        StartCompletedTimestamp = Stopwatch.GetTimestamp();
        Volatile.Write(ref _running, 1);
        _pump = new Thread(Pump) { IsBackground = true, Name = "Yeti X PCM capture" };
        _pump.Start();
    }

    private void Pump()
    {
        try
        {
            while (Volatile.Read(ref _stopRequested) == 0 && SampleCount < _samples.Length)
            {
                _dataReady.WaitOne(100);
                if (Volatile.Read(ref _stopRequested) != 0) break;
                // Every queued block returned at once means no buffer remained
                // available for capture. Treat possible dropped audio as invalid.
                if (_buffers.All(b => b.Queued && (ReadFlags(b) & HeaderDone) != 0))
                    throw new IOException("All input buffers completed before recycling; continuous capture is no longer assured.");
                ConsumeReady(requeue: true);
                if (Stopwatch.GetElapsedTime(Volatile.Read(ref _lastDataTimestamp)).TotalSeconds > 2)
                    throw new IOException("Microphone PCM input stalled for over two seconds.");
            }
        }
        catch (Exception ex) { Fail(ex.Message); }
        finally
        {
            ReleaseNative();
            try { SaveWave(); }
            catch (Exception ex) { Fail("WAV save failed: " + ex.Message); }
            finally { _wave.Dispose(); Volatile.Write(ref _running, 0); }
        }
    }

    private void ConsumeReady(bool requeue)
    {
        for (var count = 0; count < _buffers.Count; count++)
        {
            var buffer = _buffers[_nextBuffer];
            // Read DONE before the other driver-written fields. A whole-struct
            // copy could otherwise observe old BytesRecorded and new DONE if
            // the driver completes the buffer during that copy.
            if (!buffer.Queued || (ReadFlags(buffer) & HeaderDone) == 0) break;
            var header = ReadHeader(buffer);
            if (header.BytesRecorded > buffer.Bytes || (header.BytesRecorded & 1) != 0)
                throw new IOException("Microphone driver returned an invalid PCM block length.");
            buffer.Queued = false;
            var frames = checked((int)header.BytesRecorded / 2);
            if (frames > 0)
            {
                lock (_samplesLock)
                {
                    var accepted = Math.Min(frames, _samples.Length - _sampleCount);
                    Marshal.Copy(buffer.Data, _samples, _sampleCount, accepted);
                    _sampleCount += accepted;
                }
                Volatile.Write(ref _lastDataTimestamp, Stopwatch.GetTimestamp());
            }
            _nextBuffer = (_nextBuffer + 1) % _buffers.Count;
            if (requeue && Volatile.Read(ref _stopRequested) == 0 && SampleCount < _samples.Length)
            {
                if (_buffers.All(b => !b.Queued || (ReadFlags(b) & HeaderDone) != 0))
                    throw new IOException("Input queue emptied while consuming PCM; capture may contain a discontinuity.");
                // Preserve flags controlled by winmm. waveInAddBuffer clears DONE.
                Check(WaveInAddBuffer(_handle, buffer.Header, (uint)HeaderSize), "waveInAddBuffer (recycle)");
                buffer.Queued = true;
            }
        }
    }

    public void Stop()
    {
        lock (_stopLock)
        {
            if (_disposed) return;
            Volatile.Write(ref _stopRequested, 1);
            _dataReady.Set();
            if (_pump is not null && !_pump.Join(TimeSpan.FromSeconds(5)))
            {
                Fail("Microphone capture thread did not stop within five seconds; native resources remain owned by that thread.");
                throw new TimeoutException(Error);
            }
        }
    }

    public void Dispose()
    {
        lock (_stopLock)
        {
            if (_disposed) return;
            Stop();
            // The event is a native callback target until successful waveInClose.
            if (_handle == IntPtr.Zero) _dataReady.Dispose();
            _disposed = true;
        }
    }

    private void ReleaseNative()
    {
        if (_handle == IntPtr.Zero) { FreeBuffers(); return; }
        var reset = WaveInReset(_handle);
        if (reset != 0) Fail($"waveInReset failed ({reset}); capture cleanup incomplete.");
        else
        {
            try { ConsumeReady(requeue: false); }
            catch (Exception ex) { Fail("Final PCM drain failed: " + ex.Message); }
        }
        var until = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2;
        foreach (var buffer in _buffers.Where(b => b.Prepared))
        {
            uint result;
            do
            {
                result = WaveInUnprepareHeader(_handle, buffer.Header, (uint)HeaderSize);
                if (result == StillPlaying) Thread.Sleep(10);
            } while (result == StillPlaying && Stopwatch.GetTimestamp() < until);
            if (result == 0) buffer.Prepared = false;
            else Fail($"waveInUnprepareHeader failed ({result}); native memory retained.");
        }
        var closed = WaveInClose(_handle);
        if (closed == 0)
        {
            _handle = IntPtr.Zero;
            if (_buffers.Any(b => b.Prepared)) RetainNativeOwner();
            else FreeBuffers();
        }
        else
        {
            Fail($"waveInClose failed ({closed}); native memory and event retained.");
            RetainNativeOwner();
        }
    }

    private void RetainNativeOwner()
    {
        if (_nativeMemoryRetained) return;
        _nativeMemoryRetained = true;
        lock (RetainedNativeOwners) RetainedNativeOwners.Add(this);
    }

    private void FreeBuffers()
    {
        if (_nativeMemoryRetained) return;
        foreach (var buffer in _buffers) buffer.Free();
        _buffers.Clear();
    }

    private void SaveWave()
    {
        var samples = Snapshot(0, SampleCount);
        var bytes = new byte[checked(samples.Length * 2)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        _wave.Position = 44;
        _wave.Write(bytes);
        _wave.SetLength(44L + bytes.Length);
        _wave.Position = 0;
        WriteWaveHeader(_wave, bytes.Length);
        _wave.Flush(flushToDisk: true);
    }

    private static void WriteWaveHeader(Stream stream, int dataBytes)
    {
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8); writer.Write(36 + dataBytes); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((ushort)1); writer.Write((ushort)1);
        writer.Write(Rate); writer.Write(Rate * 2); writer.Write((ushort)2); writer.Write((ushort)16);
        writer.Write("data"u8); writer.Write(dataBytes);
    }

    private void Fail(string message) => Interlocked.CompareExchange(ref _error, message, null);
    private static uint ReadFlags(NativeBuffer buffer)
    {
        var flags = unchecked((uint)Marshal.ReadInt32(buffer.Header, HeaderFlagsOffset));
        Thread.MemoryBarrier();
        return flags;
    }
    private static WaveHeader ReadHeader(NativeBuffer buffer) => Marshal.PtrToStructure<WaveHeader>(buffer.Header);
    private static void Check(uint result, string operation)
    {
        if (result == 0) return;
        var message = new StringBuilder(256);
        _ = WaveInGetErrorText(result, message, (uint)message.Capacity);
        throw new IOException($"{operation} failed ({result}): {message}");
    }

    private sealed class NativeBuffer
    {
        public IntPtr Header { get; private set; }
        public IntPtr Data { get; private set; }
        public int Bytes { get; }
        public bool Prepared { get; set; }
        public bool Queued { get; set; }
        public NativeBuffer(int bytes)
        {
            Bytes = bytes;
            try
            {
                Data = Marshal.AllocHGlobal(bytes);
                Header = Marshal.AllocHGlobal(HeaderSize);
                Marshal.StructureToPtr(new WaveHeader { Data = Data, BufferLength = (uint)bytes }, Header, false);
            }
            catch { Free(); throw; }
        }
        public void Free()
        {
            if (Header != IntPtr.Zero) Marshal.FreeHGlobal(Header);
            if (Data != IntPtr.Zero) Marshal.FreeHGlobal(Data);
            Header = Data = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public ushort FormatTag, Channels;
        public uint SamplesPerSecond, AverageBytesPerSecond;
        public ushort BlockAlign, BitsPerSample, ExtraSize;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength, BytesRecorded;
        public UIntPtr User;
        public uint Flags, Loops;
        public IntPtr Next;
        public UIntPtr Reserved;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WaveInCaps
    {
        public ushort ManufacturerId, ProductId;
        public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        public uint Formats;
        public ushort Channels, Reserved;
    }
    [DllImport("winmm.dll", EntryPoint = "waveInGetNumDevs")]
    private static extern uint WaveInGetNumDevs();
    [DllImport("winmm.dll", EntryPoint = "waveInGetDevCapsW", CharSet = CharSet.Unicode)]
    private static extern uint WaveInGetDevCaps(UIntPtr device, out WaveInCaps caps, uint size);
    [DllImport("winmm.dll", EntryPoint = "waveInOpen")]
    private static extern uint WaveInOpen(out IntPtr handle, uint device, ref WaveFormat format, IntPtr callbackEvent, UIntPtr instance, uint flags);
    [DllImport("winmm.dll", EntryPoint = "waveInPrepareHeader")]
    private static extern uint WaveInPrepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll", EntryPoint = "waveInAddBuffer")]
    private static extern uint WaveInAddBuffer(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll", EntryPoint = "waveInStart")]
    private static extern uint WaveInStart(IntPtr handle);
    [DllImport("winmm.dll", EntryPoint = "waveInReset")]
    private static extern uint WaveInReset(IntPtr handle);
    [DllImport("winmm.dll", EntryPoint = "waveInUnprepareHeader")]
    private static extern uint WaveInUnprepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll", EntryPoint = "waveInClose")]
    private static extern uint WaveInClose(IntPtr handle);
    [DllImport("winmm.dll", EntryPoint = "waveInGetErrorTextW", CharSet = CharSet.Unicode)]
    private static extern uint WaveInGetErrorText(uint result, StringBuilder text, uint chars);
}
