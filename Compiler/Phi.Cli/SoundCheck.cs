using System.Globalization;

namespace Phi.Cli
{
    /// <summary>
    /// Checks what a test's sound card played, from QEMU's recording of it (a 16-bit WAV file),
    /// against NAME.sound. Each line of NAME.sound is one check:
    ///
    ///   sound at least 0.8      at least this many seconds weren't silent
    ///   sound at most 1.2       and no more than this many
    ///   tones 440 880 440       the steady pitches heard, in order, each within 3%
    ///                           (a pitch heard again straight after itself counts once)
    ///
    /// Blank lines and lines starting with # are skipped.
    /// </summary>
    public static class SoundCheck
    {
        const double WindowSeconds = 0.02;
        const double SilenceRms = 300;        // out of 32767
        const double Tolerance = 0.03;
        const int MinToneWindows = 3;         // 60 ms

        public sealed record Tone(double Hz, double Seconds);

        public sealed class Recording
        {
            public double SoundSeconds { get; init; }
            public List<Tone> Tones { get; init; } = new();

            public string Describe() =>
                $"heard {SoundSeconds:0.00} s of sound; tones: " +
                (Tones.Count == 0 ? "none" : string.Join(", ", Tones.Select(t => $"{t.Hz:0.0} Hz ({t.Seconds:0.00} s)")));
        }

        /// <returns>null if every check passes, otherwise what went wrong</returns>
        public static string? Check(string wavPath, IEnumerable<string> lines)
        {
            if (!File.Exists(wavPath)) return "QEMU didn't record any sound";

            Recording heard;
            try { heard = Analyze(File.ReadAllBytes(wavPath)); }
            catch (InvalidDataException e) { return "the recording can't be read: " + e.Message; }

            var failures = new List<string>();
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                if (words.Length == 4 && words[0] == "sound" && words[1] == "at" && words[2] is "least" or "most")
                {
                    double limit = double.Parse(words[3], CultureInfo.InvariantCulture);
                    bool ok = words[2] == "least" ? heard.SoundSeconds >= limit : heard.SoundSeconds <= limit;
                    if (!ok) failures.Add($"expected sound at {words[2]} {limit} s");
                }
                else if (words.Length >= 2 && words[0] == "tones")
                {
                    var expected = words.Skip(1).Select(w => double.Parse(w, CultureInfo.InvariantCulture)).ToList();
                    bool ok = expected.Count == heard.Tones.Count &&
                              expected.Zip(heard.Tones).All(p => Math.Abs(p.Second.Hz - p.First) <= p.First * Tolerance);
                    if (!ok) failures.Add($"expected the tones {string.Join(" ", expected)}");
                }
                else
                {
                    failures.Add($"unknown check in the .sound file: {line}");
                }
            }

            return failures.Count == 0 ? null : string.Join("\n", failures) + "\n" + heard.Describe();
        }

        /// <summary>How long the recording sounds, and its steady pitches.</summary>
        public static Recording Analyze(byte[] wav)
        {
            var (samples, rate) = ReadMono(wav);
            int window = Math.Max(1, (int)(rate * WindowSeconds));
            int windows = samples.Length / window;

            int sounding = 0;
            var pitches = new double?[windows];
            for (int w = 0; w < windows; w++)
            {
                double sum = 0;
                for (int i = w * window; i < (w + 1) * window; i++) sum += samples[i] * (double)samples[i];
                if (Math.Sqrt(sum / window) < SilenceRms) continue;
                sounding++;

                // the pitch over two windows, so low notes still have a few cycles
                int start = w * window;
                int end = Math.Min(samples.Length, start + 2 * window);
                pitches[w] = Pitch(samples, start, end, rate);
            }

            // runs of windows with the same pitch become tones
            var tones = new List<Tone>();
            int runStart = 0;
            while (runStart < windows)
            {
                if (pitches[runStart] is not double first) { runStart++; continue; }
                int runEnd = runStart + 1;
                while (runEnd < windows && pitches[runEnd] is double p && Math.Abs(p - first) <= first * Tolerance) runEnd++;

                if (runEnd - runStart >= MinToneWindows)
                {
                    var run = pitches[runStart..runEnd].Select(p => p!.Value).OrderBy(p => p).ToList();
                    double hz = run[run.Count / 2];
                    double seconds = (runEnd - runStart) * WindowSeconds;
                    if (tones.Count > 0 && Math.Abs(tones[^1].Hz - hz) <= hz * Tolerance)
                        tones[^1] = tones[^1] with { Seconds = tones[^1].Seconds + seconds };
                    else
                        tones.Add(new Tone(hz, seconds));
                }
                runStart = runEnd;
            }

            return new Recording { SoundSeconds = sounding * WindowSeconds, Tones = tones };
        }

        /// <summary>
        /// The frequency from where the signal rises through zero (interpolated between samples,
        /// with a little hysteresis); null if there are too few crossings to tell.
        /// </summary>
        static double? Pitch(short[] samples, int start, int end, int rate)
        {
            const int Hysteresis = 1000;
            var crossings = new List<double>();
            bool armed = false;
            for (int i = start + 1; i < end; i++)
            {
                if (samples[i] < -Hysteresis) armed = true;
                if (armed && samples[i - 1] < 0 && samples[i] >= 0)
                {
                    double fraction = -samples[i - 1] / (double)(samples[i] - samples[i - 1]);
                    crossings.Add(i - 1 + fraction);
                    armed = false;
                }
            }
            if (crossings.Count < 3) return null;
            return (crossings.Count - 1) / (crossings[^1] - crossings[0]) * rate;
        }

        /// <summary>
        /// The samples of a 16-bit WAV file, left and right averaged. QEMU fills in the header's
        /// sizes only when it closes the file, so the data runs to the end of the file whatever
        /// the header says.
        /// </summary>
        static (short[] Samples, int Rate) ReadMono(byte[] wav)
        {
            if (wav.Length < 12 || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F')
                throw new InvalidDataException("not a WAV file");

            int channels = 0, rate = 0, bits = 0, at = 12;
            while (at + 8 <= wav.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(wav, at, 4);
                int size = BitConverter.ToInt32(wav, at + 4);
                if (id == "fmt " && at + 24 <= wav.Length)
                {
                    channels = BitConverter.ToInt16(wav, at + 10);
                    rate = BitConverter.ToInt32(wav, at + 12);
                    bits = BitConverter.ToInt16(wav, at + 22);
                }
                else if (id == "data")
                {
                    if (bits != 16 || channels < 1) throw new InvalidDataException($"{bits}-bit, {channels} channels (expected 16-bit)");
                    int frames = (wav.Length - at - 8) / (2 * channels);
                    var samples = new short[frames];
                    for (int f = 0; f < frames; f++)
                    {
                        int sum = 0;
                        for (int c = 0; c < channels; c++) sum += BitConverter.ToInt16(wav, at + 8 + (f * channels + c) * 2);
                        samples[f] = (short)(sum / channels);
                    }
                    return (samples, rate);
                }
                if (size < 0) break;
                at += 8 + size + (size & 1);
            }
            throw new InvalidDataException("no data chunk");
        }
    }
}
