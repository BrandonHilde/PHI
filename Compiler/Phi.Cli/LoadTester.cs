using System.Text.RegularExpressions;

namespace Phi.Cli
{
    /// <summary>
    /// phi load: boots a server (machine 1, as Machine.address) on a VirtualNetwork with as
    /// many load-client kernels (lib/phi/test/load.phi) as it takes to open the connections,
    /// up to PerMachine each. Each client logs its connections in to the server's port
    /// ("login NAME PASSWORD", answered "welcome NAME": the mmo sample's lobby), has each
    /// say a numbered line once a second for the run ("say ...", answered "said ..."),
    /// checking that every answer comes back in order, and prints its totals, which this
    /// adds up. The clients are PHI kernels rather than connections from this computer:
    /// QEMU's user network on Windows can only keep 64 connections from outside.
    /// </summary>
    public static class LoadTester
    {
        const int PerMachine = 100;
        static readonly Regex Totals = new(@"load: connections (\d+) logged (\d+) answered (\d+) wrong (\d+) quit (\d+) latency (\d+) most (\d+)");

        /// <param name="profile">sample where the server's CPU is, as phi profile does, and report it</param>
        public static int Run(string phiFile, int connections, int seconds, int port, bool profile = false)
        {
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

            // each client: a stub that uses the load client, told who it is and what to do
            string stub = Path.Combine(outDir, "client.phi");
            File.WriteAllText(stub, "# made by phi load\nuse test.load;\n");
            var images = new List<string> { server.ImagePath };
            for (int c = 0; c < clients && errors.Count == 0; c++)
            {
                int number = c + 2;
                int share = Math.Min(PerMachine, connections - c * PerMachine);
                string settings = $"\nphi.LoadSettings:Library\n{{\n\tu32 connections: {share};\n\tu32 seconds: {seconds};\n\tu32 port: {port};\n}}\n";
                BuildResult client = Builder.Build(stub, Path.Combine(outDir, $"client.{number}"),
                    NetworkMachines.MachineClass(number, machines, 0x0A000300u + (uint)number, 0x0A000301) + settings);
                warnings.AddRange(client.Warnings.Where(w => !warnings.Contains(w)));
                errors.AddRange(client.Errors);
                images.Add(client.ImagePath);
            }
            foreach (string w in warnings.Distinct()) Console.Error.WriteLine(w.Contains(": warning:") ? w : "warning: " + w);
            foreach (string e in errors) Console.Error.WriteLine(e.Contains(": error:") ? e : "error: " + e);
            if (errors.Count > 0) return 1;
            if (server.IsProgram)
            {
                Console.Error.WriteLine("error: phi load needs a kernel, not a user program");
                return 1;
            }

            Console.WriteLine($"phi load: {connections} connections to {Path.GetRelativePath(Environment.CurrentDirectory, file)} " +
                              $"(port {port}) from {clients} machines, for {seconds} s");

            using var network = new VirtualNetwork(card: Qemu.NetworkCard);
            int? profilePort = profile ? Qemu.FreePort() : null;
            using var stopSampling = new CancellationTokenSource();
            Task<List<Profiler.Sample>>? sampler = profilePort is int pp ? Task.Run(() => Profiler.Collect(pp, stopSampling.Token)) : null;

            int clientsDone = 0;
            TimeSpan timeout = TimeSpan.FromSeconds(seconds + 240);
            var runs = images.Select((image, i) => Task.Run(() => Qemu.RunHeadless(image, timeout,
                i == 0
                    ? _ => Volatile.Read(ref clientsDone) == clients                  // the server: until the clients are done
                    : output =>
                    {
                        if (!Totals.IsMatch(output)) return false;
                        Interlocked.Increment(ref clientsDone);
                        return true;
                    },
                network: network.MachineArgs(i + 1), profilePort: i == 0 ? profilePort : null))).ToArray();
            Task.WaitAll(runs);
            stopSampling.Cancel();

            File.WriteAllText(Path.Combine(outDir, "server.txt"), runs[0].Result.Serial);
            long count = 0, logged = 0, answered = 0, wrong = 0, quit = 0, latency = 0, most = 0;
            var problems = new List<string>();
            for (int i = 1; i < runs.Length; i++)
            {
                HeadlessResult run = runs[i].Result;
                File.WriteAllText(Path.Combine(outDir, $"client.{i + 1}.txt"), run.Serial);
                Match m = Totals.Match(run.Serial);
                if (!m.Success)
                {
                    problems.Add($"machine {i + 1} didn't finish: {run.Explain(timeout, "no totals")}");
                    continue;
                }
                long N(int g) => long.Parse(m.Groups[g].Value);
                count += N(1); logged += N(2); answered += N(3); wrong += N(4); quit += N(5); latency += N(6);
                most = Math.Max(most, N(7));
            }

            if (sampler != null && sampler.Wait(TimeSpan.FromSeconds(5)))
            {
                var maps = new[] { Path.Combine(outDir, "server", "kernel.lst") }.Where(File.Exists).Select(AddressMap.Load).ToList();
                Console.Write(Profiler.Report(sampler.Result, maps, 12, seconds));
                Console.WriteLine();
            }

            long expected = (long)connections * seconds;
            Console.WriteLine($"  logged in:  {logged} of {connections}");
            Console.WriteLine($"  answered:   {answered:N0} lines (about {expected:N0} expected), " +
                              (wrong == 0 ? "all in order" : $"{wrong} wrong or out of order"));
            if (answered > 0)
                Console.WriteLine($"  answers in: {(double)latency / answered:0.0} ms on average, {most} ms at most (in the kernel's ms)");
            Console.WriteLine($"  quit:       {quit} of {connections}");
            Console.WriteLine($"  network:    {network.Frames:N0} frames");
            foreach (string p in problems) Console.WriteLine("  " + p);
            Console.WriteLine($"  (each machine's output: {Path.GetRelativePath(Environment.CurrentDirectory, outDir)})");

            // a line takes a second each, so a run can be a line or so short per connection
            bool ok = problems.Count == 0 && logged == connections && quit == connections && wrong == 0 && answered >= expected - 2L * connections;
            Console.WriteLine(ok ? "passed" : "FAILED");
            return ok ? 0 : 1;
        }
    }
}
