using Phi.Compiler.Semantics;
using Phi.Compiler.Syntax;

namespace Phi.Compiler.Tests
{
    public class CompilerTests
    {
        static CompileResult Compile(string text) => PhiCompiler.Compile(new SourceFile("test.phi", text));

        static string Boot(string body) => "phi.Test:Bootloader\n{\n" + body + "\n}\n";

        static void AssertError(string text, string message)
        {
            CompileResult result = Compile(text);
            Assert.False(result.Success);
            Assert.Contains(result.Diagnostics, d => d.Message.Contains(message));
        }

        [Fact]
        public void ParsesTheMainStatementForms()
        {
            CompileResult result = Compile(Boot("""
                int a: 1;
                a is 2;
                a++;
                a++ 3;
                a-- a;
                if a > 1 and not (a is 5)
                    log 'x' a;
                ;;
                elif a < 0
                    log 'y';
                ;;
                else
                    log 'z';
                ;;
                while int i: 0; i < 3; i++;
                    debug i;
                ;;
                call a is Twice: a;
                [Twice: int n: 0;]
                [end: n * 2]
                """));

            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            Assert.Single(result.Units);
        }

        [Fact]
        public void CompoundAssignmentsBecomePlainOnes()
        {
            CompileResult result = Compile(Boot("int a: 1;\na++ 4;"));
            var assign = result.Syntax!.Classes[0].Body.OfType<AssignStmt>().Single();
            Assert.Equal(AssignOp.Set, assign.Op);
            Assert.IsType<BinaryExpr>(assign.Value);
        }

        [Fact]
        public void ElifNestsInsideElse()
        {
            CompileResult result = Compile(Boot("int a: 1;\nif a is 1\n log 'a';\n;;\nelif a is 2\n log 'b';\n;;"));
            var ifs = result.Syntax!.Classes[0].Body.OfType<IfStmt>().Single();
            Assert.IsType<IfStmt>(Assert.Single(ifs.Else!));
        }

        [Fact]
        public void BinaryOperatorsDoNotContinueOnTheNextLine()
        {
            // the condition is just "flag"; "-x" on the next line isn't part of it
            CompileResult result = Compile(Boot("bln flag: true;\nint x: 1;\nif flag\n x is -x;\n;;"));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        }

        [Fact]
        public void StorageIsWorkedOutFromTheDeclaration()
        {
            CompileResult result = Compile(Boot("""
                str name: 'abc';
                str buffer: [40];
                str fromNumber: 256;
                int list: 1 2 3;
                str days: 'Mon' 'Tuesday';
                var inferred: 'text';
                """));

            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            var vars = result.Bound!.Boot.Variables.ToDictionary(v => v.Name);
            Assert.Equal(4, vars["name"].Capacity);
            Assert.Equal(41, vars["buffer"].Capacity);
            Assert.Equal(12, vars["fromNumber"].Capacity);
            Assert.Equal(PhiType.Int.ArrayOf(3), vars["list"].Type);
            Assert.Equal(3, vars["list"].Count);
            Assert.Equal(PhiType.Str.ArrayOf(2), vars["days"].Type);
            Assert.Equal(8, vars["days"].Capacity);
            Assert.Equal(PhiType.Str, vars["inferred"].Type);
        }

        [Fact]
        public void ClassConstantsAreStaticButMethodLocalsAreSetEachCall()
        {
            CompileResult result = Compile(Boot("int a: 5;\ncall M;\n[M]\n int b: 6;\n[end]"));
            var vars = result.Bound!.Boot.Variables.ToDictionary(v => v.Name);
            Assert.False(vars["a"].NeedsInitCode);
            Assert.True(vars["b"].NeedsInitCode);
        }

        [Fact]
        public void BootAndKernelBecomeSeparateUnits()
        {
            CompileResult result = Compile("phi.B:Bootloader\n{\n call Bootloader.JumpToSectorTwo;\n}\nphi.K:OS\n{\n log 'k';\n}\n");
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            Assert.Equal(new[] { UnitKind.Boot, UnitKind.Kernel }, result.Units.Select(u => u.Kind));
            Assert.Contains("org 0x7E00", result.Units[1].Text);
        }

        [Fact]
        public void OnlyUsedLibraryRoutinesAreIncluded()
        {
            string asm = Compile(Boot("log 'hi';")).Units[0].Text;
            Assert.Contains("phi_print:", asm);
            Assert.DoesNotContain("phi_int_to_str:", asm);
            Assert.DoesNotContain("OS_DrawRectangle:", asm);
        }

        [Fact]
        public void UnhandledEventsGetANoOp()
        {
            string asm = Compile(Boot("call OS.SetupInteruptTimer;")).Units[0].Text;
            Assert.Contains("OS_TimerEvent:\n    ret", asm.Replace("\r\n", "\n"));
        }

        [Fact]
        public void HandledEventsUseTheProgramsMethod()
        {
            string asm = Compile(Boot("call OS.SetupInteruptTimer;\n[OS.TimerEvent]\n log 't';\n[end]")).Units[0].Text;
            Assert.Single(asm.Split('\n'), l => l.Trim() == "OS_TimerEvent:");
        }

        [Theory]
        [InlineData("log nope;", "unknown name 'nope'")]
        [InlineData("int x: 'abc';", "needs a number")]
        [InlineData("str s: 'a';\ns++ 1;", "only works on numbers")]
        [InlineData("call Missing;", "unknown method 'Missing'")]
        [InlineData("call OS.DrawPixel: 1;", "takes 3 arguments")]
        [InlineData("int x: 1;\nint x: 2;", "already declared")]
        [InlineData("int log: 1;", "keyword")]
        [InlineData("[OS.Unknown]\n[end]", "isn't an event")]
        [InlineData("str s: 'a';\nif s > 'b'\n;;", "strings can only be compared")]
        [InlineData("dec pi: 3.14;", "isn't supported yet")]
        [InlineData("int a: 1;\na ^^ 2;", "isn't supported yet")]
        public void SemanticErrors(string body, string message) => AssertError(Boot(body), message);

        [Theory]
        [InlineData("log 'a'\nlog 'b'", "expected ';'")]
        [InlineData("if 1\n log 'a';", "expected ';;'")]
        [InlineData("[M]\n log 'a';", "missing its [end]")]
        [InlineData("else\n;;", "must directly follow")]
        public void SyntaxErrors(string body, string message) => AssertError(Boot(body), message);

        [Fact]
        public void ParserRecoversAndReportsSeveralErrors()
        {
            CompileResult result = Compile(Boot("log 'a'\nint : 1;\nlog 'fine';\ncall ;"));
            Assert.True(result.Diagnostics.Count(d => d.Severity == Severity.Error) >= 3, string.Join("\n", result.Diagnostics));
        }

        [Fact]
        public void ProgramsNeedABootloader()
        {
            AssertError("phi.K:OS\n{\n}\n", "needs a class that inherits Bootloader");
        }

        [Fact]
        public void VariablesAreNotSharedBetweenBootAndKernel()
        {
            AssertError("phi.B:Bootloader\n{\n int x: 1;\n call Bootloader.JumpToSectorTwo;\n}\nphi.K:OS\n{\n log x;\n}\n",
                        "built separately");
        }

        [Fact]
        public void ErrorsHaveLineAndColumn()
        {
            Diagnostic d = Compile(Boot("log 'ok';\n  log nope;")).Diagnostics.Single();
            Assert.StartsWith("test.phi:4:7: error:", d.ToString());
        }

        static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PHI.sln"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
        }

        public static IEnumerable<object[]> RunnableSamples() =>
            new[] { "hello.phi", "arcade.phi" }.Select(f => new object[] { f });

        [Theory]
        [MemberData(nameof(RunnableSamples))]
        public void SamplesCompile(string sample)
        {
            CompileResult result = PhiCompiler.Compile(Path.Combine(RepoRoot(), "samples", sample));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        }

        [Fact]
        public void SyntaxShowcaseParses()
        {
            // code.phi demonstrates syntax ideas rather than being a runnable program
            var diagnostics = new DiagnosticBag();
            Parser.Parse(SourceFile.Load(Path.Combine(RepoRoot(), "samples", "code.phi")), diagnostics);
            Assert.Empty(diagnostics.Items);
        }
    }
}
