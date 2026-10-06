using Phi.Cli;

const string Usage = """
    usage:
      phi check <file.phi>                 report errors without building
      phi build <file.phi> [-o <dir>]      compile to a bootable disk image
      phi run   <file.phi> [--debug]       build, then boot it in QEMU (serial output prints here)
      phi test  [dir|file.phi] [-f <name>] [--timeout <sec>]
                                           boot each test headless and compare its serial
                                           output with NAME.expected (default dir: tests/)
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

    try
    {
        switch (command)
        {
            case "build":
            case "run":
            {
                string? outDir = Option("-o");
                bool debug = Flag("--debug");
                if (rest.Count != 1) throw new ArgumentException($"{command} needs exactly one .phi file");

                string file = Path.GetFullPath(rest[0]);
                outDir ??= Path.Combine(Path.GetDirectoryName(file)!, "build", Path.GetFileNameWithoutExtension(file));

                BuildResult build = Builder.Build(file, outDir);
                foreach (string w in build.Warnings) Console.Error.WriteLine("warning: " + w);
                foreach (string e in build.Errors) Console.Error.WriteLine("error: " + e);
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

                return command == "run" ? Qemu.RunInteractive(build.ImagePath, debug) : 0;
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
                string target = rest.Count > 0 ? rest[0] : FindTestsDir();
                return TestRunner.Run(target, filter, TimeSpan.FromSeconds(timeout != null ? double.Parse(timeout) : 10));
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
