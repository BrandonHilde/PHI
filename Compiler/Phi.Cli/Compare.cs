using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Phi.Compiler;

namespace Phi.Cli
{
    /// <summary>
    /// phi compare: how fast PHI's code is beside C's and C#'s. A benchmark is one program written
    /// three times in a folder (bench/ by default): NAME.phi, NAME.c and NAME.cs, each doing the
    /// same work the same way and printing the same line. Each is built as a program for this
    /// computer (PHI in 64 and 32 bits, C with and without its optimizer, C# as a Release build),
    /// run several times, and the best time of each kept: the whole program's, from being started
    /// to having ended, so a benchmark named startup, which does nothing, shows what part of every
    /// other time is the language starting up. A program that prints something else than PHI's
    /// does isn't doing the same work, and the comparison fails.
    ///
    /// A real program can be timed the same way though it's only in PHI (its C and C# columns are
    /// left out): what follows -- is given to each program as its arguments, and --varies says
    /// its output isn't the same from run to run (it prints its own timings, say), so it's
    /// shown and not compared.
    /// </summary>
    public static class Compare
    {
        /// <summary>One way a benchmark is built: a column of the table.</summary>
        sealed record Entry(string Name, Func<string, string, List<string>, string[]?> Build);

        /// <summary>A time under this (in seconds) is the program starting, not its work: no ratio is made of it.</summary>
        const double TooShort = 0.010;

        public static int Run(string dir, string? filter, int runs, string? save, IReadOnlyList<string>? arguments = null, bool varies = false)
        {
            if (!Directory.Exists(dir))
            {
                Console.Error.WriteLine($"error: there is no folder {dir} (phi compare takes the folder the benchmarks are in)");
                return 2;
            }
            List<string> names = Directory.GetFiles(dir, "*.phi")
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .Where(n => filter == null || n.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n == "startup" ? "" : n, StringComparer.Ordinal).ToList();
            if (names.Count == 0)
            {
                Console.Error.WriteLine(filter == null ? $"error: no .phi files in {dir}" : $"error: no benchmark in {dir} has '{filter}' in its name");
                return 2;
            }

            string outDir = Path.Combine(dir, "build");
            var notes = new List<string>();
            List<Entry> entries = Entries(dir, outDir, names, notes);

            // build everything first, so nothing is compiling while something is timed
            var commands = new Dictionary<(string Bench, string Entry), string[]>();
            var errors = new List<string>();
            foreach (string name in names)
            {
                Console.Error.Write($"\rbuilding {name}...            ");
                foreach (Entry e in entries)
                {
                    string[]? command = e.Build(name, dir, errors);
                    if (command != null) commands[(name, e.Name)] = command;
                }
            }
            Console.Error.Write("\r                                        \r");
            if (errors.Count > 0)
            {
                foreach (string error in errors) Console.Error.WriteLine(error);
                return 1;
            }

            var times = new Dictionary<(string, string), double>();
            var different = new List<string>();
            var printed = new List<string>();
            foreach (string name in names)
            {
                string? expected = null;
                foreach (Entry e in entries)
                {
                    if (!commands.TryGetValue((name, e.Name), out string[]? command)) continue;
                    if (arguments != null && arguments.Count > 0) command = command.Concat(arguments).ToArray();
                    Console.Error.Write($"\rrunning {name}, {e.Name}...            ");
                    double best = double.MaxValue;
                    string output = "";
                    for (int i = 0; i < runs; i++)
                    {
                        var (seconds, text, code) = Time(command, dir);
                        if (code != 0)
                        {
                            Console.Error.WriteLine($"\rerror: {name} ({e.Name}) ended with code {code}:\n{text}");
                            return 1;
                        }
                        best = Math.Min(best, seconds);
                        output = text;
                    }
                    times[(name, e.Name)] = best;
                    if (varies)
                    {
                        // its own words about its last run, for the first way it's built
                        if (expected == null && output.Length > 0)
                            printed.Add($"What {name} ({e.Name}) printed, the last time it ran:\n\n" + string.Join("\n", output.Split('\n').Select(l => "  " + l)) + "\n");
                        expected ??= output;
                        continue;
                    }
                    expected ??= output;        // PHI's comes first
                    if (output != expected)
                        different.Add($"{name}: {e.Name} printed '{Short(output)}' where {entries[0].Name} printed '{Short(expected)}'");
                }
            }
            Console.Error.Write("\r                                                  \r");

            if (arguments != null && arguments.Count > 0) notes.Add("Each program was given the arguments: " + string.Join(" ", arguments));
            string report = Report(names, entries.Select(e => e.Name).Where(e => names.Any(n => times.ContainsKey((n, e)))).ToList(), times, runs, notes);
            if (printed.Count > 0) report += "\n" + string.Join("\n", printed);
            Console.Write(report);
            if (save != null)
            {
                File.WriteAllText(save, Markdown(report, dir));
                Console.WriteLine($"saved to {save}");
            }
            foreach (string line in different) Console.Error.WriteLine("error: " + line);
            return different.Count > 0 ? 1 : 0;
        }

        // ---------------------------------------------------------------- what's compared

        static List<Entry> Entries(string dir, string outDir, List<string> names, List<string> notes)
        {
            var entries = new List<Entry>();
            bool windows = OperatingSystem.IsWindows();

            entries.Add(new Entry("PHI 64-bit", (name, _, errors) => BuildPhi(name, dir, outDir, windows ? Host.Windows64 : Host.Linux64, errors)));
            entries.Add(new Entry("PHI 32-bit", (name, _, errors) => BuildPhi(name, dir, outDir, windows ? Host.Windows : Host.Linux, errors)));

            string? cc = FindC();
            if (cc == null) notes.Add("No C compiler was found (gcc, clang or cc on the PATH, or PHI_CC), so C isn't here.");
            else
            {
                string label = Path.GetFileNameWithoutExtension(cc);
                foreach (string level in new[] { "-O2", "-O0" })
                    entries.Add(new Entry($"{label} {level}", (name, _, errors) => BuildC(cc, level, name, dir, outDir, errors)));
                notes.Add("C: " + FirstLine(cc, "--version"));
            }

            string? dotnet = FindOnPath("dotnet");
            if (!names.Any(n => File.Exists(Path.Combine(dir, n + ".cs")))) { }
            else if (dotnet == null) notes.Add("dotnet wasn't found on the PATH, so C# isn't here.");
            else
            {
                // every benchmark's C# goes into one program, built once, which is told which to run
                string? program = null;
                bool built = false;
                entries.Add(new Entry("C#", (name, _, errors) =>
                {
                    if (!File.Exists(Path.Combine(dir, name + ".cs"))) return null;
                    if (!built)
                    {
                        built = true;
                        program = BuildCSharp(dotnet, names.Where(n => File.Exists(Path.Combine(dir, n + ".cs"))).ToList(), dir, outDir, errors);
                    }
                    return program == null ? null : new[] { program, name };
                }));
                notes.Add($"C#: .NET {FirstLine(dotnet, "--version")}, a Release build, started as any program is (its time has the runtime starting and the JIT in it)");
            }
            return entries;
        }

        static string[]? BuildPhi(string name, string dir, string outDir, Host host, List<string> errors)
        {
            if (!HostRunner.CanRun(host, out _)) return null;
            BuildResult result = Builder.Build(Path.Combine(dir, name + ".phi"), Path.Combine(outDir, host.Is64() ? "phi64" : "phi32", name), host: host);
            if (!result.Success || result.ExecutablePath == null)
            {
                errors.Add($"error: {name}.phi didn't build as a {host.Name()} program:");
                errors.AddRange(result.Errors.Select(e => "  " + e));
                return null;
            }
            return new[] { Path.GetFullPath(result.ExecutablePath) };
        }

        static string[]? BuildC(string cc, string level, string name, string dir, string outDir, List<string> errors)
        {
            string source = Path.Combine(dir, name + ".c");
            if (!File.Exists(source)) return null;
            string folder = Path.Combine(outDir, "c" + level);
            Directory.CreateDirectory(folder);
            string exe = Path.GetFullPath(Path.Combine(folder, name + (OperatingSystem.IsWindows() ? ".exe" : "")));
            var (code, output) = Toolchain.Run(cc, new[] { level, "-o", exe, source });
            if (code != 0)
            {
                errors.Add($"error: {name}.c didn't build ({Path.GetFileName(cc)} {level}):\n{output}");
                return null;
            }
            return new[] { exe };
        }

        static string? BuildCSharp(string dotnet, List<string> names, string dir, string outDir, List<string> errors)
        {
            string folder = Path.Combine(outDir, "cs");
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            Directory.CreateDirectory(folder);

            var start = new StringBuilder();
            start.AppendLine("// written by phi compare: runs the benchmark it's given the name of");
            start.AppendLine("static class PhiCompareStart\n{\n    static int Main(string[] args)\n    {\n        switch (args.Length > 0 ? args[0] : \"\")\n        {");
            foreach (string name in names)
            {
                string text = File.ReadAllText(Path.Combine(dir, name + ".cs"));
                // the class that has Main: the one named as the file is, or the first in the file
                List<string> classes = Regex.Matches(text, @"\bclass\s+(\w+)").Select(m => m.Groups[1].Value).ToList();
                string? cls = classes.FirstOrDefault(c => c.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? classes.FirstOrDefault();
                if (cls == null || !Regex.IsMatch(text, @"\bpublic\s+static\s+void\s+Main\s*\(\s*\)"))
                {
                    errors.Add($"error: {name}.cs needs a public class with `public static void Main()` (with no parameters), which phi compare calls: " +
                               "the class named as the file is, or the first in the file");
                    return null;
                }
                File.WriteAllText(Path.Combine(folder, name + ".cs"), text);
                start.AppendLine($"            case \"{name}\": {cls}.Main(); return 0;");
            }
            start.AppendLine("        }\n        return 2;\n    }\n}");
            File.WriteAllText(Path.Combine(folder, "PhiCompareStart.cs"), start.ToString());
            File.WriteAllText(Path.Combine(folder, "compare.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net{Environment.Version.Major}.0</TargetFramework>
                    <AssemblyName>compare</AssemblyName>
                    <StartupObject>PhiCompareStart</StartupObject>
                    <Nullable>disable</Nullable>
                    <ImplicitUsings>disable</ImplicitUsings>
                    <InvariantGlobalization>true</InvariantGlobalization>
                    <GenerateDocumentationFile>false</GenerateDocumentationFile>
                  </PropertyGroup>
                </Project>

                """);
            // (a folder of its own, so no Directory.Build.props or solution above it takes part)
            File.WriteAllText(Path.Combine(folder, "Directory.Build.props"), "<Project />\n");
            File.WriteAllText(Path.Combine(folder, "Directory.Build.targets"), "<Project />\n");

            var (code, output) = Toolchain.Run(dotnet, new[] { "build", "compare.csproj", "-c", "Release", "-o", "out", "--nologo", "-v", "quiet" }, folder);
            string exe = Path.GetFullPath(Path.Combine(folder, "out", "compare" + (OperatingSystem.IsWindows() ? ".exe" : "")));
            if (code != 0 || !File.Exists(exe))
            {
                errors.Add($"error: the C# benchmarks didn't build (dotnet build, in {folder}):\n{output}");
                return null;
            }
            return exe;
        }

        static string? FindC()
        {
            string? given = Environment.GetEnvironmentVariable("PHI_CC");
            if (!string.IsNullOrWhiteSpace(given)) return given;
            return FindOnPath("gcc") ?? FindOnPath("clang") ?? FindOnPath("cc");
        }

        static string? FindOnPath(string name)
        {
            string exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string candidate = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        static string FirstLine(string exe, string argument)
        {
            try
            {
                return Toolchain.Run(exe, new[] { argument }).Output.Split('\n')[0].Trim();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return Path.GetFileName(exe);
            }
        }

        // ---------------------------------------------------------------- timing

        /// <summary>One run of a program: how long it took from being started to having ended, what it printed, and its exit code.</summary>
        static (double Seconds, string Output, int Code) Time(string[] command, string folder)
        {
            var psi = new ProcessStartInfo(command[0])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = folder,
            };
            foreach (string a in command.Skip(1)) psi.ArgumentList.Add(a);
            var watch = Stopwatch.StartNew();
            using Process p = Process.Start(psi)!;
            Task<string> stdout = p.StandardOutput.ReadToEndAsync();
            Task<string> stderr = p.StandardError.ReadToEndAsync();
            p.WaitForExit();
            watch.Stop();
            string text = stdout.Result.Replace("\r", "").Trim();
            return (watch.Elapsed.TotalSeconds, p.ExitCode == 0 ? text : (text + "\n" + stderr.Result).Trim(), p.ExitCode);
        }

        static string Short(string text) => text.Length > 60 ? text[..60] + "..." : text;

        // ---------------------------------------------------------------- the report

        static string Report(List<string> names, List<string> columns, Dictionary<(string, string), double> times, int runs, List<string> notes)
        {
            var c = CultureInfo.InvariantCulture;
            var text = new StringBuilder();
            int first = Math.Max(names.Max(n => n.Length), 9) + 2;
            int width = Math.Max(columns.Max(n => n.Length), 7) + 2;

            text.AppendLine($"Seconds, the best of {runs} runs of each program, from being started to having ended:");
            text.AppendLine();
            text.Append("".PadRight(first));
            foreach (string column in columns) text.Append(column.PadLeft(width));
            text.AppendLine();
            foreach (string name in names)
            {
                text.Append(name.PadRight(first));
                foreach (string column in columns)
                    text.Append((times.TryGetValue((name, column), out double t) ? t.ToString("0.000", c) : "-").PadLeft(width));
                text.AppendLine();
            }

            // PHI against each of the others
            string phi = columns[0];
            List<string> others = columns.Skip(1).ToList();
            List<string> worth = names.Where(n => times.TryGetValue((n, phi), out double t) && t >= TooShort).ToList();
            if (others.Count > 0 && worth.Count > 0)
            {
                text.AppendLine();
                text.AppendLine($"{phi}'s time over each of the others' (1.50: PHI took half as long again; 0.50: half as long):");
                text.AppendLine();
                text.Append("".PadRight(first));
                foreach (string column in others) text.Append(column.PadLeft(width));
                text.AppendLine();
                var logs = others.ToDictionary(o => o, _ => new List<double>());
                foreach (string name in worth)
                {
                    text.Append(name.PadRight(first));
                    foreach (string column in others)
                    {
                        if (times.TryGetValue((name, column), out double t) && t > 0)
                        {
                            double ratio = times[(name, phi)] / t;
                            logs[column].Add(Math.Log(ratio));
                            text.Append(ratio.ToString("0.00", c).PadLeft(width));
                        }
                        else text.Append("-".PadLeft(width));
                    }
                    text.AppendLine();
                }
                if (worth.Count > 1)
                {
                    text.Append("together".PadRight(first));
                    foreach (string column in others)
                        text.Append((logs[column].Count > 0 ? Math.Exp(logs[column].Average()).ToString("0.00", c) : "-").PadLeft(width));
                    text.AppendLine();
                    text.AppendLine();
                    text.AppendLine("(together: the geometric mean of the rows above it)");
                }
                List<string> left = names.Except(worth).ToList();
                if (left.Count > 0)
                    text.AppendLine($"({string.Join(", ", left)}: under {TooShort * 1000:0} ms in PHI, which is starting and not work, so not in this table)");
            }

            text.AppendLine();
            foreach (string note in notes) text.AppendLine(note);
            return text.ToString();
        }

        /// <summary>The report as a Markdown file: the same text, with when and where it was measured.</summary>
        static string Markdown(string report, string dir)
        {
            var text = new StringBuilder();
            text.AppendLine("# PHI beside C and C#");
            text.AppendLine();
            text.AppendLine($"Measured {DateTime.Now:yyyy-MM-dd} by `phi compare` on the benchmarks in `{Path.GetRelativePath(Environment.CurrentDirectory, dir).Replace('\\', '/')}`, on {Machine()}.");
            text.AppendLine();
            text.AppendLine("```");
            text.Append(report);
            text.AppendLine("```");
            return text.ToString();
        }

        static string Machine()
        {
            string cpu = "";
            try
            {
                if (OperatingSystem.IsLinux())
                    cpu = File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name"))?.Split(':', 2)[1].Trim() ?? "";
                else
                    cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "";
            }
            catch (IOException) { }
            string os = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
            return cpu.Length > 0 ? $"{cpu} ({os})" : os;
        }
    }
}
