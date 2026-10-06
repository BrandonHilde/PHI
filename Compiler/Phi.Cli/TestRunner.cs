namespace Phi.Cli
{
    /// <summary>
    /// Each test is tests/NAME.phi with the serial output it must produce in tests/NAME.expected.
    /// The test boots headless in QEMU; on a mismatch the real output is saved to NAME.actual.
    /// NAME.input scripts keyboard and mouse input (see InputScript); NAME.errors instead lists
    /// compile errors the program must produce.
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

            int passed = 0;
            foreach (string test in tests)
            {
                string name = Path.GetFileNameWithoutExtension(test);
                string? failure = RunOne(test, timeout);

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
            Write(passed == tests.Count ? ConsoleColor.Green : ConsoleColor.Red, $"{passed}/{tests.Count} passed");
            Console.WriteLine();

            return passed == tests.Count ? 0 : 1;
        }

        /// <returns>null on success, otherwise a description of the failure</returns>
        static string? RunOne(string phiFile, TimeSpan timeout)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(phiFile))!;
            string name = Path.GetFileNameWithoutExtension(phiFile);
            string expectedFile = Path.Combine(dir, name + ".expected");
            string actualFile = Path.Combine(dir, name + ".actual");

            if (File.Exists(actualFile)) File.Delete(actualFile);

            string errorsFile = Path.Combine(dir, name + ".errors");
            string inputFile = Path.Combine(dir, name + ".input");

            BuildResult build = Builder.Build(phiFile, Path.Combine(dir, "build", name));

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

            HeadlessResult run = Qemu.RunHeadless(build.ImagePath, timeout,
                expected == null ? null : output => Normalize(output).Contains(expected), input);

            string actual = Normalize(run.Serial);

            if (expected == null)
            {
                File.WriteAllText(actualFile, actual);
                return $"missing {name}.expected; the output was saved to {name}.actual " +
                       "(rename it to .expected if it is correct)";
            }

            if (actual == expected && run.InputError == null) return null;

            File.WriteAllText(actualFile, actual);

            string reason = run.InputError ?? (run.TimedOut ? $"timed out after {timeout.TotalSeconds:0}s" : "output did not match");
            return $"{reason}\n--- expected\n{expected}\n--- actual (saved to {name}.actual)\n{actual}";
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
