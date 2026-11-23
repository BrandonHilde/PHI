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
    /// without a word. So libraries carry a version (CompiledLibrary.AbiVersion: 1.0, 1.1, ...), and this test
    /// fails when the layouts of a struct with one of everything aren't the ones saved here
    /// under that version.
    ///
    /// When it fails, something has moved. If that was meant, raise CompiledLibrary.AbiVersion
    /// (1.0 to 1.1: every .philib is then built again, and says so) and save the new layouts below
    /// with the new version. Don't save them under the same one: that's the silence this is here for.
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

        const string Saved = @"abi 1.3
Linux Spot: 8 bytes, aligned to 4
     0 x: int
     4 y: i16
     6 z: u8
Linux Leaf: 48 bytes, aligned to 4
     0 id: int
     4 name: str
    12 tag: str
    36 parts: int array
Linux Everything: 248 bytes, aligned to 8
     0 small: u8
     4 whole: int
     8 wide: i64
    16 real: f64
    24 flag: bln
    28 pointer: ptr<u8>
    32 fixed: str
    44 grows: str
    52 inside: Spot
    60 managed: Leaf
   108 one: Leaf
   112 pair: Leaf array
   120 few: Leaf array (at most 3)
   136 many: Leaf array
   148 spots: Spot array
   160 four: Spot array
   192 numbers: int array
   204 some: int array (at most 5)
   228 texts: str array
   240 last: u8
Linux64 Spot: 8 bytes, aligned to 4
     0 x: int
     4 y: i16
     6 z: u8
Linux64 Leaf: 64 bytes, aligned to 8
     0 id: int
     8 name: str
    24 tag: str
    48 parts: int array
Linux64 Everything: 328 bytes, aligned to 8
     0 small: u8
     4 whole: int
     8 wide: i64
    16 real: f64
    24 flag: bln
    32 pointer: ptr<u8>
    40 fixed: str
    56 grows: str
    72 inside: Spot
    80 managed: Leaf
   144 one: Leaf
   152 pair: Leaf array
   168 few: Leaf array (at most 3)
   200 many: Leaf array
   216 spots: Spot array
   232 four: Spot array
   264 numbers: int array
   280 some: int array (at most 5)
   304 texts: str array
   320 last: u8
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
                "If that was meant: raise CompiledLibrary.AbiVersion, and save these layouts in LibraryLayoutTests.Saved with the new version.\n\n" + now);
        }

        [Fact]
        public void ALibraryBuiltForAnotherVersionIsRefused()
        {
            string path = Path.Combine(Path.GetTempPath(), $"phi_abi_{Guid.NewGuid():N}.philib");
            try
            {
                var library = new CompiledLibrary { Name = "lib" };
                library.Write(path);
                Assert.Equal("lib", CompiledLibrary.Load(path).Name);

                // the same file, as a compiler with the next version would have written it (1.0: 1.1)
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
