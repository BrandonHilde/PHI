using System.Text.RegularExpressions;

namespace Phi.Cli
{
    /// <summary>
    /// Each test is tests/NAME.phi with the serial output it must produce in tests/NAME.expected.
    /// The test boots headless in QEMU; on a mismatch the real output is saved to NAME.actual.
    /// NAME.input scripts keyboard and mouse input (see InputScript); NAME.errors instead lists
    /// compile errors the program must produce.
    ///
    /// A test whose classes are all :Library runs twice, as a 16-bit program and as a 32-bit
    /// kernel, so both code generators are checked against the same expected output.
    /// </summary>
    public static class TestRunner
    {
        public static int Run(string target, string? filter, TimeSpan timeout)
        {
            List<string> tests = Directory.Exists(target)
                ? Directory.GetFiles(target, "*.phi").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string> { target };

            if (filter != null)
                tests = tests.Where(t => Path.GetFileNameWithoutExtension(t).Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

            if (tests.Count == 0)
            {
                Console.WriteLine($"No tests found in {target}");
                return 1;
            }

            var runs = tests.SelectMany(t => Modes(t).Select(m => (File: t, Mode: m))).ToList();

            int passed = 0;
            foreach (var (test, mode) in runs)
            {
                string name = Path.GetFileNameWithoutExtension(test) + (mode == null ? "" : $" ({mode.Name})");
                string? failure = RunOne(test, timeout, mode);

                if (failure == null)
                {
                    passed++;
                    Write(ConsoleColor.Green, "PASS ");
                    Console.WriteLine(name);
                }
                else
                {
                    Write(ConsoleColor.Red, "FAIL ");
                    Console.WriteLine(name);
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
        static string? RunOne(string phiFile, TimeSpan timeout, Mode? mode)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(phiFile))!;
            string name = Path.GetFileNameWithoutExtension(phiFile);
            string variant = mode == null ? name : $"{name}.{mode.Name}";
            string expectedFile = Path.Combine(dir, name + ".expected");
            string actualFile = Path.Combine(dir, variant + ".actual");

            if (File.Exists(actualFile)) File.Delete(actualFile);

            string errorsFile = Path.Combine(dir, name + ".errors");
            string inputFile = Path.Combine(dir, name + ".input");

            BuildResult build = Builder.Build(phiFile, Path.Combine(dir, "build", variant), mode?.Wrapper ?? "");

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

            // NAME.contains: each line must appear somewhere in the output (for output that
            // changes from build to build, like addresses on a panic screen)
            string containsFile = Path.Combine(dir, name + ".contains");
            if (File.Exists(containsFile))
            {
                var lines = File.ReadAllLines(containsFile).Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
                HeadlessResult partial = Qemu.RunHeadless(build.ImagePath, timeout,
                    output => lines.All(l => Normalize(output).Contains(l)), input);
                string text = Normalize(partial.Serial);
                var missing = lines.Where(l => !text.Contains(l)).ToList();
                if (missing.Count == 0 && partial.InputError == null) return null;

                File.WriteAllText(actualFile, text);
                string why = partial.InputError ?? "missing from the output: " + string.Join(" | ", missing);
                return $"{why}\n--- actual (saved to {Path.GetFileName(actualFile)})\n{text}";
            }

            HeadlessResult run = Qemu.RunHeadless(build.ImagePath, timeout,
                expected == null ? null : output => Normalize(output).Contains(expected), input);

            string actual = Normalize(run.Serial);

            if (expected == null)
            {
                File.WriteAllText(actualFile, actual);
                return $"missing {name}.expected; the output was saved to {Path.GetFileName(actualFile)} " +
                       "(rename it to .expected if it is correct)";
            }

            if (actual == expected && run.InputError == null) return null;

            File.WriteAllText(actualFile, actual);

            string reason = run.InputError ?? (run.TimedOut ? $"timed out after {timeout.TotalSeconds:0}s" : "output did not match");
            return $"{reason}\n--- expected\n{expected}\n--- actual (saved to {Path.GetFileName(actualFile)})\n{actual}";
        }

        /// <summary>Line endings and trailing whitespace don't matter.</summary>
        static string Normalize(string s) =>
            string.Join("\n", s.Replace("\r", "").Split('\n').Select(l => l.TrimEnd())).Trim('\n');

        static void Write(ConsoleColor color, string text)
        {
            ConsoleColor old = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.Write(text);
            Console.ForegroundColor = old;
        }
    }
}
