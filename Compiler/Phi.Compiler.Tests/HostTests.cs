namespace Phi.Compiler.Tests
{
    /// <summary>Programs built for another system (H1, H3): Linux and Windows executables.</summary>
    public class HostTests
    {
        static CompileResult Compile(string text, Host host) => PhiCompiler.Compile(new SourceFile("test.phi", text), host: host);

        static string Program(string body) => "phi.Test:Program\n{\n" + body + "\n}\n";

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
