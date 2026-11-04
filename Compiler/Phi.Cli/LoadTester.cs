using System.Text.RegularExpressions;
using Phi.Compiler;

namespace Phi.Cli
{
    /// <summary>
    /// phi load: boots a server (machine 1, as Machine.address) on a VirtualNetwork with as
    /// many load-client kernels as it takes to open the connections, up to PerMachine each.
    ///
    /// The default client (lib/phi/test/load.phi) logs its connections in to the server's port
    /// ("login NAME PASSWORD", answered "welcome NAME": the mmo sample's lobby), has each say a
    /// numbered line once a second for the run ("say ...", answered "said ..."), checking that
    /// every answer comes back in order, and prints its totals, which this adds up. The clients
    /// are PHI kernels rather than connections from this computer: QEMU's user network on
    /// Windows can only keep 64 connections from outside.
    ///
    /// A client is a :Library with no network of its own: phi load adds the one for where it
    /// runs (net.tcp and net.udp in a kernel, net.sockets in a program, whose calls are the
    /// same), and the class that makes it a kernel or a program.
    ///
    /// With a host (--target linux or windows), the server is a program (a world or lobby from a
    /// kernel's rootfs, say) run on this computer, in a folder of its own with copies of the
    /// files beside it (a certificate, say), as a fresh disk would have them; and the clients are
    /// programs too, one for each PerMachine connections, reaching it at 127.0.0.1.
    ///
    /// --client FILE uses another load client, built with the same LoadSettings and
    /// Machine classes. Its last line is "load: NAME COUNT NAME COUNT ...", which this adds up
    /// across the machines (taking the largest of a name that starts with "most"); it passes
    /// when its "problems" count is 0.
    ///
    /// Either way, a server that writes "tick: N ticks in MS ms, L late, longest X ms, P players"
    /// (or "zone Z tick: ...", one for each zone of a world split into zones)
    /// lines (the mmo world does, every 10 seconds) has its tick checked: after the first such
    /// line, and once it has as many players as it gets in the run, no tick may be late.
    /// </summary>
    public static class LoadTester
    {
        const int PerMachine = 100;
        static readonly Regex Totals = new(@"load: connections (\d+) logged (\d+) answered (\d+) wrong (\d+) quit (\d+) latency (\d+) most (\d+)");
        static readonly Regex Handshakes = new(@" handshake (\d+) refused (\d+)");
        static readonly Regex Counts = new(@"^load: (.+)$", RegexOptions.Multiline);
        static readonly Regex Ticks = new(@"(?:zone (\d+) )?tick: (\d+) ticks in (\d+) ms, (\d+) late, longest (\d+) ms, (\d+) players");

        /// <param name="clientFile">a load-client kernel to use instead of lib/phi/test/load.phi</param>
        /// <param name="profile">sample where the server's CPU is, as phi profile does, and report it</param>
        /// <param name="root">with host: each connection is TLS, trusting this root (a DER file) for a certificate for host</param>
        /// <param name="target">PHI OS (kernels on a network), or a system the server and clients run on here as programs</param>
        public static int Run(string phiFile, int connections, int seconds, int port, bool profile = false, string? clientFile = null,
                              string? root = null, string? host = null, Host target = Host.PhiOS)
        {
            if (target != Host.PhiOS) return RunOnHost(phiFile, target, connections, seconds, port, clientFile, root, host);
            string file = Path.GetFullPath(phiFile);
            string outDir = Path.Combine(Path.GetDirectoryName(file)!, "build", Path.GetFileNameWithoutExtension(file) + ".load");
            Directory.CreateDirectory(outDir);

            int clients = (connections + PerMachine - 1) / PerMachine;
            int machines = clients + 1;
            var errors = new List<string>();
            var warnings = new List<string>();

            BuildResult server = Builder.Build(file, Path.Combine(outDir, "server"),
                NetworkMachines.MachineClass(1, machines, 0x0A000301, 0x0A000301), listing: profile);
            warnings.AddRange(server.Warnings);
            errors.AddRange(server.Errors);

            // each client: the load client (or a stub that uses the default one), told who it is and what to do
            string client = clientFile != null ? Path.GetFullPath(clientFile) : Path.Combine(outDir, "client.phi");
            if (clientFile == null) File.WriteAllText(client, Stub(KernelClient));
            var images = new List<string> { server.ImagePath };
            for (int c = 0; c < clients && errors.Count == 0; c++)
            {
                int number = c + 2;
                int share = Math.Min(PerMachine, connections - c * PerMachine);
                BuildResult built = Builder.Build(client, Path.Combine(outDir, $"client.{number}"), KernelClient +
                    NetworkMachines.MachineClass(number, machines, 0x0A000300u + (uint)number, 0x0A000301) + Settings(share, seconds, port, root, host));
                warnings.AddRange(built.Warnings.Where(w => !warnings.Contains(w)));
                errors.AddRange(built.Errors);
                images.Add(built.ImagePath);
            }
            foreach (string w in warnings.Distinct()) Console.Error.WriteLine(w.Contains(": warning:") ? w : "warning: " + w);
            foreach (string e in errors) Console.Error.WriteLine(e.Contains(": error:") ? e : "error: " + e);
            if (errors.Count > 0) return 1;
            if (server.IsProgram)
            {
                Console.Error.WriteLine("error: phi load needs a kernel, not a user program");
                return 1;
            }

            string what = clientFile == null
                ? $"{connections} connections to {Path.GetRelativePath(Environment.CurrentDirectory, file)} (port {port})"
                : $"{connections} of {Path.GetRelativePath(Environment.CurrentDirectory, client)} to {Path.GetRelativePath(Environment.CurrentDirectory, file)}";
            Console.WriteLine($"phi load: {what} from {clients} machines, for {seconds} s");

            using var network = new VirtualNetwork(card: Qemu.NetworkCard);
            int? profilePort = profile ? Qemu.FreePort() : null;
            using var stopSampling = new CancellationTokenSource();
            Task<List<Profiler.Sample>>? sampler = profilePort is int pp ? Task.Run(() => Profiler.Collect(pp, stopSampling.Token)) : null;

            Regex done = clientFile == null ? Totals : Counts;
            int clientsDone = 0;
            TimeSpan timeout = TimeSpan.FromSeconds(seconds + 240);
            // when each of the server's tick reports came, by this computer's clock: a machine
            // that can't keep up can fall behind in its own time too, which its ms wouldn't show
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var reportedAt = new List<double>();
            bool ServerDone(string output)
            {
                int seen = Ticks.Matches(output).Count;
                while (reportedAt.Count < seen) reportedAt.Add(clock.Elapsed.TotalMilliseconds);
                return Volatile.Read(ref clientsDone) == clients;
            }

            // a client is done when its machine stops (it exits after its totals, which QEMU can
            // do before its output is looked at again); the server runs until they all are
            var runs = images.Select((image, i) => Task.Run(() =>
            {
                HeadlessResult run = Qemu.RunHeadless(image, timeout,
                    i == 0 ? ServerDone : done.IsMatch,
                    network: network.MachineArgs(i + 1), profilePort: i == 0 ? profilePort : null);
                if (i > 0) Interlocked.Increment(ref clientsDone);
                return run;
            })).ToArray();
            Task.WaitAll(runs);
            stopSampling.Cancel();

            File.WriteAllText(Path.Combine(outDir, "server.txt"), runs[0].Result.Serial);
            var problems = new List<string>();
            var outputs = new List<string>();
            for (int i = 1; i < runs.Length; i++)
            {
                HeadlessResult run = runs[i].Result;
                File.WriteAllText(Path.Combine(outDir, $"client.{i + 1}.txt"), run.Serial);
                if (!done.IsMatch(run.Serial))
                    problems.Add($"machine {i + 1} didn't finish: {run.Explain(timeout, "no totals")}");
                else
                    outputs.Add(run.Serial);
            }

            if (sampler != null && sampler.Wait(TimeSpan.FromSeconds(5)))
            {
                var maps = new[] { Path.Combine(outDir, "server", "kernel.lst") }.Where(File.Exists).Select(AddressMap.Load).ToList();
                Console.Write(Profiler.Report(sampler.Result, maps, 12, seconds));
                Console.WriteLine();
            }

            bool ok = clientFile == null ? Lobby(outputs, connections, seconds) : Custom(outputs);
            ok &= TickHeld(runs[0].Result.Serial, reportedAt);
            Console.WriteLine($"  network:    {network.Frames:N0} frames");
            foreach (string p in problems) Console.WriteLine("  " + p);
            Console.WriteLine($"  (each machine's output: {Path.GetRelativePath(Environment.CurrentDirectory, outDir)})");

            ok &= problems.Count == 0;
            Console.WriteLine(ok ? "passed" : "FAILED");
            return ok ? 0 : 1;
        }

        /// <summary>What makes a load client (a :Library) a kernel, with the network it uses.</summary>
        const string KernelClient = "\nuse net.tcp;\nuse net.udp;\nphi.LoadMachine:Kernel\n{\n}\n";

        /// <summary>What makes a load client a program, with the network it uses.</summary>
        const string ProgramClient = "\nuse net.sockets;\nphi.LoadMachine:Program\n{\n}\n";

        const uint Loopback = 0x7F000001;

        /// <summary>What a run of phi load adds to a load client, for checking one: which machine it is and how many connections don't matter to a check.</summary>
        internal static string ClientAdded(bool kernel) => (kernel ? KernelClient : ProgramClient) +
            NetworkMachines.MachineClass(2, 2, 0x0A000302, 0x0A000301) + Settings(1, 1, 4001, null, null);

        /// <summary>
        /// The default client's file: the network's use lines first, since a module's classes
        /// start in the order they're used (the client's body talks to the network as it starts).
        /// </summary>
        static string Stub(string wrapper) =>
            "# made by phi load\n" + string.Concat(wrapper.Split('\n').Where(l => l.StartsWith("use ")).Select(l => l + "\n")) + "use test.load;\n";

        /// <summary>The class LoadSettings a client is built with: its share of the connections, the run, and TLS's root and name (with --root and --host).</summary>
        static string Settings(int share, int seconds, int port, string? root, string? host)
        {
            string tls = root != null
                ? $"\tbln tls: true;\n\tstr host: '{host}';\n\tu8 root: {string.Join(" ", File.ReadAllBytes(root).Select(b => $"0x{b:X2}"))};\n"
                : "\tbln tls: false;\n\tstr host: '';\n\tu8 root[1];\n";
            return $"\nphi.LoadSettings:Library\n{{\n\tu32 connections: {share};\n\tu32 seconds: {seconds};\n\tu32 port: {port};\n{tls}}}\n";
        }

        /// <summary>phi load --target: the server program and its clients, all as programs on this computer.</summary>
        static int RunOnHost(string phiFile, Host target, int connections, int seconds, int port, string? clientFile, string? root, string? host)
        {
            if (!HostRunner.CanRun(target, out string why))
            {
                Console.Error.WriteLine("error: " + why);
                return 1;
            }
            string file = Path.GetFullPath(phiFile);
            string outDir = Path.Combine(Builder.BuildHome(file), "build", $"{Path.GetFileNameWithoutExtension(file)}.load.{target.FlagName()}");
            Directory.CreateDirectory(outDir);

            int clients = (connections + PerMachine - 1) / PerMachine;
            int machines = clients + 1;
            var errors = new List<string>();
            var warnings = new List<string>();

            BuildResult server = Builder.Build(file, Path.Combine(outDir, "server"), listing: true, host: target);
            warnings.AddRange(server.Warnings);
            errors.AddRange(server.Errors);

            string client = clientFile != null ? Path.GetFullPath(clientFile) : Path.Combine(outDir, "client.phi");
            if (clientFile == null) File.WriteAllText(client, Stub(ProgramClient));
            var programs = new List<string>();
            for (int c = 0; c < clients && errors.Count == 0; c++)
            {
                int number = c + 2;
                int share = Math.Min(PerMachine, connections - c * PerMachine);
                BuildResult built = Builder.Build(client, Path.Combine(outDir, $"client.{number}"), ProgramClient +
                    NetworkMachines.MachineClass(number, machines, Loopback, Loopback) + Settings(share, seconds, port, root, host),
                    host: target);
                warnings.AddRange(built.Warnings.Where(w => !warnings.Contains(w)));
                errors.AddRange(built.Errors);
                if (built.ExecutablePath != null) programs.Add(built.ExecutablePath);
            }
            foreach (string w in warnings.Distinct()) Console.Error.WriteLine(w.Contains(": warning:") ? w : "warning: " + w);
            foreach (string e in errors) Console.Error.WriteLine(e.Contains(": error:") ? e : "error: " + e);
            if (errors.Count > 0) return 1;

            // the server's own folder: the files beside it, as a fresh disk has them, and nothing it saved last time
            string folder = Path.Combine(outDir, "server.files");
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            Directory.CreateDirectory(folder);
            foreach (string f in Directory.GetFiles(Path.GetDirectoryName(file)!).Where(f => !f.EndsWith(".phi", StringComparison.OrdinalIgnoreCase)))
                File.Copy(f, Path.Combine(folder, Path.GetFileName(f)));

            string what = clientFile == null
                ? $"{connections} connections to {Path.GetRelativePath(Environment.CurrentDirectory, file)} (port {port})"
                : $"{connections} of {Path.GetRelativePath(Environment.CurrentDirectory, client)} to {Path.GetRelativePath(Environment.CurrentDirectory, file)}";
            Console.WriteLine($"phi load: {what}, all {target.Name()} programs here ({clients} clients), for {seconds} s");

            Regex done = clientFile == null ? Totals : Counts;
            TimeSpan timeout = TimeSpan.FromSeconds(seconds + 240);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var reportedAt = new List<double>();
            void Look(string output)
            {
                int seen = Ticks.Matches(output).Count;
                while (reportedAt.Count < seen) reportedAt.Add(clock.Elapsed.TotalMilliseconds);
            }

            string serverOutput;
            HostRunner.Result[] results;
            using (HostRunner.Running running = HostRunner.StartBeside(server.ExecutablePath!, target, folder))
            {
                var runs = programs.Select(p => Task.Run(() => HostRunner.Run(p, target, timeout))).ToArray();
                while (!Task.WaitAll(runs, 100)) Look(running.Output);
                Look(running.Output);
                running.Stop();
                serverOutput = running.Output;
                results = runs.Select(r => r.Result).ToArray();
            }

            File.WriteAllText(Path.Combine(outDir, "server.txt"), serverOutput);
            var problems = new List<string>();
            var outputs = new List<string>();
            for (int i = 0; i < results.Length; i++)
            {
                File.WriteAllText(Path.Combine(outDir, $"client.{i + 2}.txt"), results[i].Output);
                if (!done.IsMatch(results[i].Output))
                    problems.Add($"client {i + 2} didn't finish: " +
                                 (results[i].TimedOut ? $"timed out after {timeout.TotalSeconds:0}s" : $"it ended with {results[i].ExitCode}, with no totals"));
                else
                    outputs.Add(results[i].Output);
            }

            bool ok = clientFile == null ? Lobby(outputs, connections, seconds) : Custom(outputs);
            ok &= TickHeld(serverOutput, reportedAt);
            foreach (string p in problems) Console.WriteLine("  " + p);
            Console.WriteLine($"  (each program's output: {Path.GetRelativePath(Environment.CurrentDirectory, outDir)})");

            ok &= problems.Count == 0;
            Console.WriteLine(ok ? "passed" : "FAILED");
            return ok ? 0 : 1;
        }

        /// <summary>The default client's totals: every connection logged in, answered in order and quit.</summary>
        static bool Lobby(List<string> outputs, int connections, int seconds)
        {
            long logged = 0, answered = 0, wrong = 0, quit = 0, latency = 0, most = 0, handshake = -1, refused = 0;
            foreach (string output in outputs)
            {
                Match m = Totals.Match(output);
                long N(int g) => long.Parse(m.Groups[g].Value);
                logged += N(2); answered += N(3); wrong += N(4); quit += N(5); latency += N(6);
                most = Math.Max(most, N(7));
                Match h = Handshakes.Match(output);
                if (h.Success)
                {
                    handshake = Math.Max(handshake, long.Parse(h.Groups[1].Value));
                    refused += long.Parse(h.Groups[2].Value);
                }
            }

            long expected = (long)connections * seconds;
            if (handshake >= 0)
                Console.WriteLine($"  TLS:        every handshake done in {handshake:N0} ms (the slowest machine's), {refused} refused");
            Console.WriteLine($"  logged in:  {logged} of {connections}");
            Console.WriteLine($"  answered:   {answered:N0} lines (about {expected:N0} expected), " +
                              (wrong == 0 ? "all in order" : $"{wrong} wrong or out of order"));
            if (answered > 0)
                Console.WriteLine($"  answers in: {(double)latency / answered:0.0} ms on average, {most} ms at most (in the kernel's ms)");
            Console.WriteLine($"  quit:       {quit} of {connections}");

            // a line takes a second each, so a run can be a line or so short per connection
            return logged == connections && quit == connections && wrong == 0 && refused == 0 && answered >= expected - 2L * connections;
        }

        /// <summary>Another client's "load: NAME COUNT ..." line, added up across the machines; passes when problems is 0.</summary>
        static bool Custom(List<string> outputs)
        {
            var totals = new List<(string Name, long Count)>();
            foreach (string output in outputs)
            {
                string[] words = Counts.Matches(output).Last().Groups[1].Value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                for (int w = 0; w + 1 < words.Length; w += 2)
                {
                    if (!long.TryParse(words[w + 1], out long count)) continue;
                    int at = totals.FindIndex(t => t.Name == words[w]);
                    if (at < 0) totals.Add((words[w], count));
                    else totals[at] = (words[w], words[w].StartsWith("most") ? Math.Max(totals[at].Count, count) : totals[at].Count + count);
                }
            }
            int width = totals.Count == 0 ? 0 : totals.Max(t => t.Name.Length) + 1;
            foreach (var (name, count) in totals)
                Console.WriteLine($"  {(name + ":").PadRight(width)} {count:N0}");
            int problems = totals.FindIndex(t => t.Name == "problems");
            if (problems < 0) Console.WriteLine("  the clients' load: lines have no problems count");
            return problems >= 0 && totals[problems].Count == 0;
        }

        /// <summary>
        /// The server's tick, if it says how it kept up: true when no tick was late once its
        /// players were all in (leaving out the first report, which has the start in it), and
        /// its clock kept up with this computer's over those reports. A world split into zones
        /// reports each (zone N tick: ...): each zone is looked at on its own, then together.
        /// </summary>
        /// <param name="reportedAt">when each report came, in ms by this computer's clock</param>
        static bool TickHeld(string serial, List<double> reportedAt)
        {
            var all = Ticks.Matches(serial).Select((m, i) => (Zone: m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0,
                                                              Ticks: long.Parse(m.Groups[2].Value), Ms: long.Parse(m.Groups[3].Value),
                                                              Late: long.Parse(m.Groups[4].Value), Longest: long.Parse(m.Groups[5].Value),
                                                              Players: long.Parse(m.Groups[6].Value),
                                                              At: i < reportedAt.Count ? reportedAt[i] : double.NaN)).ToList();
            if (all.Count == 0) return true;

            var counted = new List<(long Ticks, long Ms, long Late, long Longest, double Host)>();
            long players = 0;
            int zones = 0;
            foreach (var zone in all.GroupBy(r => r.Zone))
            {
                var reports = zone.ToList();
                long most = reports.Max(r => r.Players);
                players += most;
                zones++;
                // since this zone's report before, by this computer's clock (NaN if it isn't known)
                var timed = reports.Select((r, i) => (r.Ticks, r.Ms, r.Late, r.Longest, r.Players,
                                                      Host: i > 0 ? r.At - reports[i - 1].At : double.NaN)).ToList();
                var kept = timed.Skip(1).SkipWhile(r => r.Players < most).ToList();
                if (kept.Count == 0)
                {
                    Console.WriteLine($"  tick:       not measured: the run ended before a report with all {most} players in" +
                                      (zone.Key > 0 ? $" in zone {zone.Key}" : "") + " (try more --seconds)");
                    return false;
                }
                counted.AddRange(kept.Select(r => (r.Ticks, r.Ms, r.Late, r.Longest, r.Host)));
            }

            long ticks = counted.Sum(r => r.Ticks), ms = counted.Sum(r => r.Ms), late = counted.Sum(r => r.Late);
            Console.WriteLine($"  tick:       {ticks:N0} ticks in {ms / 1000.0:0.0} s ({ticks * 1000.0 / ms:0.0} a second" +
                              (zones > 1 ? $" in each of {zones} zones" : "") + $") with {players} players, " +
                              $"{late} late, longest {counted.Max(r => r.Longest)} ms");

            // a report's ms against the time it took to come here (give or take how often the output is read)
            var hosted = counted.Where(r => !double.IsNaN(r.Host)).ToList();
            double host = hosted.Sum(r => r.Host), guest = hosted.Sum(r => r.Ms);
            bool clockKept = hosted.All(r => r.Host <= r.Ms * 1.05 + 250);
            if (hosted.Count > 0)
                Console.WriteLine($"  its clock:  {guest / 1000.0:0.0} s of the server's time took {host / 1000.0:0.0} s here" +
                                  (clockKept ? "" : ": it fell behind, so its ms don't show how late the ticks really were " +
                                               $"(each report: {string.Join(", ", hosted.Select(r => $"{r.Ms / 1000.0:0.0} s in {r.Host / 1000.0:0.0}"))})"));
            return late == 0 && clockKept;
        }
    }
}
