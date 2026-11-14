using Phi.Compiler.Language;
using Xunit;

namespace Phi.Compiler.Tests
{
    public class FormatterTests
    {
        static string Format(string text)
        {
            var diagnostics = new DiagnosticBag();
            string? laid = Formatter.Format(new SourceFile("test.phi", text), diagnostics);
            Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
            return laid!;
        }

        /// <summary>A class around some lines, with the tabs it has when it's laid out.</summary>
        static string InClass(params string[] lines) =>
            "phi.Test:Program\n{\n" + string.Concat(lines.Select(l => "\t" + l + "\n")) + "}\n";

        [Fact]
        public void IndentsBlocksByWhatTheyreIn()
        {
            string messy = """
                phi.Test:Program
                {
                      int a: 1;
                  if a is 1
                 log 'one';
                      while a < 3
                   a++;
                         ;;
                ;;
                  elif a is 2
                log 'two';
                    ;;
                  else
                log 'other';
                       ;;
                    [Method: int x: 0;]
                  return x;
                    [end: x]
                }

                """.Replace("\r\n", "\n");
            Assert.Equal(InClass(
                "int a: 1;",
                "if a is 1",
                "\tlog 'one';",
                "\twhile a < 3",
                "\t\ta++;",
                "\t;;",
                ";;",
                "elif a is 2",
                "\tlog 'two';",
                ";;",
                "else",
                "\tlog 'other';",
                ";;",
                "[Method: int x: 0;]",
                "\treturn x;",
                "[end: x]"), Format(messy));
        }

        [Fact]
        public void AnIfOnOneLineOpensNothing()
        {
            string laid = InClass("int a: 1;", "if a is 1 exit 0;", "log a;", "while int i: 0; i < 3; i++;", "\tif i is 2 break;", "\tlog i;", ";;");
            Assert.Equal(laid, Format(laid));
        }

        [Fact]
        public void WhenBranchesEachOpenABlock()
        {
            string laid = InClass("int a: 1;", "when a", "\tis 1", "\t\tlog 'one';", "\t;;", "\tis 2 3", "\t\tlog 'more';", "\t;;", "\telse", "\t\tlog 'other';", "\t;;", ";;");
            Assert.Equal(laid, Format(laid));
        }

        [Fact]
        public void ACatchOpensABlockAndOneOnOneLineDoesnt()
        {
            string laid = InClass("if call Go throws err", "\tlog err;", ";;", "if call Go throws err return;", "log 'on';", "[Go]", "[end]");
            Assert.Equal(laid, Format(laid));
        }

        [Fact]
        public void StructsInterfacesAndEnums()
        {
            string laid = "interface.Shape\n{\n\t[Area: : int]\n\t[Grow: int by;]\n}\n\n" +
                          "struct.Circle has Shape\n{\n\tint r;\n\n\t[Area]\n\t[end: r]\n\n\t[Grow: int by;]\n\t\tr++ by;\n\t[end]\n}\n\n" +
                          "enum.Tool\n{\n\tPENCIL\n\tBRUSH: 5\n}\n";
            Assert.Equal(laid, Format(laid));
        }

        [Fact]
        public void ALineThatCarriesOnStaysDeeper()
        {
            // deeper is what says it carries on, so it has to stay deeper
            Assert.Equal(InClass("int a: 1 2", "\t3 4;", "log a:3;"), Format("phi.Test:Program\n{\n      int a: 1 2\n          3 4;\n  log a:3;\n}\n"));
            Assert.Equal(InClass("int a: (1 + 2", "\t\t+ 3);"), Format(InClass("int a: (1 + 2", "\t\t+ 3);")));
        }

        [Fact]
        public void CommentsGoAtTheDepthOfTheCodeAfterThem()
        {
            string laid = "# the file\nphi.Test:Program\n{\n\t# a variable\n\tint a: 1;     # beside it stays\n\tif a is 1\n\t\t# inside\n\t\tlog a;\n\t\t# at the end of the block\n\t;;\n}\n";
            Assert.Equal(laid, Format(laid.Replace("\t", "   ")));
        }

        [Fact]
        public void TheInsideOfAsmABlockCommentAndAnEmptyLineStayOrEmpty()
        {
            string laid = "asm.Go\n{\n      mov eax, 1   ; as it was\n  ret\n}\n\n#\n      a block comment\n  stays as it is\n#\nphi.Test:Program\n{\n\tcall Go;\n}\n";
            Assert.Equal(laid, Format(laid));
            Assert.Equal("phi.Test:Program\n{\n\tint a: 1;\n\n\tlog a;\n}\n", Format(InClass("int a: 1;   ", "   ", "log a;") + "\n\n"));
        }

        [Theory]
        [InlineData("call Add: x y + 1 z;", "call Add: x (y + 1) z;")]              // what's asked for
        [InlineData("call Add: x y;", "call Add: x y;")]                            // nothing to count
        [InlineData("call Add: x + 1;", "call Add: x + 1;")]                        // only one
        [InlineData("call Add: x (y + 1);", "call Add: x (y + 1);")]                // already
        [InlineData("call Add: (x + 1) * 2 y;", "call Add: ((x + 1) * 2) y;")]      // its first ) closes early
        [InlineData("call Add: x -1;", "call Add: x -1;")]                          // two values
        [InlineData("call Add: x as int y;", "call Add: x as int y;")]              // not an expression to count
        [InlineData("int a: (Add: x + 1 y);", "int a: (Add: (x + 1) y);")]          // as a value
        [InlineData("log (Add: x y * 2) x + 1;", "log (Add: x (y * 2)) x + 1;")]    // log's own list is left
        public void AnExpressionInACallWithSeveralArgumentsGetsParentheses(string written, string laid)
        {
            string Program(string line) => InClass("int x: 1;", "int y: 2;", "int z: 3;", line, "[Add: int a: 0; int b: 0; int c: 0;]", "[end: a]");
            Assert.Equal(Program(laid), Format(Program(written)));
        }

        [Fact]
        public void KeepsItsLineEndingsAndEndsWithOneNewline()
        {
            Assert.Equal("phi.Test:Program\r\n{\r\n\tlog 1;\r\n}\r\n", Format("phi.Test:Program\r\n{\r\n  log 1;  \r\n}"));
            Assert.Equal("phi.Test:Program\n{\n\tlog 1;\n}\n", Format("phi.Test:Program\n{\n\tlog 1;\n}\n\n\n"));
        }

        [Fact]
        public void LeavesAFileWithErrorsAlone()
        {
            var diagnostics = new DiagnosticBag();
            Assert.Null(Formatter.Format(new SourceFile("test.phi", "phi.Test:Program\n{\n\tif\n}\n"), diagnostics));
            Assert.True(diagnostics.HasErrors);
        }

        // ---------------------------------------------------------------- on real code

        static string RepoRoot()
        {
            DirectoryInfo? dir = new(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PHI.sln"))) dir = dir.Parent;
            return dir!.FullName;
        }

        /// <summary>The standard library laid out, to read in place of itself.</summary>
        static Dictionary<string, string> LaidOutLibrary()
        {
            string root = Path.Combine(RepoRoot(), "lib", "phi");
            var open = new Dictionary<string, string>();
            foreach (string path in Directory.GetFiles(root, "*.phi", SearchOption.AllDirectories))
            {
                var diagnostics = new DiagnosticBag();
                string? laid = Formatter.Format(SourceFile.Load(path), diagnostics);
                if (laid != null) open["lib/phi/" + Path.GetRelativePath(root, path).Replace('\\', '/')] = laid;
            }
            return open;
        }

        /// <summary>What a compile made, without its comments (which quote the source).</summary>
        static string Code(CompileResult result) =>
            string.Join("\n", result.Units.SelectMany(u => u.Text.Replace("\r\n", "\n").Split('\n')).Where(l => !l.TrimStart().StartsWith(';')));

        public static IEnumerable<object[]> Programs()
        {
            string[] tests = Directory.GetFiles(Path.Combine(RepoRoot(), "tests"), "*.phi").OrderBy(p => p).ToArray();
            // every one takes a minute or so; PHI_FMT_ALL=1 runs them all, after changing the formatter
            bool all = Environment.GetEnvironmentVariable("PHI_FMT_ALL") != null;
            for (int i = 0; i < tests.Length; i += all ? 1 : 25)
                yield return new object[] { tests[i] };
        }

        [Theory]
        [MemberData(nameof(Programs))]
        public void LaidOutCodeCompilesToTheSameCode(string path)
        {
            Host host = path.EndsWith("_program.phi") ? Host.Linux64 : Host.PhiOS;
            CompileResult before = PhiCompiler.Compile(SourceFile.Load(path), host: host);
            if (!before.Success) return;    // (a test of errors, or of something only a later step adds)
            // (a compiled library is held to the standard library's modules as they are, and here
            // they're laid out again, which adds parentheses: it's refused, rightly, for that alone)
            if (File.ReadAllText(path).Contains("# library:")) return;

            string text = File.ReadAllText(path);
            var diagnostics = new DiagnosticBag();
            string? laid = Formatter.Format(new SourceFile(path, text), diagnostics);
            Assert.NotNull(laid);
            CompileResult after = PhiCompiler.Compile(new SourceFile(path, laid!), host: host, open: LaidOutLibrary());
            Assert.True(after.Success, string.Join("\n", after.Diagnostics));
            Assert.Equal(Code(before), Code(after));

            Assert.Equal(laid, Formatter.Format(new SourceFile(path, laid!), new DiagnosticBag()));
        }
    }
}
