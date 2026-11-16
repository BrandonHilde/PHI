namespace Phi.Compiler.Tests
{
    /// <summary>
    /// vec&lt;T&gt; (Plan.md, "vec&lt;T&gt;: the design"): what it says when it's used where it
    /// can't be, and that what can be builds. What it works out is tests/vec_lanes_program
    /// (every operation on every kind of lane) and tests/vec_program.
    /// </summary>
    public class VecTests
    {
        static CompileResult Compile(string text, Host host = Host.Linux64) => PhiCompiler.Compile(new SourceFile("test.phi", text), host: host);

        static string InMethod(string body) => "phi.Test:Program\n{\n\tcall Run;\n\texit 0;\n\n\t[Run:]\n" + body + "\n\t[end]\n}\n";

        static string Errors(CompileResult result) => string.Join("\n", result.Diagnostics.Select(d => d.Message));

        [Theory]
        // the lanes
        [InlineData("vec<bln> v;", "a vec's lanes are whole numbers (u8 to u64, int) or f32 or f64, and this is a bln")]
        [InlineData("vec<f32> v: 1.0 2.0;", "'v' is a vec<f32>, which has 4 lanes here, and 2 values were given; one value goes in every lane")]
        [InlineData("vec<u8> v: 300;", "300 doesn't fit a u8")]
        [InlineData("vec<f32> v: 1.0;\n\t\tint x: v;", "'x' is an int, and this is a vec<f32>; for one lane, write v:0, or one of its methods that gives a number, like (v.Sum)")]
        [InlineData("vec<f32> v;\n\t\tint x: v:4;", "index 4 is outside 'v', which has 4 lanes here")]
        // what it does, and doesn't
        [InlineData("vec<int> a: 1;\n\t\tvec<int> b: a / 2;", "x86's vector instructions don't divide whole numbers, so a vec<int> has no /")]
        [InlineData("vec<int> a: 1;\n\t\tbln b: a < a;", "vectors are compared lane by lane with a method, which gives a mask: (a.Less: b)")]
        [InlineData("vec<f32> a: 1.0;\n\t\tvec<f32> b: a << 1;", "<< and >> shift whole numbers' bits, and a vec<f32>'s lanes are floats")]
        [InlineData("vec<int> a: 1;\n\t\tvec<int> b: a << a;", "every lane is shifted by the same count, one number (v << 2)")]
        [InlineData("vec<int> a: 1;\n\t\tvec<int> b: a % 2;", "a vec has no %")]
        [InlineData("vec<int> a: 1;\n\t\tbln b: not a;", "not is for a bln, and this is a vec<int>")]
        [InlineData("vec<int> a: 1;\n\t\tvec<f32> b: 1.0;\n\t\tvec<int> c: a + b;", "the right side is a vec<int>, and this is a vec<f32>; write (b as vec<int>)")]
        // its methods
        [InlineData("vec<u32> a: 1;\n\t\tvec<u32> b: (a.Abs);", "a vec<u32>'s lanes are never negative, so Abs would give them back as they are")]
        [InlineData("vec<int> a: 1;\n\t\tvec<int> b: (a.Sqrt);", "Sqrt is for f32 and f64 lanes, and these are ints")]
        [InlineData("vec<int> a: 1;\n\t\tvec<int> b: (a.AddSat: a);", "AddSat stops at the ends of 8- and 16-bit lanes (as pixels need), and these are ints")]
        [InlineData("vec<i16> a: 1;\n\t\tvec<i16> b: (a.Average: a);", "Average is for u8 and u16 lanes")]
        [InlineData("vec<i64> a: 1;\n\t\tvec<i64> b: (a.Widen);", "i64 lanes are as wide as lanes go")]
        [InlineData("vec<u8> a: 1;\n\t\tvec<u8> b: (a.Narrow: a);", "u8 lanes are as narrow as lanes go")]
        [InlineData("vec<int> a: 1;\n\t\tvec<int> b: (a.Min);", "Min is given 1 value, and this gives it 0")]
        [InlineData("vec<int> a: 1;\n\t\tvec<int> b: (a.Biggest);", "has no method Biggest")]
        [InlineData("vec<int> a: 1;\n\t\tvec<u8> m: 1;\n\t\tvec<int> b: (m.Pick: a a);", "a mask picks lane by lane: a vec<u8> has 16 lanes, and a vec<int> 4")]
        // as
        [InlineData("vec<f32> a: 1.0;\n\t\tvec<f64> b: (a as vec<f64>);", "(v.Widen) and (v.WidenHigh) make f64s of an f32 vec's low and high halves")]
        [InlineData("vec<f32> a: 1.0;\n\t\tvec<u8> b: (a as vec<u8>);", "as between floats and whole numbers keeps each lane's value, so the lanes stay as many")]
        public void SaysWhatsWrong(string body, string error)
        {
            CompileResult result = Compile(InMethod(body));
            Assert.Contains(result.Diagnostics, d => d.Message.Contains(error));
        }

        [Fact]
        public void IsForSixtyFourBitPrograms()
        {
            string program = InMethod("vec<f32> v: 1.0;");
            foreach (Host host in new[] { Host.Linux, Host.Windows, Host.PhiOS })
                Assert.Contains(Compile(program, host).Diagnostics, d => d.Message.Contains("vec is in 64-bit programs for now (--target linux64 or windows64)"));
            Assert.True(Compile(program, Host.Linux64).Success, Errors(Compile(program, Host.Linux64)));
            Assert.True(Compile(program, Host.Windows64).Success, Errors(Compile(program, Host.Windows64)));
        }

        [Fact]
        public void IsUsedInMethods()
        {
            // a class's own statements are the generator's, which has no vectors
            string program = "phi.Test:Program\n{\n\tvec<f32> kept;\n\tkept is 1.0;\n\texit 0;\n}\n";
            Assert.Contains(Compile(program).Diagnostics, d => d.Message.Contains("vec works in methods for now, not in a class's own statements"));
            string given = "phi.Test:Program\n{\n\tvec<f32> kept: 1.0;\n\texit 0;\n}\n";
            Assert.Contains(Compile(given).Diagnostics, d => d.Message.Contains("a class's vec starts as zeros; give 'kept' its lanes in a method"));
        }

        [Fact]
        public void SaysWhatAMethodUsingOneCantHave()
        {
            // catching is still the generator's alone: said, naming what to move
            string program = "phi.Test:Program\n{\n\tcall Run;\n\texit 0;\n\n\t[Run:]\n\t\tvec<int> v: 1;\n\t\tif call Fails throws err\n\t\t\tlog err.message;\n\t\t;;\n\t[end]\n\n\t[Fails:]\n\t[end]\n}\n";
            Assert.Contains(Compile(program).Diagnostics, d => d.Message.Contains("Run uses a vec, and what writes vector code doesn't take") && d.Message.Contains("put that part in a method of its own"));
        }

        [Fact]
        public void IsNotAMethodValue()
        {
            string program = "phi.Test:Program\n{\n\tmethod<vec<int>: int> f: addr First;\n\texit 0;\n\n\t[First: vec<int> v: 0;]\n\t[end: v:0]\n}\n";
            Assert.Contains(Compile(program).Diagnostics, d => d.Message.Contains("Test.First takes or gives a vec, so it's called by name, not through a method value (yet)"));
        }

        [Fact]
        public void BuildsWhereItCan()
        {
            string program = InMethod(
                "\t\tvec<f32> a: 1.0 2.0 3.0 4.0;\n" +
                "\t\tvec<f32> b: a * 2.0 + 1.0;\n" +
                "\t\tvec<f32> m: (a.Less: b);\n" +
                "\t\tb is (m.Pick: a b);\n" +
                "\t\tb:0 is 5.0;\n" +
                "\t\tb is 1.0 2.0 3.0 4.0;\n" +
                "\t\tvec<int> i: (b as vec<int>);\n" +
                "\t\tvec<u8> bytes: (i as vec<u8>);\n" +
                "\t\tvec<u16> wide: (bytes.Widen);\n" +
                "\t\tlog b ' ' i ' ' (bytes.Sum) ' ' wide.len '\\n';");
            CompileResult result = Compile(program);
            Assert.True(result.Success, Errors(result));
        }
    }
}
