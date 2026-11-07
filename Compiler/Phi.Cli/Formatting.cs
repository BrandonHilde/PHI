using Phi.Compiler;
using Phi.Compiler.Language;

namespace Phi.Cli
{
    /// <summary>phi fmt: each file or folder of .phi files named, laid out in place.</summary>
    public static class Formatting
    {
        public static int Run(IReadOnlyList<string> paths, bool checkOnly)
        {
            if (paths.Count == 0) throw new ArgumentException("fmt needs a .phi file or a folder");

            var files = new List<string>();
            foreach (string path in paths)
            {
                if (File.Exists(path)) files.Add(path);
                else if (Directory.Exists(path)) files.AddRange(PhiFilesIn(path));
                else throw new ArgumentException($"file not found: {path}");
            }

            int changed = 0, failed = 0;
            foreach (string path in files)
            {
                string text = File.ReadAllText(path);
                var diagnostics = new DiagnosticBag();
                string? laid = Formatter.Format(new SourceFile(path, text), diagnostics);
                if (laid == null)
                {
                    foreach (Diagnostic d in diagnostics.Items.Where(d => d.Severity == Severity.Error)) Console.Error.WriteLine(d);
                    failed++;
                    continue;
                }
                if (laid == text) continue;
                changed++;
                if (checkOnly) Console.WriteLine($"{path} would change");
                else
                {
                    File.WriteAllText(path, laid);
                    Console.WriteLine($"{path} laid out");
                }
            }

            if (failed > 0) Console.Error.WriteLine($"{failed} file{(failed == 1 ? "" : "s")} left alone, with errors to fix first");
            if (changed == 0 && failed == 0) Console.WriteLine($"{files.Count} file{(files.Count == 1 ? "" : "s")} laid out already");
            return failed > 0 || checkOnly && changed > 0 ? 1 : 0;
        }

        static IEnumerable<string> PhiFilesIn(string folder)
        {
            foreach (string file in Directory.GetFiles(folder, "*.phi")) yield return file;
            foreach (string sub in Directory.GetDirectories(folder))
            {
                string name = Path.GetFileName(sub);
                if (name is "build" or "bin" or "obj" or "node_modules" || name.StartsWith('.')) continue;
                foreach (string file in PhiFilesIn(sub)) yield return file;
            }
        }
    }
}
