using Phi.Compiler.Semantics;

namespace Phi.Compiler.Tests
{
    /// <summary>Phase 7: user programs (phi.Name:Program) and the kernel's process support.</summary>
    public class ProgramTests
    {
        static CompileResult Compile(string text) => PhiCompiler.Compile(new SourceFile("test.phi", text));

        static void AssertError(string text, string message)
        {
            CompileResult result = Compile(text);
            Assert.False(result.Success);
            Assert.Contains(result.Diagnostics, d => d.Message.Contains(message));
        }

        static string Program(string body) => "phi.P:Program\n{\n" + body + "\n}\n";

        [Fact]
        public void ProgramsAreFlat32BitBinariesAtTheUserAddress()
        {
            CompileResult result = Compile(Program("log 'hi';"));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            var unit = Assert.Single(result.Units);
            Assert.Equal(UnitKind.Program, unit.Kind);
            Assert.Contains("org 0x40000000", unit.Text);
            Assert.Empty(result.BootStages);           // not bootable on its own
            Assert.DoesNotContain("phi_interrupts_init", unit.Text);
        }

        [Fact]
        public void OutputIsASystemCallAndWholeLinesAreFlushed()
        {
            string asm = Compile(Program("log 'a' 1;")).Units[0].Text;
            Assert.Contains("int 0x80", asm);
            Assert.Contains("call phi_flush", asm);
            Assert.DoesNotContain("0xB8000", asm);     // never the screen directly
        }

        [Fact]
        public void ExitAndTheEndOfTheProgramAreSystemCalls()
        {
            string asm = Compile(Program("exit 3;")).Units[0].Text;
            Assert.Contains("jmp phi_exit", asm);
            Assert.DoesNotContain("out dx, al", asm);  // not QEMU's debug exit port
        }

        [Fact]
        public void SpawnTakesText()
        {
            CompileResult result = Compile(Program("int pid: 0;\ncall pid is OS.Spawn: 'X.BIN';\ncall pid is OS.Wait: pid;"));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        }

        [Fact]
        public void LibraryClassesJoinPrograms()
        {
            CompileResult result = Compile("phi.Shared:Library\n{\n int x: 1;\n}\n" + Program("log Shared.x;"));
            Assert.Equal(2, result.Bound!.Program!.Classes.Count);
        }

        [Theory]
        [InlineData("out 0x3F8 65;", "can't use ports")]
        [InlineData("u8 s: in 0x60;", "can't use ports")]
        [InlineData("[isr H]\n[end]", "can't handle interrupts")]
        [InlineData("call OS.SetupKeyboardInterupt;", "only through the kernel's system calls")]
        [InlineData("call OS.StartMultitasking;", "only through the kernel's system calls")]
        [InlineData("ptr<u8> p: new u8[4];", "only 32-bit kernels")]
        public void ProgramsCantTouchHardware(string body, string message) => AssertError(Program(body), message);

        [Fact]
        public void ProgramsCantMixWithKernelClasses() =>
            AssertError(Program("") + "phi.K:Kernel\n{\n}\n", "can't be part of a user program");

        [Fact]
        public void TheProcessLibraryCompiles()
        {
            CompileResult result = Compile("use proc.process;\nphi.K:Kernel\n{\n int pid: 0;\n call pid is Process.Spawn: 'A.BIN';\n call pid is Process.Wait: pid;\n}\n");
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        }
    }
}
