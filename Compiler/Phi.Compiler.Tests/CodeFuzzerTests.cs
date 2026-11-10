using Phi.Cli;

namespace Phi.Compiler.Tests
{
    /// <summary>phi fuzz code (CodeFuzzer): the programs it writes, and what it says they print.</summary>
    public class CodeFuzzerTests
    {
        static IEnumerable<CodeFuzzer.Case> Cases() =>
            Enumerable.Range(1, 40).Select(CodeFuzzer.Make).Where(c => c != null).Select(c => c!);

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
            // (running them needs NASM and the system, which is phi fuzz code's own job)
            foreach (CodeFuzzer.Case made in Cases().Take(12))
            {
                CompileResult result = PhiCompiler.Compile(new SourceFile("fuzz.phi", made.Source), host: host);
                Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)) + "\n" + made.Source);
                Assert.DoesNotContain(result.Diagnostics, d => d.Severity == Severity.Warning);
            }
        }

        [Fact]
        public void WhatAProgramPrintsEndsWithEveryVariableAndArray()
        {
            foreach (CodeFuzzer.Case made in Cases().Take(12))
            {
                string[] lines = made.Expected.TrimEnd('\n').Split('\n');
                Assert.StartsWith("end ", lines[^4]);
                Assert.StartsWith("ints ", lines[^3]);
                Assert.StartsWith("words ", lines[^2]);
                Assert.StartsWith("bytes ", lines[^1]);
            }
        }
    }
}
