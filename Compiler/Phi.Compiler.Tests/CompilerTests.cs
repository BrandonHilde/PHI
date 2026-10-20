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

        [Theory]
        [InlineData("int a: 3 -1 -2;", 3)]       // a spaced '-1' is its own value
        [InlineData("int a: 3 - 1 -2;", 2)]      // 3 - 1, then -2
        [InlineData("int a: 3-1 3 -1;", 3)]      // unspaced subtracts
        [InlineData("int a: (3 -1) 4;", 2)]      // inside ( ) it always subtracts
        public void NegativeValuesInLists(string declaration, int count)
        {
            CompileResult result = Compile(Boot(declaration));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            var decl = result.Syntax!.Classes[0].Body.OfType<VarDecl>().Single();
            Assert.Equal(count, decl.Values.Count);
        }

        [Fact]
        public void SpacedMinusStillSubtractsOutsideLists()
        {
            CompileResult result = Compile(Boot("int a: 5;\na is a -1;"));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            var assign = result.Syntax!.Classes[0].Body.OfType<AssignStmt>().Single();
            Assert.IsType<BinaryExpr>(assign.Value);
        }

        [Theory]
        [InlineData("int s: sizeof str;", "the size of a str depends on its declaration")]
        [InlineData("int s: sizeof Missing;", "unknown name 'Missing'")]
        [InlineData("str days: 'Mon' 'Tue';\nint s: sizeof days;", "the size of a str depends on its declaration")]
        [InlineData("int sizeof: 1;", "'sizeof' is a keyword")]
        public void SizeofErrors(string body, string message) => AssertError(Boot(body), message);

        [Theory]
        [InlineData("break;", "break can only be used inside a while loop")]
        [InlineData("continue;", "continue can only be used inside a while loop")]
        [InlineData("return;", "return can only be used inside a method")]
        [InlineData("call Show;\n[Show]\n return 1;\n[end]", "Show doesn't return a value (it ends with [end]), so return can't take one")]
        [InlineData("[Get]\n return;\n[end: 1]", "Get returns a value (it ends with [end: value]), so return needs one")]
        [InlineData("[Get]\n return 'x';\n[end: 1]", "Get returns a number (from its [end: value]), but this is a str")]
        [InlineData("int x: (Show);\n[Show]\n[end]", "Show doesn't return a value (it ends with [end]), so it can't be used as one")]
        [InlineData("int list: 1 2;\nint x: (list: 1);", "'list' isn't a method; for an element, leave out the space after ':' (list:i)")]
        [InlineData("int x: (Missing: 1);", "unknown method 'Missing'")]
        [InlineData("int x: (Twice: 1 2);\n[Twice: int n: 0;]\n[end: n * 2]", "Twice takes 1 argument but was given 2")]
        public void EarlyExitAndCallErrors(string body, string message) => AssertError(Boot(body), message);

        [Theory]
        [InlineData("[Count: int n: 0;]\n call Count: n - 1;\n[end]", "Count calls itself (Count -> Count)")]
        [InlineData("[Count: int n: 0;]\n[end: (Count: n - 1)]", "Count calls itself (Count -> Count)")]
        [InlineData("[Ping]\n call Pong;\n[end]\n[Pong]\n call Ping;\n[end]", "Ping calls itself (Ping -> Pong -> Ping)")]
        public void RecursionIsReported(string body, string message) => AssertError(Boot(body), message);

        [Fact]
        public void ParenthesizedVariablesStayVariables()
        {
            // (x) is a variable when one has the name; (list:i) with no space is an element
            CompileResult result = Compile(Boot("const int SIZE: 4;\nint list: 1 2 3 4;\nint a: (SIZE);\nint b: (list:1) + list:(SIZE - 1);"));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        }

        [Fact]
        public void ArraysBecomePointersOnlyWhereAPointerIsExpected()
        {
            AssertError(Boot("u8 buf[4];\nint n: 0;\ncall n is Count: buf;\n[Count: int p: 0;]\n[end: p]"),
                "needs a number, but this is a u8 array");
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
            var vars = result.Bound!.Boot!.Variables.ToDictionary(v => v.Name);
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
            var vars = result.Bound!.Boot!.Variables.ToDictionary(v => v.Name);
            Assert.False(vars["a"].NeedsInitCode);
            Assert.True(vars["b"].NeedsInitCode);
        }

        [Fact]
        public void BootAndKernelBecomeSeparateUnits()
        {
            CompileResult result = Compile("phi.B:Bootloader\n{\n call Bootloader.JumpToSectorTwo;\n}\nphi.K:OS\n{\n log 'k';\n}\n");
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            Assert.Equal(new[] { UnitKind.Boot, UnitKind.Os }, result.Units.Select(u => u.Kind));
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
            new[] { "hello.phi", "arcade.phi", "kernel.phi", "terminal.phi", "os.phi", "web.phi", "music.phi" }.Select(f => new object[] { f });

        // net.net and net.tcp use each other: checking one from disk (as an editor does, by its
        // full path) must not load it a second time as the embedded copy
        [Theory]
        [InlineData("net/tcp.phi")]
        [InlineData("net/net.phi")]
        [InlineData("net/http.phi")]
        public void LibraryFilesThatUseEachOtherCompileFromDisk(string file)
        {
            string path = Path.GetFullPath(Path.Combine(RepoRoot(), "lib", "phi", file));
            CompileResult result = PhiCompiler.Compile(path);
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        }

        [Theory]
        [MemberData(nameof(RunnableSamples))]
        public void SamplesCompile(string sample)
        {
            CompileResult result = PhiCompiler.Compile(Path.Combine(RepoRoot(), "samples", sample));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        }

        public static IEnumerable<object[]> TutorialExamples() =>
            Directory.GetFiles(Path.Combine(RepoRoot(), "docs", "examples"), "*.phi").Select(f => new object[] { Path.GetFileName(f) });

        [Theory]
        [MemberData(nameof(TutorialExamples))]
        public void TutorialExamplesCompile(string example)
        {
            CompileResult result = PhiCompiler.Compile(Path.Combine(RepoRoot(), "docs", "examples", example));
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
