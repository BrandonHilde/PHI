using Phi.Compiler.Semantics;
using Phi.Compiler.Syntax;

namespace Phi.Compiler.Tests
{
    /// <summary>Phase 2: sized types, bit operations, constants, pointers, structs, ports, interrupts.</summary>
    public class SystemsTests
    {
        static CompileResult Compile(string text) => PhiCompiler.Compile(new SourceFile("test.phi", text));

        static string Boot(string body) => "phi.Test:Bootloader\n{\n" + body + "\n}\n";

        static CompileResult CompileOk(string text)
        {
            CompileResult result = Compile(text);
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            return result;
        }

        static Expr ValueOf(CompileResult result, string variable) =>
            result.Syntax!.Classes[0].Body.OfType<VarDecl>().Single(v => v.Name == variable).Values[0];

        [Fact]
        public void ShiftsBindTighterThanBitwiseAndBitwiseTighterThanComparisons()
        {
            // 1 << 2 | 1 is 5   means   ((1 << 2) | 1) is 5
            var cmp = Assert.IsType<BinaryExpr>(ValueOf(CompileOk(Boot("bln b: 1 << 2 | 1 is 5;")), "b"));
            Assert.Equal(BinaryOp.Equal, cmp.Op);
            var or = Assert.IsType<BinaryExpr>(cmp.Left);
            Assert.Equal(BinaryOp.BitOr, or.Op);
            Assert.Equal(BinaryOp.ShiftLeft, Assert.IsType<BinaryExpr>(or.Left).Op);
        }

        [Fact]
        public void LessLessIsAShiftNow()
        {
            Assert.Equal(BinaryOp.ShiftLeft, Assert.IsType<BinaryExpr>(ValueOf(CompileOk(Boot("int x: 1 << 3;")), "x")).Op);
        }

        [Theory]
        [InlineData("u32 x: 0xFFFFFFFF / 2;", 0x7FFFFFFFL)]
        [InlineData("int x: -16 >> 2;", -4L)]
        [InlineData("u32 x: 0x80000000 >> 31;", 1L)]
        [InlineData("int x: ~0;", -1L)]
        [InlineData("int x: (12 & 10) | 1;", 9L)]
        public void ConstantsFoldWithTheRightSignedness(string body, long value)
        {
            CompileResult result = CompileOk(Boot(body));
            long folded = ConstantFolder.Evaluate(ValueOf(result, "x"))!.Value;
            Assert.Equal((uint)value, (uint)folded); // compare as the 32 bits the CPU would hold
        }

        [Fact]
        public void NestedPointerTypesParse()
        {
            CompileResult result = CompileOk(Boot("ptr<ptr<u8>> table: 0;"));
            PhiType t = result.Bound!.Boot!.Variables.Single().Type;
            Assert.True(t.IsPointer);
            Assert.Equal(PhiType.PointerTo(PhiType.U8), t.Pointee);
        }

        [Fact]
        public void StructsArePackedInOrder()
        {
            CompileResult result = CompileOk("struct.Point\n{\n i16 x;\n i16 y;\n}\nstruct.Task\n{\n u32 id;\n Point pos;\n u8 name[8];\n ptr<Task> next;\n}\n" + Boot("Task t;"));
            StructSymbol task = result.Bound!.Boot!.Variables.Single().Type.Struct!;
            Assert.Equal(new[] { ("id", 0), ("pos", 4), ("name", 8), ("next", 16) }, task.Fields.Select(f => (f.Name, f.Offset)));
            Assert.Equal(20, task.Size);
        }

        [Fact]
        public void FieldsOfIndexedStructsParseAsMembers()
        {
            CompileResult result = CompileOk("struct.T\n{\n int id;\n}\n" + Boot("T list[4];\nint i: 1;\nlist:i.id is 5;"));
            var assign = result.Syntax!.Classes[0].Body.OfType<AssignStmt>().Single();
            var member = Assert.IsType<MemberExpr>(assign.Target);
            Assert.Equal("id", member.Member);
            Assert.IsType<IndexExpr>(member.Target);
        }

        [Fact]
        public void FieldsByNameResolveToAnOffset()
        {
            CompileResult result = CompileOk("struct.P\n{\n u8 a;\n u16 b;\n}\nstruct.Q\n{\n u8 pad;\n P inner;\n}\n" + Boot("Q q;\nq.inner.b is 3;"));
            var assign = result.Syntax!.Classes[0].Body.OfType<AssignStmt>().Single();
            var path = Assert.IsType<FieldPathSymbol>(((NameExpr)assign.Target).Symbol);
            Assert.Equal(2, path.Offset);
            Assert.Equal(PhiType.U16, path.Type);
        }

        [Fact]
        public void ConstantsWorkInSizesAndOtherConstants()
        {
            CompileResult result = CompileOk(Boot("const int N: 4;\nconst int M: N * 2;\nint buffer[M];"));
            Assert.Equal(8, result.Bound!.Boot!.Variables.Single().Count);
        }

        [Fact]
        public void IndexesAreCheckedOutsideUnsafe()
        {
            CompileResult result = CompileOk(Boot("int a: 1 2 3;\nint i: 0;\nlog a:i;\nunsafe\n log a:i;\n;;"));
            var logs = Flatten(result.Syntax!.Classes[0].Body).OfType<LogStmt>().ToList();
            Assert.Equal(3, ((IndexExpr)logs[0].Values[0]).CheckLimit);
            Assert.Null(((IndexExpr)logs[1].Values[0]).CheckLimit);
        }

        [Fact]
        public void PointersAreNeverBoundsChecked()
        {
            CompileResult result = CompileOk(Boot("ptr<u8> p: 0xB8000;\nint i: 0;\nlog p:i;"));
            var log = result.Syntax!.Classes[0].Body.OfType<LogStmt>().Single();
            Assert.Null(((IndexExpr)log.Values[0]).CheckLimit);
        }

        [Fact]
        public void InterruptHandlersSaveEverythingAndReturnWithIret()
        {
            string asm = CompileOk(Boot("call OS.SetInterruptHandler: 9 addr H;\n[isr H]\n debug 'x';\n[end]")).Units[0].Text;
            int start = asm.IndexOf("M_Test_H:");
            string handler = asm[start..asm.IndexOf("iret", start)];
            Assert.Contains("pushad", handler);
            Assert.Contains("popad", handler);
        }

        [Fact]
        public void PortsUseDx()
        {
            string asm = CompileOk(Boot("out 0x3F8 65;\nu8 s: in 0x3FD;")).Units[0].Text;
            Assert.Contains("mov dx, 0x3F8", asm);
            Assert.Contains("out dx, al", asm);
            Assert.Contains("in al, dx", asm);
        }

        [Fact]
        public void StandardLibraryFilesCanBeUsed()
        {
            CompileOk("use drivers.vga_text;\nphi.B:Bootloader\n{\n call Bootloader.JumpToSectorTwo;\n}\nphi.K:OS\n{\n call Vga.Print: 'hi';\n}\n");
        }

        [Theory]
        [InlineData("Mystery m;", "unknown type 'Mystery'")]
        [InlineData("ptr<str> p;", "str can't be used here")]
        [InlineData("int a: 1 2;\nlog a:2;", "outside 'a'")]
        [InlineData("int n: 1;\nconst int C: n;", "must be known when the program is built")]
        [InlineData("int a[0];", "between 1 and 16384")]
        [InlineData("[isr H: int x: 0;]\n[end]", "can't take parameters")]
        [InlineData("call H;\n[isr H]\n[end]", "is an interrupt handler")]
        [InlineData("bln b: 1 < 2 < 3;", "can't be chained")]
        public void SystemsErrors(string body, string message)
        {
            CompileResult result = Compile(Boot(body));
            Assert.False(result.Success);
            Assert.Contains(result.Diagnostics, d => d.Message.Contains(message));
        }

        static IEnumerable<Stmt> Flatten(IEnumerable<Stmt> body)
        {
            foreach (Stmt s in body)
            {
                yield return s;
                if (s is UnsafeStmt u)
                    foreach (Stmt inner in Flatten(u.Body)) yield return inner;
            }
        }
    }
}
