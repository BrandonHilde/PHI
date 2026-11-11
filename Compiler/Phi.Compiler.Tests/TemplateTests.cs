using Phi.Compiler.Syntax;

namespace Phi.Compiler.Tests
{
    /// <summary>Structs that take types (struct.Stack&lt;Item&gt;): how they're read, and what a use of one becomes.</summary>
    public class TemplateTests
    {
        const string Stack = """
            struct.Stack<Item>
            {
            	Item items[];

            	[Push: Item item;]
            		items++ item;
            	[end: items.len]
            }

            """;

        static CompileResult Compile(string text, Host host = Host.Linux64, IReadOnlyDictionary<string, string>? open = null) =>
            PhiCompiler.Compile(new SourceFile(Path.GetFullPath("test.phi"), text), generate: false, host: host, open: open);

        static string Errors(CompileResult result) => string.Join("\n", result.Diagnostics.Select(d => d.ToString()));

        [Fact]
        public void EachUseIsAStructOfItsOwnNamedAsItsWritten()
        {
            CompileResult result = Compile(Stack + "phi.T:Program\n{\n\tnew Stack<int> a;\n\tnew Stack<u8> b;\n\tnew Stack<int> again;\n\tlog (a.Push: 1) (b.Push: 2) (again.Push: 3);\n}\n");
            Assert.True(result.Success, Errors(result));
            var names = result.Syntax!.Structs.Select(s => s.Name).Where(n => n.StartsWith("Stack")).ToList();
            Assert.Equal(new[] { "Stack<int>", "Stack<u8>" }, names);
            // (the template itself is no struct: nothing can be a Stack of nothing)
            Assert.Single(result.Syntax.Templates);
        }

        [Theory]
        [InlineData("Stack<int>", "Stack_of_int")]
        [InlineData("Map<str ptr<u8>>", "Map_of_str_ptr_of_u8")]
        [InlineData("Pair<int Stack<u8>>", "Pair_of_int_Stack_of_u8")]
        [InlineData("Player", "Player")]
        public void ItsNameInALabelHasOnlyWhatALabelMay(string name, string label) => Assert.Equal(label, Templates.Label(name));

        [Theory]
        // less than and shifts, where a name is followed by '<', stay what they were
        [InlineData("int a: 1;\n\tint b: 2;\n\tint c: 3;\n\tif a < b and c > a\n\t\tlog 'yes';\n\t;;")]
        [InlineData("int a: 1;\n\tint n: 8;\n\twhile a < n >> 1\n\t\ta++;\n\t;;")]
        [InlineData("u32 a: 1;\n\tu32 b: 2;\n\tbln less: a as int < b as int;\n\tlog less;")]
        [InlineData("int a: 1;\n\tint b: 2;\n\tlog (a < b) (a << 2 > b);")]
        public void ANameThenLessThanIsStillAComparison(string body)
        {
            CompileResult result = Compile(Stack + "phi.T:Program\n{\n\t" + body + "\n}\n");
            Assert.True(result.Success, Errors(result));
            Assert.Empty(result.Syntax!.TemplateUses);
        }

        [Theory]
        [InlineData("new Stack<int> a;\n\tptr<Stack<int>> p: addr a;\n\tlog (p:0.Push: 4);")]                       // '>>' closes both
        [InlineData("new Stack<ptr<u8>> names;\n\tlog (names.Push: 'ann');")]
        [InlineData("new Stack<ptr<ptr<u8>>> deep;\n\tlog sizeof Stack<ptr<ptr<u8>>>;")]
        [InlineData("new Stack<method<int: int>> calls;\n\tlog calls.items.len;")]
        public void TypesInsideTypesClose(string body)
        {
            // (as a 32-bit program: 64-bit code can't yet add to an array of methods, with or without this)
            CompileResult result = Compile(Stack + "phi.T:Program\n{\n\t" + body + "\n}\n", Host.Linux);
            Assert.True(result.Success, Errors(result));
        }

        [Fact]
        public void ATemplateInAFileThatsUsedWorksAsOneInTheSameFile()
        {
            string shared = Path.GetFullPath("shared_stack.phi");
            var open = new Dictionary<string, string> { [PhiCompiler.FileKey(shared)] = Stack };
            CompileResult result = Compile("use shared_stack;\n\nphi.T:Program\n{\n\tnew Stack<int> a;\n\tlog (a.Push: 1);\n}\n", open: open);
            Assert.True(result.Success, Errors(result));
            Assert.Contains(result.Syntax!.Structs, s => s.Name == "Stack<int>");
        }

        [Fact]
        public void ATemplateNobodyUsesIsOnlyReadForItsSyntax()
        {
            // (what's wrong inside it shows with its first use: there's no type to check it with before)
            CompileResult unused = Compile("struct.Odd<Item>\n{\n\tItem one;\n\t[Get]\n\t[end: missing]\n}\n\nphi.T:Program\n{\n\tlog 'hi';\n}\n");
            Assert.True(unused.Success, Errors(unused));
            CompileResult broken = Compile("struct.Odd<Item>\n{\n\tItem one\n}\n\nphi.T:Program\n{\n\tlog 'hi';\n}\n");
            Assert.Contains(broken.Diagnostics, d => d.Message.Contains("expected ';'"));
        }

        [Fact]
        public void AnErrorInATemplateIsSaidOnceWithTheUseItWasFor()
        {
            CompileResult result = Compile("struct.Odd<Item>\n{\n\tItem one;\n\t[Get]\n\t[end: missing]\n}\n\nphi.T:Program\n{\n\tOdd<int> a;\n\tOdd<u8> b;\n\tlog (a.Get) (b.Get);\n}\n");
            var errors = result.Diagnostics.Where(d => d.Message.Contains("missing")).ToList();
            Assert.Single(errors);
            Assert.Contains("(in Odd<int>, which line 10 of test.phi uses)", errors[0].Message);
            Assert.Equal(5, errors[0].File!.GetLocation(errors[0].Span.Start).Line);
        }

        const string Spots = """
            struct.Spot
            {
            	int x;
            }

            struct.Pile<Item>
            {
            	Item items[];

            	[Push: Item item;]
            		items++ item;
            	[end: items.len]

            	[Top]
            		Item top: items:(items.len - 1);
            	[end: top]

            	[TopTwice]
            	[end: (Top)]
            }

            """;

        [Fact]
        public void AMethodThatDoesntSuitATypeIsLeftOutWhileNothingUsesIt()
        {
            // (Top starts an Item with a value, which a struct variable can't be)
            CompileResult unused = Compile(Spots + "phi.T:Program\n{\n\tnew Pile<Spot> p;\n\tSpot s;\n\tlog (p.Push: s);\n}\n");
            Assert.True(unused.Success, Errors(unused));
            var methods = unused.Bound!.Program!.Methods.Select(m => m.Name).Where(n => n.StartsWith("Pile<Spot>")).ToList();
            Assert.Equal(new[] { "Pile<Spot>.Push" }, methods);

            // the same struct for a type they do suit keeps them all
            CompileResult suited = Compile(Spots + "phi.T:Program\n{\n\tnew Pile<int> p;\n\tlog (p.Push: 1);\n}\n");
            Assert.Equal(3, suited.Bound!.Program!.Methods.Count(m => m.Name.StartsWith("Pile<int>")));
        }

        [Theory]
        [InlineData("log (p.Top);")]              // used from the program
        [InlineData("log (p.TopTwice);")]         // used by a method that's used
        public void OneThatIsUsedSaysWhatsWrong(string use)
        {
            CompileResult result = Compile(Spots + "phi.T:Program\n{\n\tnew Pile<Spot> p;\n\t" + use + "\n}\n");
            Assert.Contains(result.Diagnostics, d => d.Message.Contains("a Spot starts as all zeros") && d.Message.Contains("(in Pile<Spot>, which line"));
        }

        [Fact]
        public void HashOfIsTheHashForTheValuesType()
        {
            string program = "use data.hash;\n\nphi.T:Program\n{\n\tstr s: 'a';\n\ti64 w: 5;\n\tlog (Hash.Of: 5) (Hash.Of: s) (Hash.Of: w);\n}\n";
            CompileResult result = Compile(program);
            Assert.True(result.Success, Errors(result));
            var hash = result.Bound!.Program!.Methods.Where(m => m.Name.StartsWith("Hash.Of")).ToList();
            Assert.All(new[] { "Hash.OfNumber", "Hash.OfText", "Hash.OfWide" }, name => Assert.Contains(hash, m => m.Name == name && (m.UsedOutsideMethods || m.UsedBy.Count > 0)));

            // a value that's wrong is said to be once, and more than one value isn't a hash of anything
            CompileResult outside = Compile("use data.hash;\n\nphi.T:Program\n{\n\tint n: 1 2 3;\n\tlog (Hash.Of: n:7);\n}\n");
            Assert.Single(outside.Diagnostics, d => d.Message.Contains("index 7 is outside"));
            CompileResult two = Compile("use data.hash;\n\nphi.T:Program\n{\n\tlog (Hash.Of: 1 2);\n}\n");
            Assert.Contains(two.Diagnostics, d => d.Message.Contains("Hash.Of takes one value, and was given 2"));

            CompileResult real = Compile("use data.hash;\n\nphi.T:Program\n{\n\tf64 r: 1.5;\n\tlog (Hash.Of: r);\n}\n");
            Assert.Contains(real.Diagnostics, d => d.Message.Contains("Hash.Of has no hash for"));
            CompileResult missing = Compile("phi.T:Program\n{\n\tlog (Hash.Of: 5);\n}\n");
            Assert.Contains(missing.Diagnostics, d => d.Message.Contains("add use data.hash;"));
        }

        [Fact]
        public void ACompiledLibrarysInterfaceHasItsTemplatesWholeAndTheStructsItHadMade()
        {
            var file = new SourceFile(Path.GetFullPath("lib.phi"),
                Stack + "phi.Lib:Library\n{\n\t[One]\n\t\tnew Stack<int> s;\n\t[end: (s.Push: 1)]\n}\n");
            CompileResult result = PhiCompiler.CompileLibrary(file, Libraries.LibraryPlatform.All.First(p => p.Host == Host.Linux64), "lib");
            Assert.True(result.Success, Errors(result));
            string text = result.Units.Single().Library!.Target.Interface.Last().Text;
            // the template with its methods' code, for a program to make its own structs from
            Assert.Contains("struct.Stack<Item>", text);
            Assert.Contains("items++ item;", text);
            // and what the library's own code calls, which a program must make too
            Assert.Contains("ptr<Stack<int>> made0;", text);
            // the library holds none of a made struct's code: it's the program's
            Assert.DoesNotContain("M_Stack_of_int_Push:", result.Units.Single().Text);
            Assert.Contains("M_Stack_of_int_Push", result.Units.Single().Text);
        }

        [Fact]
        public void OneThatUsesItselfWithABiggerTypeEachTimeIsStopped()
        {
            CompileResult result = Compile("struct.Deep<Item>\n{\n\tItem one;\n\tnew Deep<ptr<Item>> more;\n}\n\nphi.T:Program\n{\n\tnew Deep<int> d;\n}\n");
            Assert.Contains(result.Diagnostics, d => d.Message.Contains("does it use itself with a bigger type each time?"));
        }
    }
}
