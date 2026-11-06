using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitorFanControl;

internal static class MenuAudioCli
{
    public static async Task<int> RunAsync()
    {
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MonitorFanControl.MenuAudio", "sessions", id);
        Directory.CreateDirectory(directory);
        var options = new JsonSerializerOptions { WriteIndented = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
        options.Converters.Add(new JsonStringEnumConverter());
        var build = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        File.WriteAllText(Path.Combine(directory, "build.json"), JsonSerializer.Serialize(new
        {
            Session = id, Build = build, Observer = "microphone-numeric-comparability",
            UserAuthorizedAutonomousMicrophone = true, PhaseWindowMs = 5000,
            ReferencePolicy = "FrozenStrongTonalReference", FixedCaptureSettlingMs = 10000,
            PhysicalOsdVisibility = "unconfirmed", PermanentQuiet = "unconfirmed"
        }, options));
        var journal = Path.Combine(directory, "parent.jsonl");
        var sync = new object();
        void Log(MenuLogEntry entry)
        {
            lock (sync) File.AppendAllText(journal, JsonSerializer.Serialize(new
            {
                Session = id, Actor = "parent", Observer = "microphone-numeric-comparability",
                Utc = DateTimeOffset.UtcNow, Qpc = Stopwatch.GetTimestamp(), QpcFrequency = Stopwatch.Frequency, Entry = entry
            }) + Environment.NewLine);
            if (entry.Kind is "phase-start" or "phase-end" or "microphone-readiness" or "result")
                Console.WriteLine($"{entry.Kind}: {entry.Detail}");
        }
        Console.WriteLine("One microphone-observed menu-only A/B/C cycle; no playback or lowering. Ctrl+C interrupts.");
        Console.WriteLine("Fixed10s settling, initial5s and prepared1s frozen tonal comparison precede opening. Broadband may be unsteady; physical OSD is unconfirmed.");
        Console.WriteLine("Session directory: " + directory);
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        MenuAudioCapture? capture = null;
        var exitCode = 1;
        try
        {
            capture = MenuAudioCapture.StartYeti(Path.Combine(directory, "capture.wav"), maxSeconds: 120);
            var clock = new MenuSystemClock();
            File.WriteAllText(Path.Combine(directory, "clock-anchor.json"), JsonSerializer.Serialize(new
            {
                Qpc = Stopwatch.GetTimestamp(), QpcFrequency = Stopwatch.Frequency, SessionClockMs = clock.Milliseconds,
                capture.StartedTimestamp, capture.StartCompletedTimestamp, capture.SampleRate,
                Alignment = "frame=(QPC-StartedTimestamp)*SampleRate/QpcFrequency; native start bracket and driver latency remain uncertain"
            }, options));
            var result = await MenuAudioSession.RunAsync(token => MenuContextWorkerProcess.StartAsync(Log, token),
                new Source(capture), clock, Log, stop.Token,
                referencePolicy: MenuAudioReferencePolicy.FrozenStrongTonalReference, preCaptureSettleMs: 10000);
            File.WriteAllText(Path.Combine(directory, "audio-session.json"), JsonSerializer.Serialize(result, options));
            capture.Stop();
            WriteCapture();
            Console.WriteLine($"Numeric readiness: {result.Readiness.Ready}; {result.Readiness.Reason}");
            Console.WriteLine($"Software: {result.Session?.State}; captured continuity: {result.CaptureHealthy && capture.Healthy}.");
            Console.WriteLine("Microphone data are separate from human observations; OSD closure and permanent quiet remain unconfirmed.");
            Console.WriteLine("Session saved: " + directory);
            exitCode = result.Session?.State == MenuRunState.Completed && result.Session.Cleanup?.CommandsSubmitted == true &&
                result.CaptureHealthy && capture.Healthy && result.Phases.Count == 3 && result.Phases.All(phase => phase.Valid) ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log(new(0, "audio-cli-error", ex.GetType().Name, "audio"));
            File.WriteAllText(Path.Combine(directory, "audio-cli-error.json"), JsonSerializer.Serialize(new
            {
                Error = ex.GetType().Name, PhysicalClosure = "unconfirmed", AcousticOutcome = "inconclusive",
                Detail = "Microphone/session failed; inspect existing independent cleanup journal. Close OSD with ordinary monitor control if needed."
            }, options));
            Console.Error.WriteLine("Microphone/session failed; physical closure unconfirmed. Session: " + directory);
            exitCode = 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            if (capture is not null)
            {
                try { capture.Dispose(); }
                catch (Exception ex)
                {
                    exitCode = 1;
                    try { Log(new(0, "audio-dispose-error", ex.GetType().Name, "audio")); } catch (Exception) { }
                }
                try { WriteCapture(); }
                catch (Exception ex)
                {
                    exitCode = 1;
                    try { Console.Error.WriteLine("Capture metadata could not be saved: " + ex.GetType().Name); } catch (Exception) { }
                }
            }
        }
        return exitCode;
        void WriteCapture()
        {
            if (capture is null) return;
            File.WriteAllText(Path.Combine(directory, "capture.json"), JsonSerializer.Serialize(new
            {
                Session = id, capture.DeviceName, capture.SampleRate, capture.Channels, capture.BitsPerSample,
                capture.SampleCount, capture.StartedAtUtc, capture.StartedTimestamp, capture.StartCompletedTimestamp,
                capture.StopwatchFrequency, capture.Healthy, capture.Error, capture.WavePath,
                Units = "uncalibrated PCM; relative dBFS, not dB SPL", UserObservationsSupplied = false,
                AlignmentUncertainty = "waveInStart call bracket plus unknown input-driver start latency; software phase is not physical OSD evidence"
            }, options));
        }
    }

    private sealed class Source(MenuAudioCapture capture) : IMenuAudioSource
    {
        public int SampleRate => capture.SampleRate;
        public long SampleCount => capture.SampleCount;
        public long TimelineFrame => Math.Max(0,
            (long)Math.Floor((Stopwatch.GetTimestamp() - capture.StartedTimestamp) * (double)capture.SampleRate / Stopwatch.Frequency));
        public bool Healthy => capture.Healthy && capture.IsRunning;
        public short[] Snapshot(long startFrame, long endFrame) => capture.Snapshot(startFrame, endFrame);
    }
}
