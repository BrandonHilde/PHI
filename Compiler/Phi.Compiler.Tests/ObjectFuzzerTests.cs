using Phi.Cli;

namespace Phi.Compiler.Tests
{
    /// <summary>phi fuzz objects (ObjectFuzzer): the programs it writes, and what it says they print.</summary>
    public class ObjectFuzzerTests
    {
        static IEnumerable<CodeFuzzer.Case> Cases() =>
            Enumerable.Range(1, 40).Select(ObjectFuzzer.Make).Where(c => c != null).Select(c => c!);

        [Fact]
        public void TheSameSeedMakesTheSameProgram()
        {
            CodeFuzzer.Case first = Cases().First(), again = Cases().First();
            Assert.Equal(first.Source, again.Source);
            Assert.Equal(first.Expected, again.Expected);
            Assert.NotEqual(first.Source, Cases().Skip(1).First().Source);
        }

        [Theory]
        [InlineData(Host.Linux)]
        [InlineData(Host.Linux64)]
        [InlineData(Host.Windows)]
        [InlineData(Host.Windows64)]
        public void ItsProgramsAreProgramsOnEverySystem(Host host)
        {
            // (running them needs NASM and the system, which is phi fuzz objects' own job)
            foreach (CodeFuzzer.Case made in Cases().Take(12))
            {
                CompileResult result = PhiCompiler.Compile(new SourceFile("fuzz.phi", made.Source), host: host);
                Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)) + "\n" + made.Source);
                Assert.DoesNotContain(result.Diagnostics, d => d.Severity == Severity.Warning);
            }
        }

        [Fact]
        public void AProgramDoesItsWorkThreeTimesAndCountsWhatIsLeft()
        {
            foreach (CodeFuzzer.Case made in Cases().Take(12))
            {
                Assert.True(made.CheckedHeap);
                Assert.EndsWith("left behind: 0\n", made.Expected);
                string rounds = made.Expected[..^"left behind: 0\n".Length];
                string one = rounds[..(rounds.Length / 3)];
                Assert.Equal(one + one + one, rounds);
            }
        }

        [Fact]
        public void ATraceIsTheSameProgramWithMorePrinted()
        {
            // the statements between the prints are the same ones, in the same order (a loop's
            // counter is numbered after the prints before it, so its number isn't compared)
            static string[] Work(string source) =>
                source.Split('\n').Where(l => !l.TrimStart().StartsWith("log ") && !l.Contains("call Show") && !l.Contains(" is empty")
                                              && l.Trim() is not (";;" or "else") && !l.TrimStart().StartsWith("while int k"))
                    .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"\bk\d+\b", "k")).ToArray();
            foreach (int seed in new[] { 3, 8, 21 })
            {
                CodeFuzzer.Case plain = ObjectFuzzer.Make(seed)!, traced = ObjectFuzzer.Make(seed, trace: true)!;
                Assert.Equal(Work(plain.Source), Work(traced.Source));
                Assert.True(traced.Expected.Length > plain.Expected.Length);
            }
        }

        [Fact]
        public void TheCheckingHeapComesWithTheCompiler()
        {
            var files = ObjectFuzzer.CheckingHeap().ToList();
            Assert.Contains(files, f => f.Name == "program_heap.phi");
            Assert.Contains(files, f => f.Name == "checking_linux64.phi" && f.Text.Contains("phi.Heap"));
        }
    }
}
