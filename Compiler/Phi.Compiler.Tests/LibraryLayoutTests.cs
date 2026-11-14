using Phi.Compiler;
using Phi.Compiler.Libraries;
using Phi.Compiler.Syntax;
using Xunit;

namespace Phi.Compiler.Tests
{
    /// <summary>
    /// A compiled library's machine code has where things are written into it: which byte of a
    /// struct a field is at, how big a str's slot is, where an array without a size keeps its
    /// count. A library built before one of those moved goes wrong in a program built after,
    /// without a word. So libraries carry a number (CompiledLibrary.AbiVersion), and this test
    /// fails when the layouts of a struct with one of everything aren't the ones saved here
    /// under that number.
    ///
    /// When it fails, something has moved. If that was meant, raise CompiledLibrary.AbiVersion
    /// (every .philib is then built again, and says so) and save the new layouts below with the
    /// new number. Don't save them under the same number: that's the silence this is here for.
    ///
    /// It sees layouts only. The other things the number stands for (how code is called, what
    /// a runtime routine keeps) are listed where the number is.
    /// </summary>
    public class LibraryLayoutTests
    {
        const string Source = @"struct.Spot
{
	int x;
	i16 y;
	u8 z;
}

struct.Leaf
{
	int id;
	new str name;
	str tag: [20];
	int parts[];
}

struct.Everything
{
	u8 small;
	int whole;
	i64 wide;
	f64 real;
	bln flag;
	ptr<u8> pointer;
	str fixed: [10];
	new str grows;
	Spot inside;
	Leaf managed;
	new Leaf one;
	new Leaf pair[2];
	new Leaf few[atmost 3];
	new Leaf many[];
	Spot spots[];
	Spot four[4];
	int numbers[];
	int some[atmost 5];
	str texts[];
	u8 last;
}

phi.Lib:Library
{
	[Size: Everything e;]
	[end: e.whole]
}
";

        const string Saved = @"abi 1
Linux Spot: 7 bytes, aligned to 1
     0 x: int
     4 y: i16
     6 z: u8
Linux Leaf: 45 bytes, aligned to 1
     0 id: int
     4 name: str
    12 tag: str
    33 parts: int array
Linux Everything: 226 bytes, aligned to 1
     0 small: u8
     1 whole: int
     5 wide: i64
    13 real: f64
    21 flag: bln
    22 pointer: ptr<u8>
    26 fixed: str
    37 grows: str
    45 inside: Spot
    52 managed: Leaf
    97 one: Leaf
   101 pair: Leaf array
   109 few: Leaf array (at most 3)
   125 many: Leaf array
   137 spots: Spot array
   149 four: Spot array
   177 numbers: int array
   189 some: int array (at most 5)
   213 texts: str array
   225 last: u8
Linux64 Spot: 7 bytes, aligned to 1
     0 x: int
     4 y: i16
     6 z: u8
Linux64 Leaf: 57 bytes, aligned to 1
     0 id: int
     4 name: str
    20 tag: str
    41 parts: int array
Linux64 Everything: 290 bytes, aligned to 1
     0 small: u8
     1 whole: int
     5 wide: i64
    13 real: f64
    21 flag: bln
    22 pointer: ptr<u8>
    30 fixed: str
    41 grows: str
    57 inside: Spot
    64 managed: Leaf
   121 one: Leaf
   129 pair: Leaf array
   145 few: Leaf array (at most 3)
   173 many: Leaf array
   189 spots: Spot array
   205 four: Spot array
   233 numbers: int array
   249 some: int array (at most 5)
   273 texts: str array
   289 last: u8
";

        static string Layouts()
        {
            var text = new System.Text.StringBuilder($"abi {CompiledLibrary.AbiVersion}\n");
            foreach (Host host in new[] { Host.Linux, Host.Linux64 })
            {
                var file = new SourceFile(Path.GetFullPath("lib.phi"), Source);
                CompileResult result = PhiCompiler.CompileLibrary(file, LibraryPlatform.All.First(p => p.Host == host), "lib");
                Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                foreach (StructDecl s in result.Syntax!.Structs.Where(s => s.Symbol != null && s.File == file))
                {
                    text.Append($"{host} {s.Name}: {s.Symbol!.Size} bytes, aligned to {s.Symbol.Alignment}\n");
                    foreach (var field in s.Symbol.Fields)
                        text.Append($"  {field.Offset,4} {field.Name}: {field.Type}\n");
                }
            }
            return text.ToString();
        }

        [Fact]
        public void TheLayoutsALibrarysCodeIsWrittenForAreTheOnesItsNumberNames()
        {
            string now = Layouts().Replace("\r\n", "\n").Trim();
            string saved = Saved.Replace("\r\n", "\n").Trim();
            Assert.True(now == saved,
                "The layout of a struct, a str or an array has changed, so a library built before this would go wrong in a program built now.\n" +
                "If that was meant: raise CompiledLibrary.AbiVersion, and save these layouts in LibraryLayoutTests.Saved with the new number.\n\n" + now);
        }

        [Fact]
        public void ALibraryBuiltForAnotherNumberIsRefused()
        {
            string path = Path.Combine(Path.GetTempPath(), $"phi_abi_{Guid.NewGuid():N}.philib");
            try
            {
                var library = new CompiledLibrary { Name = "lib" };
                library.Write(path);
                Assert.Equal("lib", CompiledLibrary.Load(path).Name);

                // the same file, as a compiler with the next number would have written it
                byte[] bytes = File.ReadAllBytes(path);
                string header = $"abi {CompiledLibrary.AbiVersion}";
                int at = bytes.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(header));
                Assert.True(at > 0);
                bytes[at + header.Length - 1]++;
                File.WriteAllBytes(path, bytes);
                var refused = Assert.Throws<LibraryFileException>(() => CompiledLibrary.Load(path));
                Assert.Contains("build it again", refused.Message);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void AModulesMarkLeavesOutCommentsAndSpacingAndNothingElse()
        {
            string Mark(string text) => PhiCompiler.ModuleMark(new SourceFile("lib/phi/user/sample.phi", text));
            string module = "phi.Sample:Library\n{\n\tint count: 3;\n\t[Next]\n\t\tcount++;\n\t[end: count]\n}\n";
            Assert.Equal(Mark(module), Mark(module.Replace("\tint count: 3;", "\t# how many\n\tint  count:   3;      # three")));
            Assert.NotEqual(Mark(module), Mark(module.Replace("count: 3", "count: 4")));
            Assert.NotEqual(Mark(module), Mark(module.Replace("int count", "u8 count")));
            Assert.NotEqual(Mark(module), Mark(module.Replace("count++;", "count++ 2;")));
        }
    }
}
