using Phi.Cli;

const string Usage = """
    usage:
      phi check <file.phi>                 report errors without building
      phi build <file.phi> [-o <dir>]      compile to a bootable disk image
      phi run   <file.phi> [--debug] [--keep-disk] [--memory <MB>]
                                           build, then boot it in QEMU (serial output prints here);
                                           --keep-disk keeps the files a kernel saved for next time
                                           (programs from the rootfs are updated on it),
                                           --memory gives the machine more (or less) than 128 MB, up to 1024
      phi test  [dir|file.phi] [-f <name>] [--timeout <sec>] [--input <file>] [--contains <file>] [--keep-output]
                [--memory <MB>]
                                           boot each test headless and compare its serial
                                           output with NAME.expected (default dir: tests/);
                                           --input scripts the input instead of NAME.input,
                                           --contains checks for the lines of a file instead of NAME.expected,
                                           --keep-output saves NAME.actual even when it passes
      phi serve <dir> [--https] [--port <n>]
                                           serve a folder over HTTP (or HTTPS) on this computer;
                                           a kernel in QEMU reaches it at 10.0.2.2
    """;

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

    void MemoryOption()
    {
        string? memory = Option("--memory");
        if (memory == null) return;
        if (!int.TryParse(memory, out int mb) || mb < 32 || mb > Qemu.MaxMemoryMb)
            throw new ArgumentException($"--memory takes the machine's memory in MB, from 32 to {Qemu.MaxMemoryMb}");
        Qemu.MemoryMb = mb;
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
                MemoryOption();
                if (rest.Count != 1) throw new ArgumentException($"{command} needs exactly one .phi file");

                string file = Path.GetFullPath(rest[0]);
                outDir ??= Path.Combine(Path.GetDirectoryName(file)!, "build", Path.GetFileNameWithoutExtension(file));

                BuildResult build = Builder.Build(file, outDir);
                // compiler diagnostics already say "file:line:col: error:"; other problems get the prefix
                foreach (string w in build.Warnings) Console.Error.WriteLine(w.Contains(": warning:") ? w : "warning: " + w);
                foreach (string e in build.Errors) Console.Error.WriteLine(e.Contains(": error:") ? e : "error: " + e);
                if (!build.Success) return 1;

                if (build.IsProgram)
                {
                    Console.WriteLine($"built the program {Path.GetRelativePath(Environment.CurrentDirectory, build.ProgramPath)} " +
                                      "(programs run inside a kernel: put the .phi file in a kernel's rootfs folder)");
                    if (command == "run")
                    {
                        Console.Error.WriteLine("error: a user program can't boot by itself; run a kernel whose rootfs has it");
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
                if (rest.Count != 1) throw new ArgumentException("check needs exactly one .phi file");
                if (!File.Exists(rest[0])) throw new ArgumentException($"file not found: {rest[0]}");

                var compiled = Phi.Compiler.PhiCompiler.Compile(Phi.Compiler.SourceFile.Load(rest[0]), generate: false);
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
                MemoryOption();
                if (input != null && !File.Exists(input)) throw new ArgumentException($"file not found: {input}");
                if (contains != null && !File.Exists(contains)) throw new ArgumentException($"file not found: {contains}");
                string target = rest.Count > 0 ? rest[0] : FindTestsDir();
                return TestRunner.Run(target, filter, TimeSpan.FromSeconds(timeout != null ? double.Parse(timeout) : 10),
                                      input != null ? Path.GetFullPath(input) : null, keepOutput,
                                      contains != null ? Path.GetFullPath(contains) : null);
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
