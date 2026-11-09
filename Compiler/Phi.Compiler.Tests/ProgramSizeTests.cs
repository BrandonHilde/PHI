using Phi.Cli;

namespace Phi.Compiler.Tests
{
    /// <summary>A PHI OS program has to fit below its stack, or Process.Spawn can't start it.</summary>
    public class ProgramSizeTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "phi-size-" + Guid.NewGuid().ToString("N"));

        public ProgramSizeTests() => Directory.CreateDirectory(root);

        public void Dispose() => Directory.Delete(root, recursive: true);

        BuildResult BuildProgram(int arrays)
        {
            // each array is written in place, in the program's variables
            string variables = string.Concat(Enumerable.Range(0, arrays).Select(i => $"\tu8 block{i}[16384];\n"));
            string path = Path.Combine(root, "big.phi");
            File.WriteAllText(path, "phi.Big:Program\n{\n" + variables + "\tblock0:0 is 1;\n\texit 0;\n}\n");
            return Builder.Build(path, Path.Combine(root, "build"));
        }

        [Fact]
        public void AProgramThatFitsIsBuilt()
        {
            BuildResult result = BuildProgram(4);
            Assert.True(result.Success, string.Join("\n", result.Errors));
            Assert.True(File.Exists(result.ProgramPath));
        }

        [Fact]
        public void AProgramTooBigToStartIsAnError()
        {
            BuildResult result = BuildProgram(260);         // 4,259,840 bytes of variables alone
            Assert.False(result.Success);
            Assert.Contains(result.Errors, e => e.Contains("more than a PHI OS program can be (4,112,384"));
            Assert.False(File.Exists(result.ProgramPath));
        }
    }
}
