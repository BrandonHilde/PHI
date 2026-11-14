namespace Phi.Compiler.Tests
{
    /// <summary>Programs built for another system (H1, H3): Linux and Windows executables.</summary>
    public class HostTests
    {
        static CompileResult Compile(string text, Host host) => PhiCompiler.Compile(new SourceFile("test.phi", text), host: host);

        static string Program(string body) => "phi.Test:Program\n{\n" + body + "\n}\n";

        // a method's variables: as much as the stack it runs on can take, reached a page at a time
        // so the page under the stack is always what stops one that goes too far
        [Theory]
        [InlineData(Host.Linux, 100000, null)]
        [InlineData(Host.Linux64, 100000, null)]
        [InlineData(Host.Windows, 1000000, null)]
        [InlineData(Host.PhiOS, 30000, null)]
        [InlineData(Host.PhiOS, 40000, "Big needs 40000 bytes of stack for its variables (at most 32768 in a program on PHI OS, whose stack is 64 KB)")]
        [InlineData(Host.Linux, 2000000, "Big needs 2000000 bytes of stack for its variables (at most 1048576 in a program)")]
        public void AMethodsVariablesCanBeBigInAProgram(Host host, int bytes, string? error)
        {
            CompileResult result = Compile(Program($"call Big;\nexit 0;\n[Big]\n u8 buffer[{bytes}];\n buffer:0 is 1;\n[end]"), host);
            if (error != null)
            {
                Assert.Contains(result.Diagnostics, d => d.Message.Contains(error));
                return;
            }
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            string text = result.Units.Single().Text;
            Assert.Contains("sub " + (host.Is64() ? "rsp" : "esp") + ", 4096", text);
            Assert.DoesNotContain($"sub esp, {bytes}", text);
        }

        [Fact]
        public void AKernelsMethodKeepsToAPage()
        {
            CompileResult result = Compile("phi.Test:Kernel\n{\n[Big]\n u8 buffer[5000];\n[end]\n}\n", Host.PhiOS);
            Assert.Contains(result.Diagnostics, d => d.Message.Contains("Big needs 5000 bytes of stack for its variables (at most 4096 in a kernel)"));
        }

        static string Errors(CompileResult result) => string.Join("\n", result.Diagnostics.Select(d => d.Message));

        [Theory]
        [InlineData(Host.Linux, "org 0x8048000", "db 0x7F, 'ELF'")]
        [InlineData(Host.Windows, "org 0x400000", "db 'PE', 0, 0")]
        public void AProgramGetsTheSystemsHeaders(Host host, string org, string header)
        {
            CompileResult result = Compile(Program("log 'hi';"), host);
            Assert.True(result.Success, Errors(result));
            string text = result.Units.Single().Text;
            Assert.Contains(org, text);
            Assert.Contains(header, text);
            Assert.Contains("phi_image_end:", text);
        }

        [Theory]
        [InlineData("ptr<u8> p: new u8[8];\n int n: p;", Severity.Error, "'n' is an int, which keeps only 32 bits of this pointer")]
        [InlineData("int n: 5;\n ptr<u8> p: n;", Severity.Warning, "'p' is a ptr<u8>, 64 bits in 64-bit code, and this is an int, so its top half is lost")]
        [InlineData("ptr<u8> p: new u8[8];\n u32 n: p + 4;", Severity.Error, "keeps only 32 bits of this pointer")]
        [InlineData("ptr<u8> p: (Make);\n [Make]\n ptr<u8> q: new u8[8];\n return q;\n [end: 0]", Severity.Error, "the returned value is an int, which keeps only 32 bits of this pointer")]
        public void In64BitCodeAPointerAndANumberDontMixQuietly(string body, Severity severity, string message)
        {
            Assert.Contains(Compile(Program(body), Host.Linux64).Diagnostics, d => d.Severity == severity && d.Message.Contains(message));
            // in 32-bit code they're the same size
            Assert.DoesNotContain(Compile(Program(body), Host.Linux).Diagnostics, d => d.Message.Contains("32 bits of this") || d.Message.Contains("top half"));
        }

        [Theory]
        [InlineData("ptr<u8> p: new u8[8];\n int n: p as int;")]            // as says it's meant
        [InlineData("ptr<u8> p: 0;\n p is 0;")]                             // a constant goes in whole
        [InlineData("ptr<u8> p: new u8[8];\n ptr<u8> q: p + 4;\n q is q - 2;\n p:1 is 3;")]   // math on a pointer keeps all of it
        [InlineData("ptr<u8> p: new u8[8];\n ptr<u8> q: p + 4;\n int apart: q - p;")]          // how far apart two are is a number
        public void In64BitCodeWhatsMeantOrWholeIsntWarnedAbout(string body)
        {
            CompileResult result = Compile(Program(body), Host.Linux64);
            Assert.True(result.Success, Errors(result));
            Assert.DoesNotContain(result.Diagnostics, d => d.Message.Contains("32 bits of this") || d.Message.Contains("top half"));
        }

        [Fact]
        public void EachSystemHasItsOwnRuntime()
        {
            string linux = Compile(Program("call OS.Sleep: 1;"), Host.Linux).Units.Single().Text;
            string windows = Compile(Program("call OS.Sleep: 1;"), Host.Windows).Units.Single().Text;
            // OS.Sleep is the system's PHI module's (lib/phi/host/time_*.phi), by its own calls
            Assert.Contains("call M_HostTime_Sleep", linux);
            Assert.Contains("; sys.NanoSleep, system call 162", linux);
            Assert.Contains("call M_HostTime_Sleep", windows);
            Assert.Contains("; Kernel32.Sleep, from kernel32.dll", windows);
            Assert.DoesNotContain("OS_Sleep:", linux + windows);
            Assert.Contains("phi_imports:", windows);
            Assert.DoesNotContain("int 0x80", windows);
        }

        [Theory]
        [InlineData(Host.Linux)]
        [InlineData(Host.Windows)]
        public void OnlyAProgramRunsOnAnotherSystem(Host host)
        {
            CompileResult result = Compile("phi.K:Kernel\n{\n\tlog 'hi';\n}\n", host);
            Assert.False(result.Success);
            Assert.Contains($"only a program runs on {host.Name()}", Errors(result));
        }

        [Fact]
        public void APhiOsCallWithNoEquivalentIsAnError()
        {
            CompileResult result = Compile(Program("call OS.PutCell: 0 0 65;"), Host.Linux);
            Assert.Contains("OS.PutCell isn't available in a Linux program; it works in a PHI OS program", Errors(result));
        }

        [Fact]
        public void AKernelOnlyCallSaysSo()
        {
            // not "it works in a PHI OS program": it doesn't
            CompileResult result = Compile(Program("call OS.SetupInteruptTimer: 100;"), Host.Linux);
            Assert.False(result.Success);
            Assert.DoesNotContain("works in a PHI OS program", Errors(result));
        }

        [Fact]
        public void AModuleThatCantWorkThereIsOneErrorAtItsUse()
        {
            CompileResult result = Compile("use drivers.ata;\n\n" + Program("log 'hi';"), Host.Linux);
            Diagnostic error = Assert.Single(result.Diagnostics, d => d.Severity == Severity.Error);
            Assert.Equal("test.phi", error.File!.Path);
            Assert.StartsWith("drivers.ata doesn't work in a Linux program (lib/phi/drivers/ata.phi:", error.Message);
        }

        [Fact]
        public void AModuleBroughtInByAnotherNamesBoth()
        {
            // on PHI OS too: a program can't have a kernel's network stack
            CompileResult result = Compile("use net.tcp;\n\n" + Program("log 'hi';"), Host.PhiOS);
            Diagnostic error = Assert.Single(result.Diagnostics, d => d.Severity == Severity.Error);
            Assert.Contains("in a PHI OS program", error.Message);
            Assert.StartsWith("net.tcp", error.Message);
        }

        [Fact]
        public void TextHasWorksInEveryProgram()
        {
            foreach (Host host in new[] { Host.PhiOS, Host.Linux, Host.Windows })
            {
                CompileResult result = Compile(Program("str t: 'abc';\n\tlog (t has 'b');"), host);
                Assert.True(result.Success, Errors(result));
                Assert.Contains("phi_str_has:", result.Units.Single().Text);
            }
        }
    }
}
