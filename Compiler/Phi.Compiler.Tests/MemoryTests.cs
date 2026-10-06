using Phi.Compiler.Semantics;
using Phi.Compiler.Syntax;

namespace Phi.Compiler.Tests
{
    /// <summary>Phase 5: new, free and the memory library.</summary>
    public class MemoryTests
    {
        static CompileResult Compile(string text) => PhiCompiler.Compile(new SourceFile("test.phi", text));

        static void AssertError(string text, string message)
        {
            CompileResult result = Compile(text);
            Assert.False(result.Success);
            Assert.Contains(result.Diagnostics, d => d.Message.Contains(message));
        }

        const string Task = "struct.Task\n{\n u32 id;\n u8 name[12];\n}\n";

        [Fact]
        public void NewGetsTheHeapAutomatically()
        {
            CompileResult result = Compile(Task + "phi.K:Kernel\n{\n ptr<Task> t: new Task;\n free t;\n}\n");
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));

            // the heap (and what it uses) comes before the program's own class
            var names = result.Syntax!.Classes.Select(c => c.Name).ToList();
            Assert.True(names.IndexOf("Heap") < names.IndexOf("K"));
            Assert.Contains("Frames", names);
            Assert.Contains("Paging", names);
        }

        [Fact]
        public void NewAsksForTheElementSizeTimesTheCount()
        {
            CompileResult result = Compile(Task + "phi.K:Kernel\n{\n u32 n: 3;\n ptr<Task> list: new Task[n];\n}\n");
            var decl = result.Syntax!.Classes.Single(c => c.Name == "K").Body.OfType<VarDecl>().Single(v => v.Name == "list");
            var create = Assert.IsType<NewExpr>(decl.Values[0]);
            var bytes = Assert.IsType<BinaryExpr>(create.Bytes);
            Assert.Equal(16L, Assert.IsType<NumberExpr>(bytes.Right).Value);
            Assert.Equal(PhiType.PointerTo(create.Type.Pointee!), create.Type);
        }

        [Fact]
        public void ProgramsWithoutNewDontGetTheHeap()
        {
            CompileResult result = Compile("phi.K:Kernel\n{\n log 'hi';\n}\n");
            Assert.DoesNotContain(result.Syntax!.Classes, c => c.Name == "Heap");
        }

        [Fact]
        public void NewNeedsA32BitKernel() =>
            AssertError(Task + "phi.B:Bootloader\n{\n ptr<Task> t: new Task;\n}\n", "only 32-bit kernels");

        [Fact]
        public void FreeNeedsAPointer() =>
            AssertError("phi.K:Kernel\n{\n str s: 'x';\n free s;\n}\n", "free needs a pointer");

        [Fact]
        public void NewCantMakeStrs() =>
            AssertError("phi.K:Kernel\n{\n ptr<u8> s: new str;\n}\n", "str can't be used here");

        [Theory]
        [InlineData("memory.frames")]
        [InlineData("memory.paging")]
        [InlineData("memory.heap")]
        [InlineData("memory.list")]
        public void MemoryLibraryFilesCompile(string file)
        {
            CompileResult result = Compile($"use {file};\nphi.K:Kernel\n{{\n}}\n");
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        }
    }
}
