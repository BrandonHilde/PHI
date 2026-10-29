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
        [InlineData("int sizeof: 1;", "'sizeof' is a keyword and can't be a variable name; try 'size'")]
        public void SizeofErrors(string body, string message) => AssertError(Boot(body), message);

        [Theory]
        [InlineData("break;", "break can only be used inside a while loop")]
        [InlineData("continue;", "continue can only be used inside a while loop")]
        [InlineData("return;", "return can only be used inside a method")]
        [InlineData("call Show;\n[Show]\n return 1;\n[end]", "Show doesn't return a value (it ends with [end]), so return can't take one")]
        [InlineData("[Get]\n return;\n[end: 1]", "Get returns a value (it ends with [end: value]), so return needs one")]
        [InlineData("[Get]\n return 'xy';\n[end: 1]", "Get returns a number (from its [end: value]), but this is a str")]        [InlineData("int x: (Show);\n[Show]\n[end]", "Show doesn't return a value (it ends with [end]), so it can't be used as one")]
        [InlineData("int list: 1 2;\nint x: (list: 1);", "'list' isn't a method; for an element, leave out the space after ':' (list:i)")]
        [InlineData("int x: (Missing: 1);", "unknown method 'Missing'")]
        [InlineData("int x: (Twice: 1 2);\n[Twice: int n: 0;]\n[end: n * 2]", "Twice takes 1 argument but was given 2")]
        [InlineData("call M: 1;\n[M: int c: 0;]\nif c is 1 return;\nelse\n log 'x';\n;;\n[end]", "an if on one line can't have an else; write the if as a block, ending in ;;")]
        public void EarlyExitAndCallErrors(string body, string message) => AssertError(Boot(body), message);

        [Fact]
        public void AnAssignmentInAOneLineIfIsReportedAtTheIf()
        {
            // it used to read as a block that never closed, reported at the end of the method
            CompileResult result = Compile(Boot("call M: 1;\n[M: int k: 0;]\nint tool: 0;\nif k is 1 tool is 2;\nif k is 2\n log 'y';\n;;\n[end]"));
            Diagnostic error = Assert.Single(result.Diagnostics);
            Assert.Contains("an if on one line can only return, break, continue, call or exit", error.Message);
            Assert.Equal(6, error.File!.GetLocation(error.Span.Start).Line);
        }

        [Theory]
        [InlineData("[Count: int n: 0;]\n if n > 0\n  call Count: n - 1;\n ;;\n[end]")]
        [InlineData("[Factorial: int n: 0;]\n if n < 2\n  return 1;\n ;;\n[end: n * (Factorial: n - 1)]")]
        [InlineData("[Ping: int n: 0;]\n if n > 0\n  call Pong: n - 1;\n ;;\n[end]\n[Pong: int n: 0;]\n call Ping: n;\n[end]")]
        public void MethodsCanCallThemselves(string body)
        {
            CompileResult result = Compile(Boot(body));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        }

        [Theory]
        [InlineData("[Echo: str t: [8];]\n[end: t]\n[Name: int n: 0;]\n[end: (Echo: (Name: n))]", "Name returns a str and uses its own result in [end: ...]")]
        [InlineData("call OS.SetInterruptHandler: 9 addr Handler;\n[Handler: int x: 0;]\n[end]", "Handler takes parameters, so OS.SetInterruptHandler can't call it")]
        [InlineData("[Pad: int width: 0; int fill: width;]\n[end]", "a parameter's default can't use the method's own variables or parameters ('width')")]
        [InlineData("[Big]\n u8 buffer[5000];\n[end]", "Big needs 5000 bytes of stack for its variables (at most 4096); declare large buffers in the class instead")]
        public void FrameErrors(string body, string message) => AssertError(Boot(body), message);

        [Theory]
        [InlineData("method<int: int> f: addr Add;\n[Add: int a: 0; int b: 0;]\n[end: a + b]", "'f' needs a method<int: int>, but this is a method<int int: int>")]
        [InlineData("method<int> f: 5;", "'f' needs a method<int>, like addr Name")]
        [InlineData("method<int: int> f;\nint x: (f: 1 2);", "f is a method<int: int>, which takes 1 argument, but was given 2")]
        [InlineData("method<int> f;\nint x: (f: 1);", "f is a method<int>, which doesn't return a value, so it can't be used as one")]
        [InlineData("int n: 3;\ncall n: 1;", "'n' is a int, not a method")]
        [InlineData("method<: str> f;", "a method called through a variable can't return a str yet")]
        [InlineData("call Show:x;\nint x: 1;\n[Show: int v: 0;]\n[end]", "put a space after the ':' that starts the arguments: Show: ...")]
        public void MethodPointerErrors(string body, string message) => AssertError(Boot(body), message);

        [Fact]
        public void PointersOfDifferentKindsWarnUnlessCast()
        {
            string structs = "struct.A\n{\n int x;\n}\nstruct.B\n{\n int y;\n}\n";
            CompileResult warned = Compile(structs + Boot("A a;\nptr<A> pa: addr a;\nptr<B> pb: pa;"));
            Assert.True(warned.Success);
            Assert.Contains(warned.Diagnostics, d => d.Severity == Severity.Warning && d.Message.Contains("write `as ptr<B>` if that's meant"));

            // with as, or through ptr<u8> (bytes), there's nothing to warn about
            CompileResult quiet = Compile(structs + Boot("A a;\nptr<A> pa: addr a;\nptr<B> pb: pa as ptr<B>;\nptr<u8> bytes: pa;\nptr<B> pc: bytes;"));
            Assert.Empty(quiet.Diagnostics);
        }

        [Theory]
        [InlineData("str s: 'x';\nint n: s as int;", "a str can't be used as a int; only numbers, pointers and methods can")]
        [InlineData("int n: 5 as str;", "str can't be used here")]
        public void CastErrors(string body, string message) => AssertError(Boot(body), message);

        [Theory]
        [InlineData("call M;\n[M]\nbln has: 1;\nif has\n log 'y';\n;;\n[end]", "'has' is a keyword and can't be a variable name")]
        [InlineData("int when: 1;", "'when' is a keyword and can't be a variable name")]
        [InlineData("i64 n: 1;", "i64 needs 32-bit code (a Kernel or a Program)")]
        [InlineData("f32 n: 1;", "f32 needs 32-bit code (a Kernel or a Program)")]
        [InlineData("int scores[];", "an array without a size grows on the heap, which needs 32-bit code")]
        [InlineData("int n: 5;\nwhile each x in n\n log x;\n;;", "while each goes through an array, and this is an int")]
        [InlineData("int a: 1 2;\nwhile each x a\n log x;\n;;", "expected 'in' and an array, like while each x in list")]
        [InlineData("int each: 1;", "'each' is a keyword and can't be a variable name")]
        [InlineData("log 2.5;", "numbers with a point need 32-bit code")]
        [InlineData("int n: 99999999999999999999;", "number is not a valid 64-bit value")]
        [InlineData("int enum: 1;", "'enum' is a keyword and can't be a variable name")]
        [InlineData("int k: 1;\nwhen k\n log 'x';\n;;", "expected 'is' (a branch), 'else' or ';;'")]
        [InlineData("if Ok: 3\n log 'y';\n;;\n[Ok: int n: 0;]\n[end: n > 1]", "'Ok' is a method; to use its result as a value, put the call in parentheses: (Ok: 3)")]
        [InlineData("int n: Seven;\n[Seven]\n[end: 7]", "put the call in parentheses: (Seven)")]
        [InlineData("u8 text[8];\nlog text;", "can't print a u8 array; to print the text in it (up to a zero byte), write addr text")]
        public void MistakesGetMessagesThatNameThem(string body, string message) => AssertError(Boot(body), message);

        [Theory]
        [InlineData("ptr<u8> kept: (Pick: 1);")]
        [InlineData("ptr<u8> kept: '';\nkept is (Pick: 1);")]
        [InlineData("ptr<u8> kept: '';\nkept is (Name);\n[Name]\n[end: 'x']")]
        public void AStrResultCantBeKeptByAPointer(string body)
        {
            string methods = "\n[Pick: int n: 0;]\n if n > 1 return 'first';\n[end: 'second']";
            AssertError(Boot(body + methods), "returns a str, which only lasts until the end of this statement");
        }

        [Fact]
        public void AStrResultCanBePassedAsAPointer()
        {
            CompileResult result = Compile(Boot("call Show: (Pick);\nstr kept: [8];\nkept is (Pick);\n[Show: ptr<u8> text: 0;]\n log text;\n[end]\n[Pick]\n[end: 'second']"));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        }

        [Theory]
        [InlineData("call M;\n[M]\nbln has: 1;\nif has\n log 'y';\n;;\nelse\n log 'n';\n;;\n[end]")]
        [InlineData("int out: 12;\nout++ 4;\nout is out * 2;\nout--;\nlog out;")]
        [InlineData("u8 free[4];\nfree:0 is 1;\nfree:1 is free:0;")]
        [InlineData("call M: 1;\n[M: int out: 0;]\nout++;\n[end]")]
        public void AKeywordAsANameIsReportedOnce(string body)
        {
            // its later uses read as the name, instead of each causing errors of their own
            CompileResult result = Compile(Boot(body));
            Assert.Single(result.Diagnostics);
        }

        [Theory]
        [InlineData("int x: 5;\ncall Show: x -1;\n[Show: int a: 0; int b: 0;]\n[end]", "'x -1' is two arguments, x and -1")]
        [InlineData("int h: 64;\nbln b: 0;\nb is h is 64;", "this stores whether h is 64 in 'b'")]
        [InlineData("str info: [24];\ncall Fill: info info.len;\n[Fill: ptr<u8> to: 0; int size: 0;]\n[end]", "'info.len' is how much text is in info now")]
        [InlineData("str home: 'HOME';\nstr docs: home '/DOCS';", "'docs' is a str array of 2 (several values make an array)")]
        public void EasilyMisreadCodeWarns(string body, string message)
        {
            CompileResult result = Compile(Boot(body));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            Assert.Contains(result.Diagnostics, d => d.Severity == Severity.Warning && d.Message.Contains(message));
        }

        [Fact]
        public void AnAsmBlockInsideAClassSaysWhereItGoes() =>
            AssertError(Boot("asm.Beep\n{\n\tret\n}"), "asm. blocks go at the top level of the file, outside any class");

        [Fact]
        public void AStrArrayIsntTextWhereAPtrU8Goes()
        {
            AssertError(Boot("str days: 'Mon' 'Tue';\ncall Show: days;\n[Show: ptr<u8> text: 0;]\n[end]"),
                        "parameter 'text' is text (a ptr<u8>), but 'days' is a str array");

            // one element is text; an array of literals doesn't warn
            CompileResult element = Compile(Boot("str days: 'Mon' 'Tue';\ncall Show: days:0;\n[Show: ptr<u8> text: 0;]\n[end]"));
            Assert.True(element.Success, string.Join("\n", element.Diagnostics));
            Assert.DoesNotContain(element.Diagnostics, d => d.Severity == Severity.Warning);
        }

        [Theory]
        [InlineData("int x: 5;\ncall Show: 5 -1;\ncall Show: x - 1 (-1);\n[Show: int a: 0; int b: 0;]\n[end]")]
        [InlineData("int h: 64;\nbln b: 0;\nb is (h is 64);\nb = h is 64;")]
        public void ClearlyWrittenCodeDoesNotWarn(string body)
        {
            CompileResult result = Compile(Boot(body));
            Assert.Empty(result.Diagnostics);
        }

        [Fact]
        public void ANameFromAnotherFilesClassWarnsUntilItsClassIsWritten()
        {
            string dir = Path.Combine(Path.GetTempPath(), "phi-names-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "state.phi"), "phi.State:Library\n{\n int dirty: 0;\n}\n");
                string main = Path.Combine(dir, "main.phi");

                File.WriteAllText(main, "use state;\nphi.Main:Kernel\n{\n dirty is 1;\n log dirty;\n}\n");
                CompileResult loose = PhiCompiler.Compile(main);
                Assert.True(loose.Success, string.Join("\n", loose.Diagnostics));
                Diagnostic warning = Assert.Single(loose.Diagnostics);
                Assert.Contains("'dirty' is a variable of State, in state.phi; write State.dirty to say so", warning.Message);

                File.WriteAllText(main, "use state;\nphi.Main:Kernel\n{\n State.dirty is 1;\n log State.dirty;\n}\n");
                Assert.Empty(PhiCompiler.Compile(main).Diagnostics);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void ANameFromAnotherClassInTheSameFileDoesNotWarn()
        {
            CompileResult result = Compile("phi.State:Library\n{\n int dirty: 0;\n}\nphi.Main:Kernel\n{\n dirty is 1;\n}\n");
            Assert.Empty(result.Diagnostics);
        }

        [Fact]
        public void ANameCanBeDeclaredAgainInASeparateBlock()
        {
            CompileResult result = Compile(Boot("""
                call M: 1;
                [M: int c: 0;]
                if c < 2
                    while int i: 0; i < 3; i++;
                    ;;
                ;;
                else
                    str i: 'x';
                    log i;
                ;;
                int i: 7;
                [end]
                """));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        }

        [Theory]
        [InlineData("if c < 2\n int t: 1;\n;;\nelse\n int t: 2;\n;;\nlog t;", "'t' is declared only inside blocks (if, else, while) that have ended here")]
        [InlineData("int t: 0;\nif c < 2\n int t: 1;\n;;", "'t' is already declared in method Test.M; a name can be declared again only in a separate block")]
        public void BlockScopeErrors(string body, string message) => AssertError(Boot("call M: 1;\n[M: int c: 0;]\n" + body + "\n[end]"), message);

        [Fact]
        public void AStrArrayIsPassedAsItsTableOfPointers()
        {
            CompileResult result = Compile(Boot("str rows: 'ab' 'c';\ncall Lines: rows;\n[Lines: ptr<ptr<u8>> lines: 0;]\nlog lines:0;\n[end]"));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            Assert.Empty(result.Diagnostics);
        }

        const string CounterStruct = "struct.Counter\n{\n int count;\n [Add: int n: 1;]\n  count++ n;\n [end: count]\n}\n";

        [Theory]
        [InlineData("int x: (Counter.Add: 1);", "Add is a method of struct Counter; call it on one, like value.Add")]
        [InlineData("Counter c;\ncall c.Add: 1 2;", "Counter.Add takes 1 argument but was given 2")]
        [InlineData("Counter c;\ncall c.Missing;", "unknown method 'c.Missing'")]
        public void StructMethodErrors(string body, string message) => AssertError(CounterStruct + Boot(body), message);

        [Fact]
        public void StructMethodsInABootSectorStayInIt()
        {
            // a boot sector alone doesn't grow an OS unit for the struct's methods
            CompileResult result = Compile(CounterStruct + Boot("Counter c;\nint n: (c.Add: 2);"));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            Assert.Equal(UnitKind.Boot, Assert.Single(result.Units).Kind);
        }

        [Fact]
        public void HandlersCantTakeParameters()
        {
            string kernel = "phi.K:Kernel\n{\ncall OS.SetIrqHandler: 1 addr Key;\n[Key: int code: 0;]\n[end]\n}\n";
            AssertError(kernel, "Key takes parameters, so OS.SetIrqHandler can't call it");
        }

        [Fact]
        public void ParametersAndLocalsLiveInTheFrame()
        {
            CompileResult result = Compile(Boot("int r: 0;\ncall r is Add: 2 3;\n[Add: int a: 0; int b: 0;]\n int sum: a + b;\n[end: sum]"));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            string asm = result.Units.Single().Text;
            Assert.Contains("push ebp", asm);
            Assert.Contains("[ebp + 6]", asm);   // b, the last argument (16-bit: 2-byte return address)
            Assert.Contains("[ebp - 4]", asm);   // sum
            Assert.DoesNotContain("VALUE_M_Test_Add_a:", asm);
        }

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
        [InlineData("str s: 'a';\ns-- 1;", "only works on numbers")]
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
        [InlineData("str s: [9];\ns is 'a'\ns is 'b';", "expected ';'")]      // only a deeper line carries on the values
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
            new[] { "arcade.phi", "kernel.phi", "terminal.phi", "os.phi", "web.phi", "music.phi" }.Select(f => new object[] { f });

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
