using System.Text.RegularExpressions;

namespace Phi.Cli
{
    /// <summary>
    /// Each test is tests/NAME.phi with the serial output it must produce in tests/NAME.expected.
    /// The test boots headless in QEMU; on a mismatch (or always, with keepOutput) the real output
    /// is saved to NAME.actual.
    /// NAME.input scripts keyboard and mouse input (see InputScript); NAME.errors instead lists
    /// compile errors the program must produce. A folder NAME.web/ is served over HTTP and HTTPS
    /// while the test runs (see WebServer); the program gets the addresses as TestWeb.url and
    /// TestWeb.secure_url. With NAME.sound, what the sound card plays is recorded and checked
    /// (see SoundCheck); such a test should end with exit, so QEMU finishes the recording.
    ///
    /// A test whose classes are all :Library runs twice, as a 16-bit program and as a 32-bit
    /// kernel, so both code generators are checked against the same expected output.
    ///
    /// A test named bench_NAME is a benchmark: its "bench WHAT COUNT" lines are left out when the
    /// output is compared, and with --bench only benchmarks run, each also checked against the
    /// counts in NAME.bench (see BenchCheck).
    /// </summary>
    public static class TestRunner
    {
        /// <param name="input">An input script to use instead of each test's NAME.input.</param>
        /// <param name="contains">Lines the output must contain, to check instead of each test's NAME.expected or NAME.contains.</param>
        /// <param name="bench">Run only the benchmarks, and check their counts.</param>
        public static int Run(string target, string? filter, TimeSpan timeout, string? input = null, bool keepOutput = false,
                              string? contains = null, BenchOptions? bench = null)
        {
            List<string> tests = Directory.Exists(target)
                ? Directory.GetFiles(target, "*.phi").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string> { target };

            if (filter != null)
                tests = tests.Where(t => Path.GetFileNameWithoutExtension(t).Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

            if (bench != null)
                tests = tests.Where(t => Path.GetFileName(t).StartsWith(BenchCheck.Prefix, StringComparison.OrdinalIgnoreCase)).ToList();

            // the other machines of a network test run with it, not as tests of their own
            if (Directory.Exists(target))
            {
                var listed = Directory.GetFiles(target, "*.machines")
                    .SelectMany(m => ReadMachines(m).Select(x => Path.GetFullPath(x.File)))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                tests = tests.Where(t => !listed.Contains(Path.GetFullPath(t))).ToList();
            }

            if (tests.Count == 0)
            {
                Console.WriteLine(bench != null ? $"No benchmarks ({BenchCheck.Prefix}NAME.phi) found in {target}" : $"No tests found in {target}");
                return 1;
            }

            var runs = tests.SelectMany(t => Modes(t).Select(m => (File: t, Mode: m))).ToList();

            int passed = 0;
            foreach (var (test, mode) in runs)
            {
                string name = Path.GetFileNameWithoutExtension(test) + (mode == null ? "" : $" ({mode.Name})");
                var report = new List<string>();
                string? failure = RunOne(test, timeout, mode, input, keepOutput, contains, bench, report);

                if (failure == null)
                {
                    passed++;
                    Write(ConsoleColor.Green, "PASS ");
                    Console.WriteLine(name);
                    foreach (string line in report) Console.WriteLine("     " + line);
                }
                else
                {
                    Write(ConsoleColor.Red, "FAIL ");
                    Console.WriteLine(name);
                    foreach (string line in report) Console.WriteLine("     " + line);
                    foreach (string line in failure.Split('\n')) Console.WriteLine("     " + line);
                }
            }

            Console.WriteLine();
            Write(passed == runs.Count ? ConsoleColor.Green : ConsoleColor.Red, $"{passed}/{runs.Count} passed");
            Console.WriteLine();

            return passed == runs.Count ? 0 : 1;
        }

        /// <summary>How to turn a library-only test into a whole program.</summary>
        sealed record Mode(string Name, string Wrapper);

        static readonly Mode[] BothModes =
        {
            new("16-bit", "\nphi.PhiTestBoot:Bootloader\n{\n\tcall Bootloader.JumpToSectorTwo;\n}\n"),
            new("32-bit", "\nphi.PhiTestKernel:Kernel\n{\n}\n"),
        };

        static readonly Regex LibraryClass = new(@"^\s*phi\.\w+\s*:\s*Library\b", RegexOptions.Multiline);
        static readonly Regex ProgramClass = new(@"^\s*phi\.\w+\s*:\s*(Bootloader|OS|Kernel)\b", RegexOptions.Multiline);

        /// <summary>null for an ordinary test; both modes for a test made only of :Library classes.</summary>
        static IEnumerable<Mode?> Modes(string phiFile)
        {
            string text = File.ReadAllText(phiFile);
            bool libraryOnly = LibraryClass.IsMatch(text) && !ProgramClass.IsMatch(text);
            return libraryOnly ? BothModes : new Mode?[] { null };
        }

        /// <returns>null on success, otherwise a description of the failure</returns>
        /// <param name="report">gets lines to show under the test's result (a benchmark's counts)</param>
        static string? RunOne(string phiFile, TimeSpan timeout, Mode? mode, string? inputOverride, bool keepOutput, string? containsOverride,
                              BenchOptions? bench, List<string> report)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(phiFile))!;
            string name = Path.GetFileNameWithoutExtension(phiFile);
            string variant = mode == null ? name : $"{name}.{mode.Name}";
            string expectedFile = Path.Combine(dir, name + ".expected");
            string actualFile = Path.Combine(dir, variant + ".actual");

            if (File.Exists(actualFile)) File.Delete(actualFile);

            string errorsFile = Path.Combine(dir, name + ".errors");
            string inputFile = inputOverride ?? Path.Combine(dir, name + ".input");

            // NAME.web/: files the program can fetch, at TestWeb.url ('http://10.0.2.2:PORT') and
            // TestWeb.secure_url ('https://10.0.2.2:PORT')
            string webDir = Path.Combine(dir, name + ".web");
            using WebServer? web = Directory.Exists(webDir) ? new WebServer(webDir) : null;
            using WebServer? secureWeb = web != null ? new WebServer(webDir, secure: true) : null;
            string wrapper = mode?.Wrapper ?? "";
            if (web != null)
                wrapper += $"\nphi.TestWeb:Library\n{{\n\tstr url: 'http://10.0.2.2:{web.Port}';\n" +
                           $"\tstr secure_url: 'https://10.0.2.2:{secureWeb!.Port}';\n}}\n";

            string machinesFile = Path.Combine(dir, name + ".machines");
            if (File.Exists(machinesFile))
                return RunNetwork(phiFile, machinesFile, timeout, wrapper, keepOutput);

            BuildResult build = Builder.Build(phiFile, Path.Combine(dir, "build", variant), wrapper);

            // NAME.errors: the program must fail to compile, with each listed message
            if (File.Exists(errorsFile))
            {
                if (build.Success) return "expected compile errors, but the build succeeded";

                var missing = File.ReadAllLines(errorsFile)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !l.StartsWith('#'))
                    .Where(l => !build.Errors.Any(e => e.Contains(l)))
                    .ToList();

                if (missing.Count == 0) return null;
                return "missing errors:\n  " + string.Join("\n  ", missing) + "\ngot:\n  " + string.Join("\n  ", build.Errors);
            }

            if (!build.Success) return "build failed:\n" + string.Join("\n", build.Errors);

            string? expected = File.Exists(expectedFile) ? Normalize(File.ReadAllText(expectedFile)) : null;
            string[]? input = File.Exists(inputFile) ? File.ReadAllLines(inputFile) : null;

            // NAME.sound: record what the sound card plays, and check it afterwards
            string soundFile = Path.Combine(dir, name + ".sound");
            string? recording = File.Exists(soundFile) ? Path.Combine(Path.GetDirectoryName(build.ImagePath)!, name + ".wav") : null;
            if (recording != null && File.Exists(recording)) File.Delete(recording);

            // NAME.contains: each line must appear somewhere in the output (for output that
            // changes from build to build, like addresses on a panic screen)
            string containsFile = containsOverride ?? Path.Combine(dir, name + ".contains");
            if (File.Exists(containsFile))
            {
                var lines = File.ReadAllLines(containsFile).Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
                HeadlessResult partial = Qemu.RunHeadless(build.ImagePath, timeout,
                    output => lines.All(l => Normalize(output).Contains(l)), input,
                    screenshots: Path.GetDirectoryName(build.ImagePath));
                string text = Normalize(partial.Serial);
                var missing = lines.Where(l => !text.Contains(l)).ToList();
                if (keepOutput) File.WriteAllText(actualFile, text);
                if (missing.Count == 0 && partial.InputError == null) return null;

                File.WriteAllText(actualFile, text);
                string why = partial.Explain(timeout, "missing from the output: " + string.Join(" | ", missing));
                return $"{why}\n--- actual (saved to {Path.GetFileName(actualFile)})\n{text}";
            }

            HeadlessResult run = Qemu.RunHeadless(build.ImagePath, timeout,
                expected == null ? null : output => Normalize(output).Contains(expected), input, recording,
                Path.GetDirectoryName(build.ImagePath), countInstructions: bench != null);

            string actual = Normalize(run.Serial);
            if (keepOutput) File.WriteAllText(actualFile, actual);

            if (expected == null)
            {
                File.WriteAllText(actualFile, actual);
                return $"missing {name}.expected; the output was saved to {Path.GetFileName(actualFile)} " +
                       "(rename it to .expected if it is correct)";
            }

            if (actual == expected && run.InputError == null)
            {
                if (recording != null)
                {
                    string? soundFailure = SoundCheck.Check(recording, File.ReadAllLines(soundFile));
                    if (soundFailure != null) return $"the sound was wrong ({Path.GetFileName(recording)} is the recording):\n{soundFailure}";
                }
                return bench == null ? null : BenchCheck.Check(Path.Combine(dir, variant + ".bench"), run.Serial, bench, report);
            }

            File.WriteAllText(actualFile, actual);

            string reason = run.Explain(timeout, "output did not match");
            return $"{reason}\n--- expected\n{expected}\n--- actual (saved to {Path.GetFileName(actualFile)})\n{actual}";
        }

        /// <summary>
        /// NAME.machines: the other kernels on one network with the test, one to a line: a .phi
        /// file next to it, and how many copies of it run (1 if left out). # starts a comment.
        /// </summary>
        public static List<(string File, int Copies)> ReadMachines(string manifest)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(manifest))!;
            var machines = new List<(string, int)>();
            foreach (string raw in File.ReadAllLines(manifest))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                int copies = parts.Length > 1 && int.TryParse(parts[1], out int n) && n > 0 ? n : 1;
                machines.Add((Path.Combine(dir, parts[0]), copies));
            }
            return machines;
        }

        /// <summary>
        /// What each machine of a network test is told about itself: its number (1 for the test,
        /// then 2, 3, ... for the machines NAME.machines lists, in order), how many there are,
        /// its address (10.0.3.number) and machine 1's.
        /// </summary>
        static string TestNetClass(int machine, int machines) =>
            $"\nphi.TestNet:Library\n{{\n\tu32 machine: {machine};\n\tu32 machines: {machines};\n" +
            $"\tu32 address: 0x0A0003{machine:X2};\n\tu32 first: 0x0A000301;\n}}\n";

        /// <summary>
        /// A test with NAME.machines: it and the machines it lists boot at once, on one network
        /// (VirtualNetwork), and each must produce its own file's .expected output (copies of
        /// one file share it).
        /// </summary>
        static string? RunNetwork(string phiFile, string manifest, TimeSpan timeout, string wrapper, bool keepOutput)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(phiFile))!;
            var machines = new List<string> { phiFile };
            foreach (var (file, copies) in ReadMachines(manifest))
            {
                if (!File.Exists(file)) return $"{Path.GetFileName(manifest)} lists {file}, which isn't there";
                machines.AddRange(Enumerable.Repeat(file, copies));
            }
            if (machines.Count > 254) return "a network test can have at most 254 machines";

            // build every machine first, each with its own disk
            var built = new List<(string File, int Number, string Image, string? Expected, string Actual)>();
            for (int i = 0; i < machines.Count; i++)
            {
                string file = machines[i];
                int number = i + 1;
                string own = Path.GetFileNameWithoutExtension(file);
                string variant = number == 1 ? own : $"{own}.{number}";
                BuildResult build = Builder.Build(file, Path.Combine(dir, "build", variant), wrapper + TestNetClass(number, machines.Count));
                if (!build.Success)
                    return $"machine {number} ({Path.GetFileName(file)}) didn't build:\n" + string.Join("\n", build.Errors);
                string expectedFile = Path.Combine(Path.GetDirectoryName(file)!, own + ".expected");
                built.Add((file, number, build.ImagePath,
                           File.Exists(expectedFile) ? Normalize(File.ReadAllText(expectedFile)) : null,
                           Path.Combine(Path.GetDirectoryName(file)!, variant + ".actual")));
            }

            using var network = new VirtualNetwork();
            var runs = built.Select(m => Task.Run(() => Qemu.RunHeadless(m.Image, timeout,
                m.Expected == null ? null : output => Normalize(output).Contains(m.Expected),
                network: network.MachineArgs(m.Number)))).ToArray();
            Task.WaitAll(runs);

            var failures = new List<string>();
            for (int i = 0; i < built.Count; i++)
            {
                var m = built[i];
                HeadlessResult run = runs[i].Result;
                string actual = Normalize(run.Serial);
                if (File.Exists(m.Actual)) File.Delete(m.Actual);
                bool passed = m.Expected != null && actual == m.Expected;
                if (keepOutput || !passed) File.WriteAllText(m.Actual, actual);
                if (passed) continue;

                string which = $"machine {m.Number} ({Path.GetFileName(m.File)})";
                if (m.Expected == null)
                    failures.Add($"{which}: missing {Path.GetFileNameWithoutExtension(m.File)}.expected; " +
                                 $"the output was saved to {Path.GetFileName(m.Actual)}");
                else
                    failures.Add($"{which}: {run.Explain(timeout, "output did not match")}\n--- expected\n{m.Expected}\n" +
                                 $"--- actual (saved to {Path.GetFileName(m.Actual)})\n{actual}");
            }
            if (failures.Count == 0) return null;
            return string.Join("\n", failures) + $"\n({network.Frames} frames went over the network)";
        }

        /// <summary>Line endings and trailing whitespace don't matter, and neither do a benchmark's counts.</summary>
        static string Normalize(string s) =>
            string.Join("\n", s.Replace("\r", "").Split('\n').Where(l => !BenchCheck.IsBenchLine(l)).Select(l => l.TrimEnd())).Trim('\n');

        static void Write(ConsoleColor color, string text)
        {
            ConsoleColor old = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.Write(text);
            Console.ForegroundColor = old;
        }
    }
}
