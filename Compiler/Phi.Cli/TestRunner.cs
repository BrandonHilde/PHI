using System.Text.RegularExpressions;
using Phi.Compiler;

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
    /// A kernel test that uses the disk itself (use fs.fat16, drivers.disk or drivers.block) runs
    /// on each kind of disk, IDE, virtio-blk and NVMe, so every driver is checked (--disk picks one).
    ///
    /// A test that can also be an ordinary program (its classes are all :Library, or it is a
    /// :Program) runs as a Linux and a Windows program too, where this computer can run them
    /// (Linux under WSL on Windows), so the targets can't drift apart. It is left to PHI OS when
    /// it can't be built for the other system (it uses a driver, say), or needs what only QEMU
    /// gives a test: an input script, a web server, recorded sound, other machines.
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
        /// <param name="alsoRun">More tests to run after target's (the samples that are tests).</param>
        public static int Run(string target, string? filter, TimeSpan timeout, string? input = null, bool keepOutput = false,
                              string? contains = null, BenchOptions? bench = null, IEnumerable<string>? alsoRun = null)
        {
            List<string> tests = Directory.Exists(target)
                ? Directory.GetFiles(target, "*.phi").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string> { target };
            // a sample is known by its folder too: mmo/server
            var samples = (alsoRun ?? Enumerable.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string TestName(string t) => samples.Contains(t)
                ? $"{Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(t)))}/{Path.GetFileNameWithoutExtension(t)}"
                : Path.GetFileNameWithoutExtension(t);
            tests.AddRange(samples);

            if (filter != null)
                tests = tests.Where(t => TestName(t).Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

            if (bench != null)
                tests = tests.Where(t => Path.GetFileName(t).StartsWith(BenchCheck.Prefix, StringComparison.OrdinalIgnoreCase)).ToList();

            // the other machines of a network test run with it, not as tests of their own
            if (Directory.Exists(target))
            {
                var listed = Directory.GetFiles(target, "*.machines")
                    .SelectMany(m => NetworkMachines.Read(m).Select(x => x.File))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                tests = tests.Where(t => !listed.Contains(Path.GetFullPath(t))).ToList();
            }

            if (tests.Count == 0)
            {
                Console.WriteLine(bench != null ? $"No benchmarks ({BenchCheck.Prefix}NAME.phi) found in {target}" : $"No tests found in {target}");
                return 1;
            }

            var runs = tests.SelectMany(t => Modes(t, bench != null).Select(m => (File: t, Mode: m))).ToList();
            if (Only is Host only && only != Host.PhiOS)
            {
                int left = tests.Count - runs.Select(r => r.File).Distinct().Count();
                Console.WriteLine($"{runs.Count} of {tests.Count} tests run as {only.Name()} programs" +
                                  (left > 0 ? $" ({left} only run on PHI OS)" : ""));
                if (runs.Count == 0) return 1;
            }

            int passed = 0;
            var secondRun = new List<string>();
            foreach (var (test, mode) in runs)
            {
                string name = TestName(test) + (mode == null ? "" : $" ({mode.Name})");
                var report = new List<string>();
                string? failure = RunGuarded(test, TimeFor(test, timeout), mode, input, keepOutput, contains, bench, report);

                // a test that failed runs once more, unless what failed was its build (that won't change)
                string? first = null;
                if (failure != null && Retry && !IsBuildFailure(failure))
                {
                    first = failure;
                    // what the first run wrote is kept, as NAME.first.actual: the second run's replaces NAME.actual
                    string actual = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(test))!,
                                                 Path.GetFileNameWithoutExtension(test) + (mode == null ? "" : "." + mode.Name));
                    if (File.Exists(actual + ".actual")) File.Copy(actual + ".actual", actual + ".first.actual", overwrite: true);
                    report.Clear();
                    failure = RunGuarded(test, TimeFor(test, timeout), mode, input, keepOutput, contains, bench, report);
                }

                if (failure == null && first != null)
                {
                    passed++;
                    secondRun.Add(name);
                    Write(ConsoleColor.Yellow, "PASS ");
                    Console.WriteLine(name + " (on its second run)");
                    foreach (string line in report) Console.WriteLine("     " + line);
                    foreach (string line in first.Split('\n').TakeWhile(l => !l.StartsWith("---"))) Console.WriteLine("     the first run: " + line);
                    string kept = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(test))!,
                                               Path.GetFileNameWithoutExtension(test) + (mode == null ? "" : "." + mode.Name) + ".first.actual");
                    if (File.Exists(kept)) Console.WriteLine($"     (its output is in {Path.GetFileName(kept)})");
                }
                else if (failure == null)
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
            if (secondRun.Count > 0)
            {
                // a busy computer slows a test past its time; but a test that fails only some of the
                // time can also be a race between threads, so they're named, not hidden
                Write(ConsoleColor.Yellow, $"{secondRun.Count} passed only on a second run: {string.Join(", ", secondRun)}");
                Console.WriteLine();
                Console.WriteLine("(the computer was busy, or the test has a race worth a look: phi test --no-retry -f NAME)");
            }

            return passed == runs.Count ? 0 : 1;
        }

        /// <summary>
        /// How to turn a library-only test into a whole program, or (for a network test) the card
        /// the machines get, or (for a disk test) the disk; and the system it runs on.
        /// </summary>
        sealed record Mode(string Name, string Wrapper, string? Card = null, Host Host = Host.PhiOS, string? Disk = null, bool Debugger = false);

        /// <summary>phi test --target: run only on that system (PHI OS, or as Linux or Windows programs).</summary>
        public static Host? Only { get; set; }

        static readonly Host[] OtherSystems = { Host.Linux, Host.Windows, Host.Linux64, Host.Windows64 };
        const string ProgramWrapper = "\nphi.PhiTestProgram:Program\n{\n}\n";

        /// <summary>What only a test in QEMU can have: input typed into it, a web server, a sound card, other machines.</summary>
        static readonly string[] OnlyInQemu = { ".input", ".web", ".sound", ".machines", ".errors" };

        static readonly Mode[] BothModes =
        {
            new("16-bit", "\nphi.PhiTestBoot:Bootloader\n{\n\tcall Bootloader.JumpToSectorTwo;\n}\n"),
            new("32-bit", "\nphi.PhiTestKernel:Kernel\n{\n}\n"),
        };

        static readonly Regex LibraryClass = new(@"^\s*phi\.\w+\s*:\s*Library\b", RegexOptions.Multiline);
        static readonly Regex ProgramClass = new(@"^\s*phi\.\w+\s*:\s*(Bootloader|OS|Kernel)\b", RegexOptions.Multiline);
        /// <summary>A test that builds as a program but needs a PHI OS machine (its memory, say) says so with this comment.</summary>
        static readonly Regex PhiOsOnly = new(@"^#\s*runs only on PHI OS\b", RegexOptions.Multiline);
        /// <summary>
        /// A library test too big for a 16-bit program (its code and variables in one 64 KB
        /// segment, at most 32 KB of OS classes) says so with this comment: it runs as a 32-bit
        /// kernel and on the other systems.
        /// </summary>
        static readonly Regex Not16Bit = new(@"^#\s*runs only in 32 and 64 bits\b", RegexOptions.Multiline);
        /// <summary>
        /// A test that needs longer than the usual time (one that types a long input script, say)
        /// says so with this comment: "# takes up to N seconds: why".
        /// </summary>
        static readonly Regex TakesUpTo = new(@"^#\s*takes up to (\d+) seconds\b", RegexOptions.Multiline);

        /// <summary>The time a test gets: what --timeout gives, or more if the test asks for it.</summary>
        static TimeSpan TimeFor(string phiFile, TimeSpan timeout)
        {
            Match m = TakesUpTo.Match(File.ReadAllText(phiFile));
            return m.Success && int.TryParse(m.Groups[1].Value, out int seconds) && seconds > timeout.TotalSeconds
                ? TimeSpan.FromSeconds(seconds) : timeout;
        }

        static readonly Regex UserProgramClass =new(@"^\s*phi\.\w+\s*:\s*Program\b", RegexOptions.Multiline);

        /// <summary>
        /// null for an ordinary test; both modes for a test made only of :Library classes; and
        /// each network card for a network test whose NAME.machines doesn't choose one, so both
        /// drivers are tested (--nic picks just one).
        /// </summary>
        static IEnumerable<Mode?> Modes(string phiFile, bool bench)
        {
            var modes = new List<Mode?>();
            if (Only is null or Host.PhiOS) modes.AddRange(PhiOsModes(phiFile));
            if (Only != Host.PhiOS && !bench) modes.AddRange(HostModes(phiFile));
            return modes;
        }

        /// <summary>The other systems a test runs on as an ordinary program (see the class's summary).</summary>
        static IEnumerable<Mode?> HostModes(string phiFile)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(phiFile))!;
            string name = Path.GetFileNameWithoutExtension(phiFile);
            string own = Path.Combine(dir, name);
            if (OnlyInQemu.Any(e => File.Exists(own + e) || Directory.Exists(own + e))) yield break;

            // NAME.cdb: it's what a debugger is asked of the program, where there's one to ask
            if (File.Exists(own + ".cdb"))
            {
                foreach (Host host in new[] { Host.Windows, Host.Windows64 }.Where(h => Only == null || Only == h))
                    if (HostRunner.CanRun(host, out _) && DebuggerRunner.Find(host) != null)
                        yield return new Mode(host.FlagName(), "", Host: host, Debugger: true);
                yield break;
            }

            string text = File.ReadAllText(phiFile);
            if (PhiOsOnly.IsMatch(text)) yield break;
            bool program = UserProgramClass.IsMatch(text);
            if (!program && !(LibraryClass.IsMatch(text) && !ProgramClass.IsMatch(text))) yield break;
            string wrapper = program ? "" : ProgramWrapper;

            Host? firstHere = null;
            int built = 0;
            foreach (Host host in OtherSystems.Where(h => Only == null || Only == h))
            {
                if (!HostRunner.CanRun(host, out _)) continue;
                firstHere ??= host;
                var source = new SourceFile(Path.GetRelativePath(Environment.CurrentDirectory, phiFile), text + wrapper);
                if (!PhiCompiler.Compile(source, generate: false, host: host).Success) continue;
                built++;
                yield return new Mode(host.FlagName(), wrapper, Host: host);
            }
            // a program that builds for none of them runs on none, which would pass by saying
            // nothing: it runs on the first, where its build fails and says why
            if (program && built == 0 && Only == null && firstHere is Host first)
                yield return new Mode(first.FlagName(), wrapper, Host: first);
        }

        static IEnumerable<Mode?> PhiOsModes(string phiFile)
        {
            if (NetworkMachines.ManifestFor(phiFile) is string manifest)
            {
                if (NetworkMachines.Card(manifest) != null || NicChosen) return new Mode?[] { null };
                return Qemu.NetworkCards.Select(card => (Mode?)new Mode(card, "", card));
            }
            string text = File.ReadAllText(phiFile);
            // a program boots only inside a kernel (from its rootfs), so on its own it's a test for the other systems
            if (UserProgramClass.IsMatch(text)) return Array.Empty<Mode?>();
            bool libraryOnly = LibraryClass.IsMatch(text) && !ProgramClass.IsMatch(text);
            if (libraryOnly && Not16Bit.IsMatch(text)) return new Mode?[] { BothModes[1] };
            if (!libraryOnly && !DiskChosen && UsesDisk.IsMatch(text))
                return Qemu.DiskKinds.Select(disk => (Mode?)new Mode(disk, "", Disk: disk));
            return libraryOnly ? BothModes : new Mode?[] { null };
        }

        static readonly Regex UsesDisk = new(@"^\s*use\s+(fs\.fat16|drivers\.disk|drivers\.block)\s*;", RegexOptions.Multiline);

        /// <summary>A test that fails runs once more (phi test --no-retry turns that off).</summary>
        public static bool Retry { get; set; } = true;

        static bool IsBuildFailure(string failure) =>
            failure.StartsWith("build failed") || failure.StartsWith("expected compile errors") || failure.StartsWith("missing errors")
            || failure.StartsWith("missing ") && failure.Contains(".expected");

        /// <summary>phi test --nic: every test uses that card, and network tests run once.</summary>
        public static bool NicChosen { get; set; }

        /// <summary>phi test --disk: every test uses that disk, and disk tests run once.</summary>
        public static bool DiskChosen { get; set; }

        /// <summary>RunOne, with a file it couldn't write or read (one still in use, say) as the test's failure rather than the end of the run.</summary>
        static string? RunGuarded(string phiFile, TimeSpan timeout, Mode? mode, string? inputOverride, bool keepOutput, string? containsOverride,
                                  BenchOptions? bench, List<string> report)
        {
            try { return RunOne(phiFile, timeout, mode, inputOverride, keepOutput, containsOverride, bench, report); }
            catch (IOException e) { return $"couldn't run: {e.Message}"; }
        }

        /// <summary>The class TestWeb a test with a NAME.web folder is built with.</summary>
        internal static string WebClass(int port, int securePort, IEnumerable<byte> root) =>
            $"\nphi.TestWeb:Library\n{{\n\tstr url: 'http://10.0.2.2:{port}';\n" +
            $"\tstr secure_url: 'https://10.0.2.2:{securePort}';\n" +
            $"\tu8 root: {string.Join(" ", root.Select(b => $"0x{b:X2}"))};\n}}\n";

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
            // TestWeb.secure_url ('https://10.0.2.2:PORT'), whose certificate is TestWeb.root (DER,
            // for X509.Trust)
            string webDir = Path.Combine(dir, name + ".web");
            using WebServer? web = Directory.Exists(webDir) ? new WebServer(webDir) : null;
            using WebServer? secureWeb = web != null ? new WebServer(webDir, secure: true) : null;
            string wrapper = mode?.Wrapper ?? "";
            if (web != null)
                wrapper += WebClass(web.Port, secureWeb!.Port, WebServer.CertificateDer);

            if (mode != null && mode.Host != Host.PhiOS)
                return mode.Debugger ? RunDebugger(phiFile, timeout, mode, keepOutput) : RunHosted(phiFile, timeout, mode, keepOutput, containsOverride);

            string machinesFile = Path.Combine(dir, name + ".machines");
            if (File.Exists(machinesFile))
                return RunNetwork(phiFile, machinesFile, timeout, wrapper, keepOutput, mode?.Card);

            string outDir = Path.Combine(dir, "build", variant);
            BuildResult build = Builder.Build(phiFile, outDir, wrapper, listing: true);

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
                    screenshots: Path.GetDirectoryName(build.ImagePath), disk: mode?.Disk, where: true);
                string text = Normalize(partial.Serial);
                var missing = lines.Where(l => !text.Contains(l)).ToList();
                if (keepOutput) File.WriteAllText(actualFile, text);
                if (missing.Count == 0 && partial.InputError == null) return null;

                File.WriteAllText(actualFile, text);
                string why = partial.Explain(timeout, "missing from the output: " + string.Join(" | ", missing)) + WhereIt(partial.Stopped, outDir);
                return $"{why}\n--- actual (saved to {Path.GetFileName(actualFile)})\n{text}";
            }

            HeadlessResult run = Qemu.RunHeadless(build.ImagePath, timeout,
                expected == null ? null : output => Normalize(output).Contains(expected), input, recording,
                Path.GetDirectoryName(build.ImagePath), countInstructions: bench != null, disk: mode?.Disk, where: true);

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

            string reason = run.Explain(timeout, "output did not match") + WhereIt(run.Stopped, outDir);
            return $"{reason}\n--- expected\n{expected}\n--- actual (saved to {Path.GetFileName(actualFile)})\n{actual}";
        }

        /// <summary>
        /// A test with NAME.cdb: a --debug build, run in Microsoft's debugger, with what it says
        /// (DebuggerRunner) compared with NAME.expected, or NAME.MODE.expected.
        /// </summary>
        static string? RunDebugger(string phiFile, TimeSpan timeout, Mode mode, bool keepOutput)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(phiFile))!;
            string name = Path.GetFileNameWithoutExtension(phiFile);
            string variant = $"{name}.{mode.Name}";
            string actualFile = Path.Combine(dir, variant + ".actual");
            if (File.Exists(actualFile)) File.Delete(actualFile);

            string outDir = Path.Combine(dir, "build", variant);
            BuildResult build = Builder.Build(phiFile, outDir, host: mode.Host, debug: true);
            if (!build.Success) return "build failed:\n" + string.Join("\n", build.Errors);

            string actual = Normalize(DebuggerRunner.Run(build.ExecutablePath!, mode.Host, Path.Combine(dir, name + ".cdb"), timeout));
            if (keepOutput) File.WriteAllText(actualFile, actual);
            string expectedFile = File.Exists(Path.Combine(dir, variant + ".expected"))
                ? Path.Combine(dir, variant + ".expected")
                : Path.Combine(dir, name + ".expected");
            if (!File.Exists(expectedFile))
            {
                File.WriteAllText(actualFile, actual);
                return $"missing {name}.expected; the output was saved to {Path.GetFileName(actualFile)} " +
                       "(rename it to .expected if it is correct)";
            }
            string expected = Normalize(File.ReadAllText(expectedFile));
            if (actual == expected) return null;
            File.WriteAllText(actualFile, actual);
            return $"what the debugger said did not match\n--- expected\n{expected}\n--- actual (saved to {Path.GetFileName(actualFile)})\n{actual}";
        }

        /// <summary>phi test --debug-builds: every test that's a program is built as --debug would, to see they do the same.</summary>
        public static bool DebugBuilds { get; set; }

        /// <summary>A test built as a Linux or Windows program, run here, and checked like one in QEMU.</summary>
        static string? RunHosted(string phiFile, TimeSpan timeout, Mode mode, bool keepOutput, string? containsOverride)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(phiFile))!;
            string name = Path.GetFileNameWithoutExtension(phiFile);
            string variant = $"{name}.{mode.Name}";
            string actualFile = Path.Combine(dir, variant + ".actual");
            if (File.Exists(actualFile)) File.Delete(actualFile);

            string outDir = Path.Combine(dir, "build", variant);
            BuildResult build = Builder.Build(phiFile, outDir, mode.Wrapper, listing: true, host: mode.Host, debug: DebugBuilds);
            if (!build.Success) return "build failed:\n" + string.Join("\n", build.Errors);

            HostRunner.Result run = HostRunner.Run(build.ExecutablePath!, mode.Host, timeout);
            string actual = Normalize(run.Output);
            if (keepOutput) File.WriteAllText(actualFile, actual);
            string stopped = run.TimedOut ? $"timed out after {timeout.TotalSeconds:0.#}s" : $"the program ended with {run.ExitCode}";
            string where = WhereIt(run.Stopped, outDir);
            // a Windows program is looked for by its .exe; none running means it hadn't started
            if (run.TimedOut && mode.Host.IsWindows() && run.Stopped.Count == 0)
                where = "\nnothing was running from its .exe when its time ran out: it may never have started " +
                        "(Windows checks a new .exe before running it, which can take a while on a busy computer)";

            string containsFile = containsOverride ?? Path.Combine(dir, name + ".contains");
            if (File.Exists(containsFile))
            {
                var missing = File.ReadAllLines(containsFile).Select(l => l.TrimEnd()).Where(l => l.Length > 0 && !actual.Contains(l)).ToList();
                if (missing.Count == 0) return null;
                File.WriteAllText(actualFile, actual);
                return $"missing from the output ({stopped}): {string.Join(" | ", missing)}{where}\n--- actual (saved to {Path.GetFileName(actualFile)})\n{actual}";
            }

            // NAME.MODE.expected for what's meant to differ on that system (the size of a
            // pointer, in 64-bit code), else NAME.expected
            string expectedFile = File.Exists(Path.Combine(dir, variant + ".expected"))
                ? Path.Combine(dir, variant + ".expected")
                : Path.Combine(dir, name + ".expected");
            if (!File.Exists(expectedFile))
            {
                File.WriteAllText(actualFile, actual);
                return $"missing {name}.expected; the output was saved to {Path.GetFileName(actualFile)} " +
                       "(rename it to .expected if it is correct)";
            }
            string expected = Normalize(File.ReadAllText(expectedFile));
            if (actual == expected && !run.TimedOut) return null;
            File.WriteAllText(actualFile, actual);
            return $"output did not match ({stopped}){where}\n--- expected\n{expected}\n--- actual (saved to {Path.GetFileName(actualFile)})\n{actual}";
        }

        /// <summary>
        /// A test with NAME.machines (see NetworkMachines): it and the machines it lists boot at
        /// once on one network (VirtualNetwork). Each machine is checked like a test of its own,
        /// with its file's .expected or .contains (copies of one file share them), and its
        /// .input script if it has one.
        /// </summary>
        static string? RunNetwork(string phiFile, string manifest, TimeSpan timeout, string wrapper, bool keepOutput, string? card)
        {
            var errors = new List<string>();
            List<NetworkMachines.Machine>? machines = NetworkMachines.Build(phiFile, manifest, wrapper, errors);
            if (machines == null) return string.Join("\n", errors);

            var checks = machines.Select(m => new MachineCheck(m)).ToList();
            using var network = new VirtualNetwork(NetworkMachines.LoseEvery(manifest), card ?? NetworkMachines.Card(manifest),
                                                   NetworkMachines.Mtu(manifest), NetworkMachines.Dhcp(manifest));
            var runs = checks.Select(c => Task.Run(() => Qemu.RunHeadless(c.Machine.Image, timeout, c.IsDone, c.Input,
                screenshots: Path.GetDirectoryName(c.Machine.Image), network: network.MachineArgs(c.Machine.Number)))).ToArray();
            Task.WaitAll(runs);

            var failures = new List<string>();
            for (int i = 0; i < checks.Count; i++)
            {
                string? failure = checks[i].Check(runs[i].Result, timeout, keepOutput);
                if (failure != null) failures.Add($"machine {checks[i].Machine.Number} ({Path.GetFileName(checks[i].Machine.File)}): {failure}");
            }
            if (failures.Count == 0) return null;
            return string.Join("\n", failures) + $"\n({network.Frames} frames went over the network, {network.Lost} lost on purpose, {network.TooBig} too big)";
        }

        /// <summary>What one machine of a network test must do: its file's .expected or .contains, and its .input.</summary>
        sealed class MachineCheck
        {
            public NetworkMachines.Machine Machine { get; }
            public string[]? Input { get; }
            readonly string? expected;
            readonly List<string>? contains;
            readonly string actualFile;

            public MachineCheck(NetworkMachines.Machine machine)
            {
                Machine = machine;
                string dir = Path.GetDirectoryName(machine.File)!;
                string own = Path.Combine(dir, Path.GetFileNameWithoutExtension(machine.File));
                if (File.Exists(own + ".expected")) expected = Normalize(File.ReadAllText(own + ".expected"));
                if (File.Exists(own + ".contains"))
                    contains = File.ReadAllLines(own + ".contains").Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
                if (File.Exists(own + ".input")) Input = File.ReadAllLines(own + ".input");
                actualFile = Path.Combine(dir, NetworkMachines.Variant(machine.File, machine.Number) + ".actual");
            }

            public bool IsDone(string output) =>
                contains != null ? contains.All(Normalize(output).Contains)
                : expected != null && Normalize(output).Contains(expected);

            /// <returns>null if it did what it should, otherwise why not</returns>
            public string? Check(HeadlessResult run, TimeSpan timeout, bool keepOutput)
            {
                string actual = Normalize(run.Serial);
                if (File.Exists(actualFile)) File.Delete(actualFile);

                string? why = null;
                if (expected == null && contains == null)
                    why = $"missing {Path.GetFileNameWithoutExtension(Machine.File)}.expected (or .contains)";
                else if (contains != null)
                {
                    var missing = contains.Where(l => !actual.Contains(l)).ToList();
                    if (missing.Count > 0 || run.InputError != null)
                        why = run.Explain(timeout, "missing from the output: " + string.Join(" | ", missing));
                }
                else if (actual != expected || run.InputError != null)
                    why = run.Explain(timeout, "output did not match") + $"\n--- expected\n{expected}";

                if (keepOutput || why != null) File.WriteAllText(actualFile, actual);
                return why == null ? null : $"{why}\n--- actual (saved to {Path.GetFileName(actualFile)})\n{actual}";
            }
        }

        /// <summary>
        /// The samples that are tests too: a .phi file anywhere under samples/ with a .expected,
        /// .contains or .machines next to it (not the machines another one lists).
        /// </summary>
        public static List<string> SampleTests(string samples)
        {
            if (!Directory.Exists(samples)) return new List<string>();
            // only the folders inside samples/ count: the path above it can have any names
            bool InBuild(string f) => Path.GetRelativePath(samples, f).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                        .Any(part => part == "build" || part.EndsWith(".rootfs"));
            var files = Directory.GetFiles(samples, "*.phi", SearchOption.AllDirectories).Where(f => !InBuild(f)).ToList();
            var listed = files.Select(NetworkMachines.ManifestFor).OfType<string>()
                              .SelectMany(m => NetworkMachines.Read(m).Select(x => x.File))
                              .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return files.Where(f => !listed.Contains(Path.GetFullPath(f)))
                        .Where(f => new[] { ".expected", ".contains", ".machines" }.Any(e => File.Exists(Path.ChangeExtension(f, e))))
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>For a run that timed out: where it was (see Where), on lines of its own after the reason; else "".</summary>
        static string WhereIt(IReadOnlyList<StoppedThread> stopped, string outDir)
        {
            if (stopped.Count == 0) return "";
            string report = Where.Report(stopped, Where.MapsIn(outDir));
            return report.Length == 0 ? "" : "\n" + report;
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
