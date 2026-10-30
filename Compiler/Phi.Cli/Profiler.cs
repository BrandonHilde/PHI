using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Phi.Cli
{
    /// <summary>Where a piece of code came from: a PHI source line, or a routine with no line (library assembly).</summary>
    /// <param name="Routine">the method (Class.Method), class body, asm. block or library routine</param>
    /// <param name="File">the .phi file, or the library's .asm file</param>
    /// <param name="Line">the PHI source line, or 0 for code that has none</param>
    public sealed record CodePlace(string Routine, string File, int Line, string Text);

    /// <summary>
    /// Maps addresses in a unit (a kernel, or a 16-bit program's OS classes or boot sector) to
    /// the PHI line or library routine that produced them. It reads NASM's listing of the
    /// generated .asm, which has each instruction's offset, and the generator's comments:
    /// "; N: text" above each line's code, and "; method Class.Name (file)", "; ---- class Name
    /// (file)", "; asm.Name" and "; ---- library: file.asm" where each piece of code starts.
    /// </summary>
    public sealed class AddressMap
    {
        static readonly Regex Offset = new(@"^\s*\d+ ([0-9A-F]{8}) \S");
        static readonly Regex Org = new(@"^org (0x[0-9A-Fa-f]+|\d+)");
        static readonly Regex SourceLine = new(@"^; (\d+): (.*)$");
        static readonly Regex ClassStart = new(@"^; ---- class (\S+) \((.+)\)$");
        static readonly Regex MethodStart = new(@"^; (?:method|event|interrupt handler) (\S+) \((.+)\)$");
        static readonly Regex RawBlock = new(@"^; asm\.(\S+)$");
        static readonly Regex LibraryFile = new(@"^; ---- library: (\S+)");
        static readonly Regex GlobalLabel = new(@"^([A-Za-z_]\w*):");

        // labels the generator makes for jumps inside a method (phi_while_3, phi_inbounds_12)
        static readonly Regex JumpLabel = new(@"^phi_[a-z]+_\d+$");

        readonly List<(uint Address, CodePlace Place)> starts = new();

        public uint Start { get; private set; }
        public uint End { get; private set; }

        public static AddressMap Load(string listingPath) => Parse(File.ReadAllLines(listingPath));

        public static AddressMap Parse(IReadOnlyList<string> lines)
        {
            var map = new AddressMap();
            if (lines.Count == 0) return map;

            // the source text starts in the same column on every line: where the header comment is
            int column = Math.Max(lines[0].IndexOf(';'), 0);
            uint org = 0;
            var place = new CodePlace("(start)", "", 0, "");
            bool library = false;
            bool labelled = false;  // the comment just read named the code, so its label doesn't
            uint end = 0;

            foreach (string raw in lines)
            {
                string source = raw.Length > column ? raw[column..].TrimEnd() : "";
                string trimmed = source.Trim();
                Match at = Offset.Match(raw.Length > column ? raw[..column] : raw);

                if (trimmed == "; ---- data") break; // the variables: no more code

                Match m;
                if ((m = Org.Match(trimmed)).Success)
                    org = m.Groups[1].Value.StartsWith("0x")
                        ? uint.Parse(m.Groups[1].Value[2..], NumberStyles.HexNumber) : uint.Parse(m.Groups[1].Value);
                else if ((m = SourceLine.Match(trimmed)).Success && !library)
                    place = place with { Line = int.Parse(m.Groups[1].Value), Text = m.Groups[2].Value.Trim() };
                else if ((m = ClassStart.Match(trimmed)).Success)
                    place = new CodePlace($"{m.Groups[1].Value} (class body)", m.Groups[2].Value, 0, "");
                else if ((m = MethodStart.Match(trimmed)).Success)
                {
                    place = new CodePlace(m.Groups[1].Value, m.Groups[2].Value, 0, "");
                    labelled = true;
                }
                else if ((m = RawBlock.Match(trimmed)).Success)
                {
                    place = new CodePlace($"asm.{m.Groups[1].Value}", "", 0, "");
                    labelled = true;
                }
                else if ((m = LibraryFile.Match(trimmed)).Success)
                {
                    library = true;
                    place = new CodePlace($"({m.Groups[1].Value})", m.Groups[1].Value, 0, "");
                }
                else if ((m = GlobalLabel.Match(source)).Success)
                {
                    // a label at the start of a line begins a routine of its own, except a method's
                    // own label and the generator's jump targets
                    string label = m.Groups[1].Value;
                    if (labelled) labelled = false;
                    else if (library || !JumpLabel.IsMatch(label))
                        place = new CodePlace(label, library ? place.File : "", 0, "");
                }

                if (at.Success && trimmed.Length > 0 && !trimmed.StartsWith(';'))
                {
                    uint address = org + uint.Parse(at.Groups[1].Value, NumberStyles.HexNumber);
                    if (map.starts.Count == 0 || map.starts[^1].Place != place) map.starts.Add((address, place));
                    end = address + 1;
                }
            }

            if (map.starts.Count > 0)
            {
                map.Start = map.starts[0].Address;
                map.End = end;
            }
            return map;
        }

        /// <summary>The code at an address, or null if it isn't in this unit's code.</summary>
        public CodePlace? Find(uint address)
        {
            if (starts.Count == 0 || address < Start || address >= End) return null;
            int low = 0, high = starts.Count - 1;
            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                if (starts[mid].Address <= address) low = mid;
                else high = mid - 1;
            }
            return starts[low].Place;
        }
    }

    /// <summary>
    /// phi profile: runs a program in QEMU while reading the CPU's registers through a monitor,
    /// many times a second, then reports which PHI lines and methods the samples landed in.
    /// The program needs no changes, and runs at (nearly) its usual speed.
    /// </summary>
    public static class Profiler
    {
        /// <param name="Address">the linear address of the next instruction (cs base + eip)</param>
        /// <param name="Cpl">0 for the kernel, 3 for a user program</param>
        /// <param name="Halted">the CPU was waiting at a hlt for an interrupt</param>
        public sealed record Sample(uint Address, int Cpl, bool Halted);

        static readonly Regex Eip = new(@"EIP=([0-9a-fA-F]{8})");
        static readonly Regex Cs = new(@"CS =([0-9a-fA-F]{4}) ([0-9a-fA-F]{8})");
        static readonly Regex Cpl = new(@"CPL=(\d)");
        static readonly Regex Hlt = new(@"HLT=(\d)");

        /// <summary>A sample from the monitor's "info registers" output, or null if it isn't one.</summary>
        public static Sample? ParseRegisters(string text)
        {
            Match eip = Eip.Match(text), cs = Cs.Match(text), cpl = Cpl.Match(text), hlt = Hlt.Match(text);
            if (!eip.Success || !cs.Success) return null;
            uint address = uint.Parse(cs.Groups[2].Value, NumberStyles.HexNumber) + uint.Parse(eip.Groups[1].Value, NumberStyles.HexNumber);
            return new Sample(address, cpl.Success ? int.Parse(cpl.Groups[1].Value) : 0, hlt.Success && hlt.Groups[1].Value == "1");
        }

        /// <param name="input">an input script: run headless until it ends</param>
        /// <param name="seconds">run headless for this long (or until the program exits)</param>
        public static int Run(string phiFile, string? input, double? seconds, int top)
        {
            string file = Path.GetFullPath(phiFile);
            string outDir = Path.Combine(Path.GetDirectoryName(file)!, "build", Path.GetFileNameWithoutExtension(file));
            BuildResult build = Builder.Build(file, outDir, listing: true);
            foreach (string w in build.Warnings) Console.Error.WriteLine(w.Contains(": warning:") ? w : "warning: " + w);
            foreach (string e in build.Errors) Console.Error.WriteLine(e.Contains(": error:") ? e : "error: " + e);
            if (!build.Success) return 1;
            if (build.IsProgram)
            {
                Console.Error.WriteLine("error: a user program can't boot by itself; profile a kernel whose rootfs has it");
                return 1;
            }

            var maps = new[] { "kernel.lst", "os.lst", "boot.lst" }
                .Select(n => Path.Combine(outDir, n)).Where(File.Exists).Select(AddressMap.Load).ToList();

            int port = Qemu.FreePort();
            using var stop = new CancellationTokenSource();
            Task<List<Sample>> sampler = Task.Run(() => Collect(port, stop.Token));
            var clock = Stopwatch.StartNew();

            if (input != null || seconds != null)
            {
                string[]? lines = input != null ? File.ReadAllLines(input) : null;
                Console.WriteLine(lines != null ? "profiling until the input script ends..." : $"profiling for {seconds} s...");
                HeadlessResult run = Qemu.RunHeadless(build.ImagePath, TimeSpan.FromSeconds(seconds ?? 600),
                    lines != null ? _ => true : null, lines, screenshots: outDir, profilePort: port);
                if (run.InputError != null) Console.Error.WriteLine("warning: " + run.InputError);
            }
            else
            {
                Console.WriteLine("profiling: close the QEMU window (or let the program exit) to see where the time went");
                Qemu.RunInteractive(build.ImagePath, debug: false, profilePort: port);
            }

            stop.Cancel();
            double elapsed = clock.Elapsed.TotalSeconds;
            List<Sample> samples = sampler.Wait(TimeSpan.FromSeconds(5)) ? sampler.Result : new List<Sample>();

            string report = Report(samples, maps, top, elapsed);
            Console.Write(report);
            string saved = Path.Combine(outDir, "profile.txt");
            File.WriteAllText(saved, report);
            Console.WriteLine($"(saved to {Path.GetRelativePath(Environment.CurrentDirectory, saved)})");
            return 0;
        }

        /// <summary>Reads the registers through the monitor at port until QEMU closes it or stop is set.</summary>
        internal static List<Sample> Collect(int port, CancellationToken stop)
        {
            var samples = new List<Sample>();
            try
            {
                using TcpClient client = InputScript.Connect(port, stop);
                NetworkStream stream = client.GetStream();
                if (ReadPrompt(stream) == null) return samples; // the banner
                byte[] ask = Encoding.ASCII.GetBytes("info registers\n");
                while (!stop.IsCancellationRequested)
                {
                    stream.Write(ask);
                    string? answer = ReadPrompt(stream);
                    if (answer == null) break;
                    if (ParseRegisters(answer) is Sample s) samples.Add(s);
                }
            }
            catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException) { }
            return samples;
        }

        /// <summary>What the monitor sends up to its next "(qemu) " prompt; null when it closes.</summary>
        static string? ReadPrompt(NetworkStream stream)
        {
            var text = new StringBuilder();
            var buffer = new byte[4096];
            while (true)
            {
                int n = stream.Read(buffer, 0, buffer.Length);
                if (n <= 0) return null;
                text.Append(Encoding.ASCII.GetString(buffer, 0, n));
                if (text.ToString().TrimEnd().EndsWith("(qemu)")) return text.ToString();
            }
        }

        public static string Report(IReadOnlyList<Sample> samples, IReadOnlyList<AddressMap> maps, int top, double seconds)
        {
            var text = new StringBuilder();
            text.AppendLine();
            if (samples.Count == 0)
            {
                text.AppendLine("phi profile: no samples (QEMU stopped before the first one?)");
                return text.ToString();
            }

            int halted = samples.Count(s => s.Halted);
            int user = samples.Count(s => !s.Halted && s.Cpl == 3);
            var busy = samples.Where(s => !s.Halted && s.Cpl != 3)
                              .Select(s => (Sample: s, Place: maps.Select(m => m.Find(s.Address)).FirstOrDefault(p => p != null)))
                              .ToList();
            int elsewhere = busy.Count(b => b.Place == null);

            string Percent(int n, int of) => $"{100.0 * n / Math.Max(of, 1),6:0.0}%";

            text.AppendLine($"phi profile: {samples.Count:N0} samples in {seconds:0.0} s");
            text.AppendLine();
            text.AppendLine("Where the time went");
            text.AppendLine($"  {Percent(busy.Count, samples.Count)}  busy (the kernel, or a 16-bit program)");
            if (user > 0) text.AppendLine($"  {Percent(user, samples.Count)}  busy in user programs (not broken down further yet)");
            text.AppendLine($"  {Percent(halted, samples.Count)}  waiting for an interrupt (hlt)");

            var mapped = busy.Where(b => b.Place != null).Select(b => b.Place!).ToList();
            if (mapped.Count > 0)
                Breakdown(text, mapped, busy.Count, top, Percent);

            if (elsewhere > 0)
            {
                text.AppendLine();
                text.AppendLine($"  {Percent(elsewhere, busy.Count)} of the busy samples were outside the program's code " +
                                "(in the BIOS, or the 32-bit loader)");
            }
            return text.ToString();
        }

        static void Breakdown(StringBuilder text, List<CodePlace> mapped, int busy, int top, Func<int, int, string> Percent)
        {
            text.AppendLine();
            text.AppendLine($"Busiest lines (share of the {busy:N0} busy samples)");
            foreach (var g in mapped.GroupBy(p => (p.File, p.Line, p.Routine)).OrderByDescending(g => g.Count()).Take(top))
            {
                CodePlace p = g.First();
                string where = p.Line > 0 ? $"{p.File}:{p.Line}" : p.File.Length > 0 ? p.File : "(generated)";
                string what = p.Line > 0 ? $"{p.Routine}: {p.Text}" : p.Routine;
                text.AppendLine($"  {Percent(g.Count(), busy)}  {where,-36}  {Shorten(what, 70)}");
            }

            text.AppendLine();
            text.AppendLine("Busiest methods and routines");
            foreach (var g in mapped.GroupBy(p => (p.Routine, p.File)).OrderByDescending(g => g.Count()).Take(top))
            {
                string file = g.Key.File.Length > 0 ? $"  ({g.Key.File})" : "";
                text.AppendLine($"  {Percent(g.Count(), busy)}  {g.Key.Routine}{file}");
            }
        }

        static string Shorten(string s, int most) => s.Length <= most ? s : s[..(most - 3)] + "...";
    }
}
