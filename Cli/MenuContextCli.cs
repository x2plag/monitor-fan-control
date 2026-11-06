using System.Reflection;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace MonitorFanControl;

internal static class MenuContextCli
{
    public static Task<int> RunWorkerAsync(string nonce, int parentPid)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonitorFanControl.MenuContext", "sessions", nonce);
        Directory.CreateDirectory(directory);
        var journal = Path.Combine(directory, "worker.jsonl");
        return MenuContextWorkerProcess.RunAsync(nonce, parentPid, HidFanController.OpenMenuContextTransport,
            entry => File.AppendAllText(journal, JsonSerializer.Serialize(new { Session = nonce, Utc = DateTimeOffset.UtcNow, Entry = entry }) + Environment.NewLine));
    }
    public static async Task<int> RunAsync()
    {
        Console.WriteLine("LG 39GX950B: one menu-only listening comparison: A closed 5s, B open 5s, C closed 5s.");
        Console.WriteLine("Automatic1 is requested; no lowering/hold is sent. Ctrl+C interrupts. No sounds are played.");
        Console.WriteLine("Ready to listen now, with target noise audible? p=present/ready, a=absent, u=unclear (30s).");
        if (Console.IsInputRedirected || await ReadObservationAsync(30000, CancellationToken.None) != MenuObservation.Present)
        {
            Console.WriteLine("Precondition not met: ready listener and audible target noise required. No monitor commands sent.");
            return 2;
        }
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonitorFanControl.MenuContext", "sessions", id);
        Directory.CreateDirectory(directory);
        var journal = Path.Combine(directory, "parent.jsonl");
        var sync = new object();
        var started = new HashSet<string>();
        var clock = new MenuSystemClock();
        void Log(MenuLogEntry entry)
        {
            lock (sync) File.AppendAllText(journal, JsonSerializer.Serialize(new { Session = id, Utc = DateTimeOffset.UtcNow, Entry = entry }) + Environment.NewLine);
            if (entry.Kind == "phase-start")
            {
                started.Add(entry.Detail);
                Console.WriteLine($"Phase {entry.Detail}: listen for five seconds; requested menu {(entry.Detail == "B" ? "OPEN" : "CLOSED")}. Physical state is unconfirmed.");
            }
            if (entry.Kind == "phase-end") Console.WriteLine($"Phase {entry.Detail} ended.");
        }
        File.WriteAllText(Path.Combine(directory, "build.json"), JsonSerializer.Serialize(new
        {
            Session = id,
            Build = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            SessionDeadlineMs = 45000, WorkerLifetimeMs = MenuContextSession.WorkerLifetimeMs,
            ObservationWindowMs = 5000, PhysicalResult = "unconfirmed"
        }, new JsonSerializerOptions { WriteIndented = true }));
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        MenuSessionResult result;
        try
        {
            result = await MenuContextSession.RunAsync(token => MenuContextWorkerProcess.StartAsync(Log, token), clock,
                async token =>
                {
                    Console.WriteLine("Preparation finished: automatic1 and close requested. Is target noise STILL audible? p/a/u within5s.");
                    return await ReadObservationAsync(5000, token);
                }, Log, stop.Token);
        }
        finally { Console.CancelKeyPress -= cancel; }
        Console.WriteLine(result.Detail);
        Console.WriteLine("Command submission does not confirm physical closure, fresh RPM or monitor protection. If needed, close the menu using the monitor's ordinary control.");
        if (started.Count > 0)
        {
            Console.WriteLine("Now record what you actually heard and saw. p=present, a=absent, u=unclear; no response=missing. Each answer has20s.");
            var phases = new List<MenuPhaseObservation>();
            foreach (var phase in new[] { "A", "B", "C" })
            {
                if (!started.Contains(phase)) { phases.Add(new(MenuObservation.Missing, MenuObservation.Missing)); continue; }
                Console.WriteLine($"Phase {phase}: target NOISE p/a/u?");
                var noise = await ReadObservationAsync(20000, CancellationToken.None);
                Console.WriteLine($"Phase {phase}: MENU visible p/a/u?");
                var menu = await ReadObservationAsync(20000, CancellationToken.None);
                phases.Add(new(noise, menu));
            }
            result = result with { Observations = new(phases[0], phases[1], phases[2]) };
            Console.WriteLine("After cleanup: is MENU visible NOW? p/a/u (20s).");
            result = result with { ClosureAfterCleanup = await ReadObservationAsync(20000, CancellationToken.None) };
        }
        File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Software: {result.State}; acoustic observation: {result.AcousticResult}; observed closure: {(result.PhysicalClosureConfirmed ? "confirmed by the latest available user observation" : "unconfirmed")}.");
        Console.WriteLine($"Session saved: {directory}");
        return result.State == MenuRunState.Completed && result.Cleanup?.CommandsSubmitted == true ? 0 : 1;
    }

    private static async Task<MenuObservation> ReadObservationAsync(int timeoutMs, CancellationToken token)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < timeoutMs)
        {
            token.ThrowIfCancellationRequested();
            if (Console.KeyAvailable)
            {
                var key = char.ToLowerInvariant(Console.ReadKey(intercept: true).KeyChar);
                var observation = key switch { 'p' => MenuObservation.Present, 'a' => MenuObservation.Absent, 'u' => MenuObservation.Unclear, _ => MenuObservation.Missing };
                if (observation != MenuObservation.Missing) { Console.WriteLine(observation); return observation; }
            }
            await Task.Delay(50, token);
        }
        Console.WriteLine("Missing");
        return MenuObservation.Missing;
    }
}
