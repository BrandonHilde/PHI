using Phi.Cli;

namespace Phi.Compiler.Tests
{
    /// <summary>phi fuzz numbers (NumberFuzzer): the programs it writes, and what it says they print.</summary>
    public class NumberFuzzerTests
    {
        static IEnumerable<CodeFuzzer.Case> Cases() =>
            Enumerable.Range(1, 60).Select(NumberFuzzer.Make).Where(c => c != null).Select(c => c!);

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
            // (running them needs NASM and the system, which is phi fuzz numbers' own job)
            foreach (CodeFuzzer.Case made in Cases().Take(12))
            {
                CompileResult result = PhiCompiler.Compile(new SourceFile("fuzz.phi", made.Source), host: host);
                Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)) + "\n" + made.Source);
                Assert.DoesNotContain(result.Diagnostics, d => d.Severity == Severity.Warning);
            }
        }

        [Fact]
        public void TheyAreMadeOfEveryKindOfNumber()
        {
            // what the programs are for: 64-bit numbers and floats, with ints and u32s among them
            string all = string.Join("\n", Cases().Take(12).Select(c => c.Source));
            foreach (string wanted in new[] { " as i64)", " as u64)", " as f64)", " as int)", " as u32)", " >> ", " << ", " / ", " % ", "(Bits: ", "while int idx", "rec.real", "rec.big", "(F" })
                Assert.Contains(wanted, all);
        }

        [Fact]
        public void WhatAProgramPrintsEndsWithEveryVariableFieldAndElement()
        {
            foreach (CodeFuzzer.Case made in Cases().Take(12))
            {
                // 11 class variables, rec's 4 fields, and three arrays of 4 or 8: a number on each line
                string[] lines = made.Expected.TrimEnd('\n').Split('\n');
                Assert.True(lines.Length >= 11 + 4 + 12);
                Assert.All(lines[^27..], line => Assert.Matches(@"^-?\d+$", line));
            }
        }
    }
}
