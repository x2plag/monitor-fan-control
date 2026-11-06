using System.Numerics;

namespace MonitorFanControl;

// One numeric engine for the unchanged menu-audio path and the short-session
// adapter. These engineering checks do not identify a physical sound source.
internal static class MenuAudioAnalysis
{
    internal const int FftSize = 8192;
    internal sealed record Anchor(string Name, long StartMs, long StartFrame, long? EndMs = null, long? EndFrame = null, bool Complete = true);
    internal sealed record FrozenTone(int Bin, double ProminenceDb, int RecurringSeconds, double[] InitialLevels);
    internal static MenuAudioReadiness Refused(string reason) =>
        new(false, reason, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, []);

    internal static IReadOnlyList<FrozenTone> FreezeStrongTones(MenuAudioWindow initial, IMenuAudioSource source)
    {
        if (initial.OneSecondDbfs.Count != 5) return [];
        var samples = source.Snapshot(initial.StartFrame, initial.EndFrame);
        var seconds = Enumerable.Range(0, 5).Select(i => Welch(samples.AsSpan(i * source.SampleRate, source.SampleRate), source.SampleRate)).ToArray();
        var whole = Welch(samples, source.SampleRate);
        var low = (int)Math.Ceiling(40.0 * FftSize / source.SampleRate);
        var high = (int)Math.Floor(20000.0 * FftSize / source.SampleRate);
        var candidates = new List<FrozenTone>();
        for (var bin = low; bin <= high; bin++)
        {
            if (!IsPeak(whole, bin)) continue;
            var prominence = Prominence(whole, bin, source.SampleRate);
            if (!double.IsFinite(prominence) || prominence < 12) continue;
            var recurring = seconds.Count(psd => Enumerable.Range(bin - 3, 7)
                .Any(i => IsPeak(psd, i) && Prominence(psd, i, source.SampleRate) >= 12));
            if (recurring != 5) continue;
            var levels = seconds.Select(psd => BandDb(psd, bin - 3, bin + 3, source.SampleRate)).ToArray();
            var median = Median(levels);
            var mad = Median(levels.Select(x => Math.Abs(x - median)).ToArray());
            if (!double.IsFinite(median) || Span(levels) > 2 || Math.Max(1, 3 * 1.4826 * mad) > 2) continue;
            candidates.Add(new(bin, prominence, recurring, levels));
        }
        var selected = new List<FrozenTone>();
        foreach (var candidate in candidates.OrderByDescending(c => c.ProminenceDb).ThenBy(c => c.Bin))
        {
            if (selected.Any(c => Math.Abs(c.Bin - candidate.Bin) <= 6)) continue;
            selected.Add(candidate);
            if (selected.Count == 5) break;
        }
        return selected;
    }

    internal static MenuAudioReadiness CompareTone(MenuAudioWindow initial, MenuAudioWindow? prepared,
        IMenuAudioSource source, IReadOnlyList<FrozenTone> frozen)
    {
        var median = Median(initial.OneSecondDbfs);
        var mad = Median(initial.OneSecondDbfs.Select(x => Math.Abs(x - median)).ToArray());
        var initialSpan = Span(initial.OneSecondDbfs);
        var tolerance = Math.Max(1, 3 * 1.4826 * mad);
        var preparedSpan = prepared is null ? double.NaN : Span(prepared.QuarterSecondDbfs);
        var delta = prepared is null ? double.NaN : prepared.AcRmsDbfs - median;
        var overallStable = initialSpan <= 2 && tolerance <= 2 && prepared is not null && preparedSpan <= 3 && Math.Abs(delta) <= tolerance;
        var selected = new List<MenuAudioBandComparison>();
        double[]? preparedSpectrum = null;
        double[][]? quarters = null;
        if (prepared is { Valid: true } && prepared.QuarterSecondDbfs.Count == 4)
        {
            var samples = source.Snapshot(prepared.StartFrame, prepared.EndFrame);
            preparedSpectrum = Welch(samples, source.SampleRate);
            quarters = Enumerable.Range(0, 4).Select(i => Welch(samples.AsSpan(i * (source.SampleRate / 4), source.SampleRate / 4), source.SampleRate)).ToArray();
        }
        foreach (var tone in frozen)
        {
            var center = Median(tone.InitialLevels);
            var spread = Median(tone.InitialLevels.Select(x => Math.Abs(x - center)).ToArray());
            var allowed = Math.Max(1, 3 * 1.4826 * spread);
            var measured = preparedSpectrum is null ? double.NaN : BandDb(preparedSpectrum, tone.Bin - 3, tone.Bin + 3, source.SampleRate);
            var quarterSpan = quarters is null ? double.NaN : Span(quarters.Select(psd => BandDb(psd, tone.Bin - 3, tone.Bin + 3, source.SampleRate)).ToArray());
            var change = measured - center;
            selected.Add(new($"initial-tone-{tone.Bin * source.SampleRate / (double)FftSize:F2}Hz",
                (tone.Bin - 3) * source.SampleRate / (double)FftSize, (tone.Bin + 3) * source.SampleRate / (double)FftSize,
                center, spread, Span(tone.InitialLevels), measured, quarterSpan, change, allowed,
                prepared is { Valid: true } && double.IsFinite(measured) && quarterSpan <= 3 && Math.Abs(change) <= allowed)
                { InitialProminenceDb = tone.ProminenceDb, InitialRecurringSeconds = tone.RecurringSeconds, Primary = selected.Count == 0 });
        }
        var primary = selected[0];
        var reason = prepared is null ? "Strong tonal reference frozen before worker/open; prepared comparison pending." :
            !prepared.Valid ? "Invalid prepared microphone recording: " + prepared.Reason :
            !double.IsFinite(primary.PreparedDbfs) || !double.IsFinite(primary.PreparedSpanDb) || primary.PreparedSpanDb > 3
                ? "Inconclusive prepared primary tonal reference: unstable or missing power; menu comparison not started." :
            !primary.Comparable ? "Preparation changed captured primary tonal reference; menu comparison not started." :
            "Comparable frozen primary tonal component only; overall noise, target-noise presence and source identity remain inconclusive.";
        return new(primary.Comparable, reason, median, mad, initialSpan, preparedSpan, delta, tolerance, selected)
        {
            ReferencePolicy = MenuAudioReferencePolicy.FrozenStrongTonalReference,
            ReferenceBasis = "highest-initial-prominence qualifying frozen tonal band; >=12dB prominence; recurrence=5/5; initial range<=2dB; tolerance=max(1,3*1.4826*MAD)<=2dB; prepared range<=3dB",
            SelectedBands = selected,
            BroadbandAssessment = overallStable ? "descriptive only; broadband comparable by engineering checks, overall/target interpretation inconclusive"
                : "UNSTEADY or changed broadband; overall-noise interpretation inconclusive; primary tonal reference only"
        };
    }

    internal static MenuAudioWindow Measure(Anchor anchor, IMenuAudioSource source)
    {
        var endFrame = anchor.EndFrame ?? source.TimelineFrame;
        short[] samples;
        try { samples = source.Snapshot(anchor.StartFrame, endFrame); }
        catch { samples = []; }
        return Measure(anchor with { EndFrame = endFrame, Complete = anchor.Complete && anchor.EndFrame is not null }, source, samples);
    }

    // A retained short-session window uses the exact same immutable PCM for its
    // sidecar and numeric measurement. The legacy caller keeps its existing rules.
    internal static MenuAudioWindow Measure(Anchor anchor, IMenuAudioSource source, short[] samples)
    {
        var endFrame = anchor.EndFrame ?? source.TimelineFrame;
        var endMs = anchor.EndMs ?? anchor.StartMs;
        var expected = Math.Max(0, endFrame - anchor.StartFrame);
        var statistics = Stats(samples);
        var seconds = Levels(samples, source.SampleRate);
        var quarters = Levels(samples, source.SampleRate / 4);
        var nearRailLimit = samples.Length * 0.0001;
        var reason = !source.Healthy ? "capture failure or discontinuity" : !anchor.Complete || anchor.EndFrame is null ? "incomplete phase" :
            expected <= 0 || samples.LongLength != expected ? "insufficient or mismatched delivered frames" :
            !double.IsFinite(statistics.Ac) ? "zero AC signal" :
            statistics.RailRun >= 3 || statistics.Rails >= nearRailLimit ? "clipping threshold exceeded" :
            statistics.ZeroRun >= source.SampleRate / 100 || statistics.ConstantRun >= source.SampleRate / 100 ? "possible dropout or constant signal" : "valid";
        return new(anchor.Name, anchor.StartMs, endMs, anchor.StartFrame, endFrame, expected,
            samples.Length, reason == "valid", reason, statistics.Ac, statistics.Raw, statistics.Dc,
            statistics.Peak, statistics.Rails, statistics.RailRun, statistics.ZeroRun, statistics.ConstantRun, seconds, quarters);
    }

    internal static MenuAudioReadiness Compare(MenuAudioWindow initial, MenuAudioWindow prepared, IMenuAudioSource source, IReadOnlyList<(string Name, int Low, int High)>? frozenBands = null)
    {
        var baseline = initial.OneSecondDbfs;
        var median = Median(baseline);
        var mad = Median(baseline.Select(x => Math.Abs(x - median)).ToArray());
        var span = Span(baseline);
        var preparedSpan = Span(prepared.QuarterSecondDbfs);
        var tolerance = Math.Max(1, 3 * 1.4826 * mad);
        var delta = prepared.AcRmsDbfs - median;
        var bands = new List<MenuAudioBandComparison>();
        var reason = !initial.Valid || !prepared.Valid ? "Invalid microphone recording: " + (!initial.Valid ? initial.Reason : prepared.Reason) :
            baseline.Count != 5 || prepared.QuarterSecondDbfs.Count != 4 ? "Inconclusive prepared baseline: insufficient complete subwindows." :
            span > 2 || preparedSpan > 3 || tolerance > 2 ? "Inconclusive prepared baseline: unstable broadband levels." :
            Math.Abs(delta) > tolerance ? "Preparation changed the captured broadband signal; menu comparison not started." : "";
        if (reason.Length == 0)
        {
            var samples = source.Snapshot(initial.StartFrame, initial.EndFrame);
            var preparedSamples = source.Snapshot(prepared.StartFrame, prepared.EndFrame);
            var baselineSpectra = Enumerable.Range(0, 5).Select(i => Welch(samples.AsSpan(i * source.SampleRate, source.SampleRate), source.SampleRate)).ToArray();
            var initialSpectrum = Welch(samples, source.SampleRate);
            var preparedSpectrum = Welch(preparedSamples, source.SampleRate);
            var preparedQuarters = Enumerable.Range(0, 4).Select(i => Welch(preparedSamples.AsSpan(i * (source.SampleRate / 4), source.SampleRate / 4), source.SampleRate)).ToArray();
            var bandEdges = frozenBands ?? StrictBands(initialSpectrum, baselineSpectra, source.SampleRate);
            foreach (var band in bandEdges)
            {
                var levels = baselineSpectra.Select(psd => BandDb(psd, band.Low, band.High, source.SampleRate)).ToArray();
                var center = Median(levels);
                var spread = Median(levels.Select(x => Math.Abs(x - center)).ToArray());
                var allowed = Math.Max(1, 3 * 1.4826 * spread);
                var measured = BandDb(preparedSpectrum, band.Low, band.High, source.SampleRate);
                var quarterSpan = Span(preparedQuarters.Select(psd => BandDb(psd, band.Low, band.High, source.SampleRate)).ToArray());
                var change = measured - center;
                var comparable = double.IsFinite(center) && double.IsFinite(measured) && Span(levels) <= 2 && quarterSpan <= 3 && allowed <= 2 && Math.Abs(change) <= allowed;
                bands.Add(new(band.Name, band.Low * source.SampleRate / (double)FftSize, band.High * source.SampleRate / (double)FftSize,
                    center, spread, Span(levels), measured, quarterSpan, change, allowed, comparable));
                if (!comparable && reason.Length == 0)
                    reason = !double.IsFinite(center) || !double.IsFinite(measured) || Span(levels) > 2 || quarterSpan > 3 || allowed > 2
                        ? $"Inconclusive prepared baseline: unstable or missing {band.Name} power."
                        : $"Preparation changed captured {band.Name} power; menu comparison not started.";
            }
        }
        if (reason.Length == 0) reason = bands.Count > 1
            ? "Comparable broadband and frozen initial spectral bands; target-noise presence unconfirmed."
            : "Comparable broadband and 200-20000Hz power; no stable initial tone; target-noise presence unconfirmed.";
        return new(initial.Valid && prepared.Valid && reason.StartsWith("Comparable", StringComparison.Ordinal), reason,
            median, mad, span, preparedSpan, delta, tolerance, bands);
    }

    internal static IReadOnlyList<(string Name, int Low, int High)> StrictBands(double[] spectrum, double[][] seconds, int rate)
    {
        var edges = new List<(string Name, int Low, int High)> { ("200-20000Hz", (int)Math.Ceiling(200.0 * FftSize / rate), (int)Math.Floor(20000.0 * FftSize / rate)) };
        edges.AddRange(TonalBins(spectrum, seconds, rate)
            .Select(bin => ($"initial-tone-{bin * rate / (double)FftSize:F2}Hz", bin - 3, bin + 3)));
        return edges;
    }

    // Immutable initial PCM and band selection, made before the worker factory.
    internal sealed class FrozenReference : IMenuAudioSource
    {
        private readonly IMenuAudioSource _source;
        private readonly MenuAudioWindow _initial;
        private readonly short[] _pcm;
        private readonly IReadOnlyList<FrozenTone> _tones;
        private readonly IReadOnlyList<(string Name, int Low, int High)> _strictBands;
        private readonly MenuAudioReferencePolicy _policy;
        public FrozenReference(MenuAudioWindow initial, IMenuAudioSource source, MenuAudioReferencePolicy policy)
        {
            _initial = initial; _source = source; _policy = policy;
            _pcm = source.Snapshot(initial.StartFrame, initial.EndFrame);
            _tones = policy == MenuAudioReferencePolicy.FrozenStrongTonalReference ? FreezeStrongTones(initial, this) : [];
            _strictBands = policy == MenuAudioReferencePolicy.StrictBroadbandAndTones
                ? StrictBands(Welch(_pcm, SampleRate), Enumerable.Range(0, 5)
                    .Select(i => Welch(_pcm.AsSpan(i * SampleRate, SampleRate), SampleRate)).ToArray(), SampleRate) : [];
            if (policy == MenuAudioReferencePolicy.FrozenStrongTonalReference && _tones.Count == 0)
                throw new System.IO.InvalidDataException("No strong recurring stable initial tonal reference; worker not started.");
        }
        public int SampleRate => _source.SampleRate;
        public long SampleCount => _source.SampleCount;
        public long TimelineFrame => _source.TimelineFrame;
        public bool Healthy => _source.Healthy;
        public short[] Snapshot(long startFrame, long endFrame) =>
            startFrame >= _initial.StartFrame && endFrame <= _initial.EndFrame
                ? _pcm.AsSpan(checked((int)(startFrame - _initial.StartFrame)), checked((int)(endFrame - startFrame))).ToArray()
                : _source.Snapshot(startFrame, endFrame);
        public MenuAudioReadiness Compare(MenuAudioWindow measured) => (_policy == MenuAudioReferencePolicy.StrictBroadbandAndTones
            ? MenuAudioAnalysis.Compare(_initial, measured, this, _strictBands)
            : CompareTone(_initial, measured, this, _tones)) with { ReferencePolicy = _policy };
    }

    internal static (double Ac, double Raw, double Dc, double Peak, int Rails, int RailRun, int ZeroRun, int ConstantRun) Stats(ReadOnlySpan<short> samples)
    {
        if (samples.Length == 0) return (double.NegativeInfinity, double.NegativeInfinity, 0, 0, 0, 0, 0, 0);
        double sum = 0, squares = 0;
        var peak = 0;
        var rails = 0; var rail = 0; var railMax = 0;
        var zero = 0; var zeroMax = 0; var constant = 0; var constantMax = 0;
        var previous = int.MaxValue;
        foreach (var sample in samples)
        {
            var value = (int)sample;
            sum += value;
            squares += (double)value * value;
            peak = Math.Max(peak, Math.Abs(value));
            if (Math.Abs(value) >= 32760) { rails++; rail++; railMax = Math.Max(railMax, rail); } else rail = 0;
            zero = value == 0 ? zero + 1 : 0; zeroMax = Math.Max(zeroMax, zero);
            constant = value == previous ? constant + 1 : 1; constantMax = Math.Max(constantMax, constant); previous = value;
        }
        var mean = sum / samples.Length;
        // A second pass avoids cancellation of nearly constant PCM when removing DC.
        double acSquares = 0;
        foreach (var sample in samples) acSquares += (sample - mean) * (sample - mean);
        return (Db(acSquares / samples.Length / (32768.0 * 32768)), Db(squares / samples.Length / (32768.0 * 32768)),
            mean / 32768, peak / 32768.0, rails, railMax, zeroMax, constantMax);
    }
    internal static double[] Levels(short[] samples, int frameCount)
    {
        if (frameCount <= 0) return [];
        return Enumerable.Range(0, samples.Length / frameCount).Select(i => Stats(samples.AsSpan(i * frameCount, frameCount)).Ac).ToArray();
    }
    internal static double Db(double power) => power > 0 ? 10 * Math.Log10(power) : double.NegativeInfinity;
    internal static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return double.NaN;
        var sorted = values.Order().ToArray();
        return sorted.Length % 2 == 0 ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2 : sorted[sorted.Length / 2];
    }
    internal static double Span(IReadOnlyList<double> values) => values.Count == 0 ? double.PositiveInfinity : values.Max() - values.Min();

    // Welch PSD: periodic Hann, 8192 frames, 4096 overlap, constant detrend,
    // one sided density in normalized PCM power/Hz. Band bins include both ends.
    internal static double[] Welch(ReadOnlySpan<short> samples, int rate)
    {
        var psd = new double[FftSize / 2 + 1];
        var window = Enumerable.Range(0, FftSize).Select(i => 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / FftSize)).ToArray();
        var scale = window.Sum(x => x * x) * rate;
        var count = 0;
        for (var start = 0; start + FftSize <= samples.Length; start += FftSize / 2)
        {
            double mean = 0;
            for (var i = 0; i < FftSize; i++) mean += samples[start + i] / 32768.0;
            mean /= FftSize;
            var data = new Complex[FftSize];
            for (var i = 0; i < FftSize; i++) data[i] = new((samples[start + i] / 32768.0 - mean) * window[i], 0);
            Fft(data);
            for (var bin = 0; bin < psd.Length; bin++)
                psd[bin] += data[bin].Magnitude * data[bin].Magnitude / scale * (bin is 0 or FftSize / 2 ? 1 : 2);
            count++;
        }
        if (count > 0) for (var bin = 0; bin < psd.Length; bin++) psd[bin] /= count;
        return psd;
    }
    internal static void Fft(Complex[] data)
    {
        for (int i = 1, j = 0; i < data.Length; i++)
        {
            var bit = data.Length >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (data[i], data[j]) = (data[j], data[i]);
        }
        for (var width = 2; width <= data.Length; width <<= 1)
        {
            var step = Complex.FromPolarCoordinates(1, -2 * Math.PI / width);
            for (var start = 0; start < data.Length; start += width)
            {
                var rotation = Complex.One;
                for (var i = 0; i < width / 2; i++)
                {
                    var even = data[start + i]; var odd = rotation * data[start + i + width / 2];
                    data[start + i] = even + odd; data[start + i + width / 2] = even - odd;
                    rotation *= step;
                }
            }
        }
    }
    internal static double BandDb(double[] psd, int low, int high, int rate) =>
        Db(psd.Skip(Math.Max(0, low)).Take(Math.Min(psd.Length - 1, high) - Math.Max(0, low) + 1).Sum() * rate / FftSize);

    internal static bool IsPeak(double[] psd, int bin) => bin > 0 && bin < psd.Length - 1 && psd[bin] > psd[bin - 1] && psd[bin] >= psd[bin + 1];
    internal static double Prominence(double[] psd, int bin, int rate)
    {
        var radius = (int)Math.Ceiling(150.0 * FftSize / rate);
        var neighbors = Enumerable.Range(Math.Max(0, bin - radius), Math.Min(psd.Length - 1, bin + radius) - Math.Max(0, bin - radius) + 1)
            .Where(i => Math.Abs(i - bin) > 3).Select(i => psd[i]).ToArray();
        var background = Median(neighbors);
        return psd[bin] > 0 && background > 0 ? 10 * Math.Log10(psd[bin] / background) : double.NaN;
    }

    internal static IEnumerable<int> TonalBins(double[] whole, double[][] seconds, int rate)
    {
        var low = (int)Math.Ceiling(40.0 * FftSize / rate);
        var high = (int)Math.Floor(20000.0 * FftSize / rate);
        var radius = (int)Math.Ceiling(150.0 * FftSize / rate);
        var chosen = new List<int>();
        foreach (var bin in Enumerable.Range(low, high - low + 1).Where(i => Peak(whole, i)).OrderByDescending(i => whole[i]))
        {
            if (chosen.Any(i => Math.Abs(i - bin) <= 6) || !Strong(whole, bin)) continue;
            if (seconds.Count(psd => Enumerable.Range(Math.Max(1, bin - 3), 7).Any(i => i < psd.Length - 1 && Peak(psd, i) && Strong(psd, i))) < 4) continue;
            chosen.Add(bin);
            if (chosen.Count == 5) break;
        }
        return chosen;
        static bool Peak(double[] psd, int bin) => bin > 0 && bin < psd.Length - 1 && psd[bin] > psd[bin - 1] && psd[bin] >= psd[bin + 1];
        bool Strong(double[] psd, int bin)
        {
            var neighbors = Enumerable.Range(Math.Max(0, bin - radius), Math.Min(psd.Length - 1, bin + radius) - Math.Max(0, bin - radius) + 1)
                .Where(i => Math.Abs(i - bin) > 3).Select(i => psd[i]).ToArray();
            var background = Median(neighbors);
            return psd[bin] > 0 && background > 0 && 10 * Math.Log10(psd[bin] / background) >= 10;
        }
    }
}
