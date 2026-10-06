using Phi.Compiler.CodeGen;
using Phi.Compiler.Semantics;

namespace Phi.Compiler.Tests
{
    /// <summary>Phase 3: 32-bit kernels (phi.Name:Kernel) and the standard boot stages.</summary>
    public class ProtectedModeTests
    {
        static CompileResult Compile(string text) => PhiCompiler.Compile(new SourceFile("test.phi", text));

        static void AssertError(string text, string message)
        {
            CompileResult result = Compile(text);
            Assert.False(result.Success);
            Assert.Contains(result.Diagnostics, d => d.Message.Contains(message));
        }

        [Fact]
        public void KernelClassesMakeA32BitKernelWithTheStandardLoader()
        {
            CompileResult result = Compile("phi.K:Kernel\n{\n log 'hi';\n}\n");
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));

            AsmUnit kernel = Assert.Single(result.Units);
            Assert.Equal(UnitKind.Kernel, kernel.Kind);
            Assert.Contains("bits 32", kernel.Text);
            Assert.Contains("org 0x10000", kernel.Text);
            Assert.Contains("call phi_console_init", kernel.Text); // the console continues below the BIOS text

            Assert.Equal(new[] { "stage1", "stage2" }, result.BootStages.Select(s => s.Name));
        }

        [Fact]
        public void SixteenBitProgramsHaveNoBootStages()
        {
            CompileResult result = Compile("phi.B:Bootloader\n{\n log 'hi';\n}\n");
            Assert.Empty(result.BootStages);
            Assert.Contains("bits 16", Assert.Single(result.Units).Text);
        }

        [Fact]
        public void PointersAreFlatIn32BitCode()
        {
            string asm = Compile("phi.K:Kernel\n{\n ptr<u16> vga: 0xB8000;\n int i: 3;\n vga:i is 1;\n}\n").Units[0].Text;
            Assert.DoesNotContain("mov fs, cx", asm);   // the 16-bit way of reaching far memory
            Assert.Contains("[ebx]", asm);
        }

        [Fact]
        public void LibraryClassesJoinWhicheverProgramUsesThem()
        {
            const string library = "phi.Shared:Library\n{\n int x: 1;\n}\n";

            CompileResult sixteen = Compile(library + "phi.B:Bootloader\n{\n call Bootloader.JumpToSectorTwo;\n}\n");
            Assert.Single(sixteen.Bound!.Os!.Classes);

            CompileResult thirtyTwo = Compile(library + "phi.K:Kernel\n{\n}\n");
            Assert.Equal(2, thirtyTwo.Bound!.Kernel!.Classes.Count);
        }

        [Fact]
        public void KernelsCantMixIn16BitClasses() =>
            AssertError("phi.K:Kernel\n{\n}\nphi.B:Bootloader\n{\n}\n", "is 16-bit code");

        [Fact]
        public void AskUsesTheKeyboardDriver()
        {
            string asm = Compile("phi.K:Kernel\n{\n str s: [5];\n ask s;\n}\n").Units[0].Text;
            Assert.Contains("phi_keyboard_irq:", asm);
        }

        [Fact]
        public void EveryKernelHasAnInterruptTableAndPanicScreen()
        {
            string asm = Compile("phi.K:Kernel\n{\n}\n").Units[0].Text;
            Assert.Contains("call phi_interrupts_init", asm);
            Assert.Contains("lidt", asm);
            Assert.Contains("phi_panic:", asm);
        }

        [Theory]
        [InlineData("OS.DrawPixel: 1 2 3")]
        [InlineData("Bootloader.EnableVideoMode")]
        public void GraphicsArentAvailableIn32BitYet(string call) =>
            AssertError($"phi.K:Kernel\n{{\n call {call};\n}}\n", "isn't available in the 32-bit kernel");

        [Theory]
        [InlineData("OS.GetTicks")]
        [InlineData("OS.SetIrqHandler: 1 0")]
        public void SomeBuiltinsAreOnlyIn32Bit(string call) =>
            AssertError($"phi.B:Bootloader\n{{\n call {call};\n}}\n", "make the program a phi.Name:Kernel");

        [Fact]
        public void BootInfoIsInTheStandardLibrary()
        {
            CompileResult result = Compile("use boot.info;\nphi.K:Kernel\n{\n log Boot.info:0.region_count;\n}\n");
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        }
    }
}
