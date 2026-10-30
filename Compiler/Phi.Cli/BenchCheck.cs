using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Phi.Cli
{
    /// <summary>How phi test --bench runs: whether to save the counts as the new baselines, and
    /// how much more than its baseline (in percent) a count may be before the test fails.</summary>
    public sealed record BenchOptions(bool Save, double TolerancePercent);

    /// <summary>
    /// A benchmark is a test named bench_NAME that writes "bench WHAT COUNT" lines to COM1
    /// (lib/phi/test/bench.phi does). phi test --bench runs it under QEMU's -icount, where the
    /// timestamp counter goes up with the instructions run, so a count is the same on any
    /// computer, and compares each count with the one saved in NAME.bench: a count more than
    /// the tolerance above its baseline fails the test. The first run, and --save-bench, save
    /// the counts as the baselines.
    /// </summary>
    public static class BenchCheck
    {
        static readonly Regex BenchLine = new(@"^bench (.+) (\d+)$");

        /// <summary>A benchmark's test file starts with this.</summary>
        public const string Prefix = "bench_";

        public static bool IsBenchLine(string line) => BenchLine.IsMatch(line.Trim());

        /// <summary>The counts in a run's output, in the order they were written.</summary>
        public static List<(string Name, ulong Count)> Parse(string output)
        {
            var counts = new List<(string, ulong)>();
            foreach (string line in output.Replace("\r", "").Split('\n'))
            {
                Match m = BenchLine.Match(line.Trim());
                if (m.Success) counts.Add((m.Groups[1].Value.Trim(), ulong.Parse(m.Groups[2].Value)));
            }
            return counts;
        }

        /// <summary>NAME.bench: a name and its count on each line (the count is the last word); # starts a comment.</summary>
        public static Dictionary<string, ulong> ReadBaseline(string path)
        {
            var baseline = new Dictionary<string, ulong>();
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                int space = line.LastIndexOf(' ');
                if (space > 0 && ulong.TryParse(line[(space + 1)..], out ulong count))
                    baseline[line[..space].Trim()] = count;
            }
            return baseline;
        }

        public static void WriteBaseline(string path, IReadOnlyList<(string Name, ulong Count)> counts)
        {
            var text = new StringBuilder();
            text.AppendLine("# Counts from phi test --bench (instructions run, under QEMU's -icount).");
            text.AppendLine("# phi test --bench --save-bench replaces them with the latest counts.");
            int width = counts.Max(c => c.Name.Length);
            foreach (var (name, count) in counts)
                text.AppendLine($"{name.PadRight(width)}  {count}");
            File.WriteAllText(path, text.ToString());
        }

        /// <summary>
        /// Compares a benchmark's counts with its baseline (saving them when there is none, or
        /// with options.Save), adding a line per count to <paramref name="report"/>.
        /// </summary>
        /// <returns>null when no count got worse than the tolerance allows, otherwise why it failed</returns>
        public static string? Check(string baselinePath, string output, BenchOptions options, List<string> report)
        {
            var counts = Parse(output);
            string file = Path.GetFileName(baselinePath);
            if (counts.Count == 0)
                return "a benchmark has to write bench lines (use test.bench; call Bench.Start; ... call Bench.Stop: 'name';)";

            var duplicate = counts.GroupBy(c => c.Name).FirstOrDefault(g => g.Count() > 1);
            if (duplicate != null) return $"two counts are called '{duplicate.Key}'; give each one its own name";

            int width = counts.Max(c => c.Name.Length);
            if (!File.Exists(baselinePath) || options.Save)
            {
                bool first = !File.Exists(baselinePath);
                WriteBaseline(baselinePath, counts);
                foreach (var (name, count) in counts) report.Add($"{name.PadRight(width)}  {Format(count),15}");
                report.Add(first ? $"(the first counts: saved to {file})" : $"(saved to {file})");
                return null;
            }

            Dictionary<string, ulong> baseline = ReadBaseline(baselinePath);
            var slower = new List<string>();
            bool faster = false;
            foreach (var (name, count) in counts)
            {
                if (!baseline.TryGetValue(name, out ulong before))
                {
                    report.Add($"{name.PadRight(width)}  {Format(count),15}  (new: not in {file})");
                    continue;
                }
                double change = before == 0 ? 0 : (count - (double)before) / before * 100;
                string verdict = change > options.TolerancePercent ? "  SLOWER"
                               : change < -options.TolerancePercent ? "  faster" : "";
                report.Add($"{name.PadRight(width)}  {Format(count),15}  {change,7:+0.0;-0.0;0.0}%  (was {Format(before)}){verdict}");
                if (change > options.TolerancePercent) slower.Add($"{name}: {change:+0.0}%");
                if (change < -options.TolerancePercent) faster = true;
            }

            var gone = baseline.Keys.Where(k => counts.All(c => c.Name != k)).ToList();
            if (slower.Count == 0 && gone.Count == 0)
            {
                if (faster || counts.Any(c => !baseline.ContainsKey(c.Name)))
                    report.Add("(phi test --bench --save-bench keeps these as the new counts)");
                return null;
            }

            var why = new List<string>();
            if (slower.Count > 0)
                why.Add($"more than {options.TolerancePercent}% over the counts in {file}: {string.Join(", ", slower)}");
            if (gone.Count > 0)
                why.Add($"not measured any more: {string.Join(", ", gone)} (--save-bench forgets them)");
            return string.Join("\n", why);
        }

        static string Format(ulong count) => count.ToString("N0", CultureInfo.InvariantCulture);
    }
}
