using Phi.Cli;

const string Usage = """
    usage:
      phi check <file.phi> [--target <system>]
                                           report errors without building
      phi build <file.phi> [-o <dir>] [--target <system>]
                                           compile to a bootable disk image; with --target linux or
                                           windows, a program (phi.Name:Program) becomes an ordinary
                                           executable for that system instead (NAME, or NAME.exe);
                                           linux64 and windows64 are 64-bit ones (not yet with atomic
                                           or locked, or assembly blocks without an x64 version)
      phi run   <file.phi> [--debug] [--keep-disk] [--memory <MB>] [--cores <n>] [--target <system>] [-- <arguments>]
                                           build, then boot it in QEMU (serial output prints here);
                                           with --target, run the executable here (a Linux one under
                                           WSL on Windows), with the arguments after --;
                                           --keep-disk keeps the files a kernel saved for next time
                                           (programs from the rootfs are updated on it),
                                           --memory gives the machine more (or less) than 128 MB, up to 1024;
                                           with NAME.machines next to it, it and the machines listed there
                                           boot together on one network, each in a window;
                                           --nic virtio gives the machine a virtio-net card (not an RTL8139),
                                           --disk virtio or nvme a virtio-blk or NVMe disk (not an IDE one)
      phi test  [dir|file.phi] [-f <name>] [--timeout <sec>] [--input <file>] [--contains <file>] [--keep-output]
                [--memory <MB>] [--cores <n>] [--target <system>] [--no-retry]
                                           boot each test headless and compare its serial
                                           output with NAME.expected (default dir: tests/);
                                           tests that can also run as Linux and Windows programs do,
                                           where this computer can run them; --target linux (or
                                           windows) runs only those, without QEMU;
                                           --input scripts the input instead of NAME.input,
                                           --contains checks for the lines of a file instead of NAME.expected,
                                           --keep-output saves NAME.actual even when it passes;
                                           a test that fails runs once more, and one that passes then is
                                           listed at the end (--no-retry: no second run); one that runs
                                           out of time says where each thread (or CPU) was
      phi test  --bench [-f <name>] [--save-bench] [--tolerance <percent>]
                                           run the benchmarks (tests/bench_NAME.phi) counting instructions,
                                           and fail one whose counts are more than the tolerance
                                           (default 2%) over the ones saved in NAME.bench;
                                           --save-bench saves the new counts instead
      phi profile <file.phi> [--input <file>] [--seconds <n>] [--top <n>] [--memory <MB>]
                                           boot it in QEMU, sampling where the CPU is, and list the PHI
                                           lines and methods that took the most time; with a window
                                           until you close it, or headless with --input (until the
                                           script ends) or --seconds
      phi load <file.phi> [--connections <n>] [--seconds <n>] [--port <n>] [--client <file.phi>] [--root <file.der> --host <name>] [--nic <card>] [--profile] [--target <system>]
                                           boot a server with load-client kernels on one network, which
                                           open n TCP connections to it (default 100, to port 4001), log
                                           each in, and have each say a line a second for the run (default
                                           30 s), checking every answer comes back in order (the mmo
                                           sample's lobby); --client uses another load-client kernel (such
                                           as samples/mmo/load_players.phi, n players in the mmo world);
                                           a server that reports its tick fails if a tick was late;
                                           --root and --host make each connection TLS, checking the server's
                                           certificate is for that host and goes back to that root (DER);
                                           --profile samples where the server's time goes;
                                           --target linux (or windows) runs a server program (such as
                                           samples/mmo/server.rootfs/world.phi) and the clients as
                                           programs on this computer instead
      phi serve <dir> [--https] [--port <n>]
                                           serve a folder over HTTP (or HTTPS) on this computer;
                                           a kernel in QEMU reaches it at 10.0.2.2
    """;

// how much over its baseline (in percent) a benchmark's count may be: -icount makes the counts
// almost the same every run, so a small change is a real one
const double DefaultTolerance = 2;

try
{
    return Execute(args);
}
catch (ToolNotFoundException e)
{
    Console.Error.WriteLine("error: " + e.Message);
    return 2;
}

static int Execute(string[] args)
{
    if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
    {
        Console.WriteLine(Usage);
        return args.Length == 0 ? 1 : 0;
    }

    string command = args[0];
    var rest = args.Skip(1).ToList();

    // phi run x.phi --target linux -- a b: what follows -- is the program's command line
    var programArguments = new List<string>();
    int dashes = rest.IndexOf("--");
    if (dashes >= 0)
    {
        programArguments = rest.Skip(dashes + 1).ToList();
        rest = rest.Take(dashes).ToList();
    }

    string? Option(string name)
    {
        int i = rest.IndexOf(name);
        if (i < 0) return null;
        if (i + 1 >= rest.Count) throw new ArgumentException($"{name} needs a value");
        string value = rest[i + 1];
        rest.RemoveRange(i, 2);
        return value;
    }

    bool Flag(string name) => rest.Remove(name);

    Phi.Compiler.Host TargetOption()
    {
        string? target = Option("--target");
        if (target == null) return Phi.Compiler.Host.PhiOS;
        return Phi.Compiler.Hosts.Parse(target)
               ?? throw new ArgumentException("--target takes the system: " + string.Join(", ", Phi.Compiler.Hosts.Names));
    }

    void MemoryOption()
    {
        string? memory = Option("--memory");
        if (memory == null) return;
        if (!int.TryParse(memory, out int mb) || mb < 32 || mb > Qemu.MaxMemoryMb)
            throw new ArgumentException($"--memory takes the machine's memory in MB, from 32 to {Qemu.MaxMemoryMb}");
        Qemu.MemoryMb = mb;
    }

    void CoresOption()
    {
        string? cores = Option("--cores");
        if (cores == null) return;
        if (!int.TryParse(cores, out int n) || n < 1 || n > 8)
            throw new ArgumentException("--cores takes how many cores the machine has, from 1 to 8");
        Qemu.Cores = n;
    }

    void NicOption()
    {
        string? nic = Option("--nic");
        if (nic == null) return;
        if (!Qemu.NetworkCards.Contains(nic))
            throw new ArgumentException("--nic takes the network card: " + string.Join(" or ", Qemu.NetworkCards));
        Qemu.NetworkCard = nic;
        TestRunner.NicChosen = true;
    }

    void DiskOption()
    {
        string? disk = Option("--disk");
        if (disk == null) return;
        if (!Qemu.DiskKinds.Contains(disk))
            throw new ArgumentException("--disk takes the kind of disk: " + string.Join(" or ", Qemu.DiskKinds));
        Qemu.DiskKind = disk;
        TestRunner.DiskChosen = true;
    }

    try
    {
        switch (command)
        {
            case "build":
            case "run":
            {
                string? outDir = Option("-o");
                bool debug = Flag("--debug");
                bool keepDisk = Flag("--keep-disk");
                Phi.Compiler.Host host = TargetOption();
                MemoryOption();
                CoresOption();
                NicOption();
                DiskOption();
                if (rest.Count != 1) throw new ArgumentException($"{command} needs exactly one .phi file");
                if (programArguments.Count > 0 && host == Phi.Compiler.Host.PhiOS)
                    throw new ArgumentException("arguments after -- are for a program built with --target linux or windows");

                string file = Path.GetFullPath(rest[0]);
                outDir ??= Path.Combine(Builder.BuildHome(file), "build", Path.GetFileNameWithoutExtension(file));

                if (host != Phi.Compiler.Host.PhiOS)
                {
                    BuildResult hosted = Builder.Build(file, outDir, host: host);
                    foreach (string w in hosted.Warnings) Console.Error.WriteLine(w.Contains(": warning:") ? w : "warning: " + w);
                    foreach (string e in hosted.Errors) Console.Error.WriteLine(e.Contains(": error:") ? e : "error: " + e);
                    if (!hosted.Success) return 1;
                    if (command != "run")
                    {
                        Console.WriteLine($"built the {Phi.Compiler.Hosts.Name(host)} program {Path.GetRelativePath(Environment.CurrentDirectory, hosted.ExecutablePath!)}");
                        return 0;
                    }
                    if (!HostRunner.CanRun(host, out string why)) throw new ArgumentException(why);
                    return HostRunner.RunInteractive(hosted.ExecutablePath!, host, programArguments);
                }

                // NAME.machines: the program and the machines it lists, on one network
                if (NetworkMachines.ManifestFor(file) is string manifest)
                    return RunMachines(file, manifest, command == "run");

                BuildResult build = Builder.Build(file, outDir);
                // compiler diagnostics already say "file:line:col: error:"; other problems get the prefix
                foreach (string w in build.Warnings) Console.Error.WriteLine(w.Contains(": warning:") ? w : "warning: " + w);
                foreach (string e in build.Errors) Console.Error.WriteLine(e.Contains(": error:") ? e : "error: " + e);
                if (!build.Success) return 1;

                if (build.IsProgram)
                {
                    Console.WriteLine($"built the program {Path.GetRelativePath(Environment.CurrentDirectory, build.ProgramPath)} " +
                                      "(programs run inside a kernel: put the .phi file in a kernel's rootfs folder, " +
                                      "or build it with --target linux or windows)");
                    if (command == "run")
                    {
                        Console.Error.WriteLine("error: a user program can't boot by itself; run a kernel whose rootfs has it, " +
                                                "or run it here with --target linux or windows");
                        return 1;
                    }
                    return 0;
                }

                Console.WriteLine($"built {Path.GetRelativePath(Environment.CurrentDirectory, build.ImagePath)}");

                if (command != "run") return 0;

                string image = build.ImagePath;
                if (keepDisk)
                {
                    var refreshed = new List<string>();
                    var diskErrors = new List<string>();
                    image = Builder.KeepDisk(build.ImagePath, out bool fresh, refreshed, diskErrors);
                    foreach (string e in diskErrors) Console.Error.WriteLine("error: " + e);
                    if (diskErrors.Count > 0) return 1;
                    Console.WriteLine(fresh
                        ? $"made a disk that keeps its files between runs: {Path.GetRelativePath(Environment.CurrentDirectory, image)}"
                        : $"booting the kept disk {Path.GetRelativePath(Environment.CurrentDirectory, image)} with the new kernel (delete it to start again)");
                    if (refreshed.Count > 0) Console.WriteLine("updated programs on it: " + string.Join(" ", refreshed));
                }
                else
                {
                    // a fresh disk every run: say so when the kernel saved something on it
                    byte[] before = HashFile(image);
                    int code = Qemu.RunInteractive(image, debug);
                    if (!HashFile(image).SequenceEqual(before))
                        Console.WriteLine("note: the kernel changed its disk, and the next phi run starts from a fresh one; " +
                                          "use phi run --keep-disk to keep what it saved");
                    return code;
                }
                return Qemu.RunInteractive(image, debug);
            }

            case "check":
            {
                Phi.Compiler.Host host = TargetOption();
                if (rest.Count != 1) throw new ArgumentException("check needs exactly one .phi file");
                if (!File.Exists(rest[0])) throw new ArgumentException($"file not found: {rest[0]}");

                var compiled = Phi.Compiler.PhiCompiler.Compile(Phi.Compiler.SourceFile.Load(rest[0]), generate: false, host: host);
                foreach (var d in compiled.Diagnostics) Console.Error.WriteLine(d);
                if (compiled.Success) Console.WriteLine($"{rest[0]}: no errors");
                return compiled.Success ? 0 : 1;
            }

            case "test":
            {
                string? filter = Option("-f");
                string? timeout = Option("--timeout");
                string? input = Option("--input");
                string? contains = Option("--contains");
                bool keepOutput = Flag("--keep-output");
                TestRunner.Retry = !Flag("--no-retry");
                bool saveBench = Flag("--save-bench");
                bool bench = Flag("--bench") || saveBench;
                string? tolerance = Option("--tolerance");
                string? onlyTarget = Option("--target");
                MemoryOption();
                CoresOption();
                NicOption();
                DiskOption();
                if (input != null && !File.Exists(input)) throw new ArgumentException($"file not found: {input}");
                if (contains != null && !File.Exists(contains)) throw new ArgumentException($"file not found: {contains}");
                double percent = DefaultTolerance;
                if (tolerance != null && (!double.TryParse(tolerance, out percent) || percent < 0))
                    throw new ArgumentException("--tolerance takes a percentage, such as 5");
                if (onlyTarget != null)
                {
                    TestRunner.Only = Phi.Compiler.Hosts.Parse(onlyTarget)
                                      ?? throw new ArgumentException("--target takes the system: " + string.Join(", ", Phi.Compiler.Hosts.Names));
                    if (TestRunner.Only != Phi.Compiler.Host.PhiOS && !HostRunner.CanRun(TestRunner.Only.Value, out string why))
                        throw new ArgumentException(why);
                }
                string target = rest.Count > 0 ? rest[0] : FindTestsDir();
                // with no folder given, the samples that have tests run too
                List<string>? samples = rest.Count > 0 ? null
                    : TestRunner.SampleTests(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(target))!, "samples"));
                return TestRunner.Run(target, filter, TimeSpan.FromSeconds(timeout != null ? double.Parse(timeout) : 10),
                                      input != null ? Path.GetFullPath(input) : null, keepOutput,
                                      contains != null ? Path.GetFullPath(contains) : null,
                                      bench ? new BenchOptions(saveBench, percent) : null, samples);
            }

            case "profile":
            {
                string? input = Option("--input");
                string? seconds = Option("--seconds");
                string? top = Option("--top");
                MemoryOption();
                CoresOption();
                NicOption();
                DiskOption();
                if (rest.Count != 1) throw new ArgumentException("profile needs exactly one .phi file");
                if (input != null && !File.Exists(input)) throw new ArgumentException($"file not found: {input}");
                double? time = null;
                if (seconds != null)
                {
                    if (!double.TryParse(seconds, out double s) || s <= 0) throw new ArgumentException("--seconds takes a number of seconds");
                    time = s;
                }
                int lines = 15;
                if (top != null && (!int.TryParse(top, out lines) || lines < 1)) throw new ArgumentException("--top takes a number of lines");
                return Profiler.Run(rest[0], input != null ? Path.GetFullPath(input) : null, time, lines);
            }

            case "load":
            {
                string? count = Option("--connections");
                string? seconds = Option("--seconds");
                string? port = Option("--port");
                string? client = Option("--client");
                string? root = Option("--root");
                string? host = Option("--host");
                string? onLoad = Option("--target");
                bool profile = Flag("--profile");
                MemoryOption();
                CoresOption();
                NicOption();
                DiskOption();
                if (rest.Count != 1) throw new ArgumentException("load needs exactly one .phi file");
                if (client != null && !File.Exists(client)) throw new ArgumentException($"file not found: {client}");
                int n = 100, s = 30, p = 4001;
                if (count != null && (!int.TryParse(count, out n) || n < 1 || n > 1000))
                    throw new ArgumentException("--connections takes a number from 1 to 1000");
                if (seconds != null && (!int.TryParse(seconds, out s) || s < 1))
                    throw new ArgumentException("--seconds takes a number of seconds");
                if (port != null && (!int.TryParse(port, out p) || p < 1 || p > 65535))
                    throw new ArgumentException("--port takes the server's TCP port");
                if (root != null && !File.Exists(root)) throw new ArgumentException($"file not found: {root}");
                if ((root == null) != (host == null)) throw new ArgumentException("--root and --host go together: the root to trust, and the name the server's certificate is for");
                Phi.Compiler.Host target = Phi.Compiler.Host.PhiOS;
                if (onLoad != null)
                    target = Phi.Compiler.Hosts.Parse(onLoad) ?? throw new ArgumentException("--target takes the system: " + string.Join(", ", Phi.Compiler.Hosts.Names));
                if (profile && target != Phi.Compiler.Host.PhiOS) throw new ArgumentException("--profile samples a kernel in QEMU, so it goes without --target");
                return LoadTester.Run(rest[0], n, s, p, profile, client, root, host, target);
            }

            case "serve":
            {
                bool https = Flag("--https");
                string? port = Option("--port");
                if (rest.Count != 1) throw new ArgumentException("serve needs exactly one folder");
                if (!Directory.Exists(rest[0])) throw new ArgumentException($"folder not found: {rest[0]}");

                using var server = new WebServer(rest[0], https, port != null ? int.Parse(port) : 0);
                string scheme = https ? "https" : "http";
                Console.WriteLine($"serving {rest[0]} at {scheme}://localhost:{server.Port}/ " +
                                  $"(from QEMU: {scheme}://10.0.2.2:{server.Port}/); press Enter to stop");
                Console.ReadLine();
                return 0;
            }

            default:
                throw new ArgumentException($"unknown command '{command}'");
        }
    }
    catch (ArgumentException e)
    {
        Console.Error.WriteLine("error: " + e.Message);
        Console.Error.WriteLine(Usage);
        return 1;
    }
}

// builds a program with NAME.machines and the machines it lists; with run, boots them all on
// one network, each in a window of its own (machine 1's serial output comes here, the others'
// goes to serial.txt in their build folders)
static int RunMachines(string file, string manifest, bool run)
{
    var errors = new List<string>();
    var warnings = new List<string>();
    List<NetworkMachines.Machine>? machines = NetworkMachines.Build(file, manifest, "", errors, warnings);
    foreach (string w in warnings) Console.Error.WriteLine(w.Contains(": warning:") ? w : "warning: " + w);
    foreach (string e in errors) Console.Error.WriteLine(e.Contains(": error:") ? e : "error: " + e);
    if (machines == null) return 1;
    foreach (var m in machines)
        Console.WriteLine($"built machine {m.Number}: {Path.GetRelativePath(Environment.CurrentDirectory, m.Image)}");
    if (!run) return 0;

    using var network = new VirtualNetwork(NetworkMachines.LoseEvery(manifest), NetworkMachines.Card(manifest),
                                           NetworkMachines.Mtu(manifest), NetworkMachines.Dhcp(manifest));
    var windows = machines.Select(m =>
    {
        string? serial = m.Number == 1 ? null : Path.Combine(Path.GetDirectoryName(m.Image)!, "serial.txt");
        if (serial != null) Console.WriteLine($"machine {m.Number}'s output: {Path.GetRelativePath(Environment.CurrentDirectory, serial)}");
        return Task.Run(() => Qemu.RunInteractive(m.Image, debug: false, network: network.MachineArgs(m.Number), serialFile: serial));
    }).ToArray();
    Task.WaitAll(windows);
    return windows[0].Result;
}

// look for tests/ in the current directory or any parent, so `phi test` works anywhere in the repo
static string FindTestsDir()
{
    for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir != null; dir = dir.Parent)
    {
        string candidate = Path.Combine(dir.FullName, "tests");
        if (Directory.Exists(candidate)) return candidate;
    }
    return "tests";
}

static byte[] HashFile(string path)
{
    using var stream = File.OpenRead(path);
    return System.Security.Cryptography.SHA256.HashData(stream);
}
