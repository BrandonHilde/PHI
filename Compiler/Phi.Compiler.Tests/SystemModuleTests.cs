namespace Phi.Compiler.Tests
{
    /// <summary>
    /// A system's own modules (phi.Name:Linux, :Windows ...) and the system calls declared in
    /// them (sys.Name: number ...;), H6 step 1: a module for another system is left out, and a
    /// system call goes only where its numbers belong.
    /// </summary>
    public class SystemModuleTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "phi-systems-" + Guid.NewGuid().ToString("N"));

        public SystemModuleTests() => Directory.CreateDirectory(root);
        public void Dispose() => Directory.Delete(root, recursive: true);

        /// <summary>Writes the files (name, text) next to each other and builds the first for host.</summary>
        CompileResult Build(Host host, params (string Name, string Text)[] files)
        {
            foreach (var (name, text) in files) File.WriteAllText(Path.Combine(root, name), text);
            return PhiCompiler.Compile(SourceFile.Load(Path.Combine(root, files[0].Name)), host: host);
        }

        static string Errors(CompileResult result) => string.Join("\n", result.Diagnostics.Select(d => d.Message));

        const string Main = "use raw_linux;\nuse raw_windows;\nphi.Test:Program\n{\n log (Raw.Which) '\\n';\n}\n";
        const string LinuxRaw = "phi.Raw:Linux\n{\n [Which]\n [end: 1]\n}\n";
        const string WindowsRaw = "phi.Raw:Windows\n{\n [Which]\n [end: 2]\n}\n";

        [Theory]
        [InlineData(Host.Linux, "raw_linux.phi")]
        [InlineData(Host.Linux64, "raw_linux.phi")]
        [InlineData(Host.Windows, "raw_windows.phi")]
        [InlineData(Host.Windows64, "raw_windows.phi")]
        public void EachSystemBuildsItsOwnVersionOfAModule(Host host, string built)
        {
            CompileResult result = Build(host, ("main.phi", Main), ("raw_linux.phi", LinuxRaw), ("raw_windows.phi", WindowsRaw));
            Assert.True(result.Success, Errors(result));
            string text = result.Units.Single().Text;
            Assert.Contains($"; method Raw.Which", text);
            Assert.Contains(built, text);
            Assert.DoesNotContain(built == "raw_linux.phi" ? "raw_windows.phi" : "raw_linux.phi", text.Replace("use raw_", ""));
        }

        [Fact]
        public void ANameWithNoVersionForTheSystemSaysWhichItHas()
        {
            CompileResult result = Build(Host.Windows, ("main.phi", "use raw_linux;\nphi.Test:Program\n{\n log (Raw.Which) '\\n';\n}\n"),
                                         ("raw_linux.phi", LinuxRaw));
            Assert.False(result.Success);
            Assert.Contains("Raw has no version for Windows: there's only :Linux", Errors(result));
        }

        [Theory]
        [InlineData(Host.Linux64, false)]   // :Linux and :Linux64 are both 64-bit Linux's
        [InlineData(Host.Linux, true)]      // :Linux64 is left out of 32-bit Linux
        public void OneNameHasOneVersionForASystem(Host host, bool builds)
        {
            CompileResult result = Build(host, ("main.phi", "use raw_linux;\nuse raw_linux64;\nphi.Test:Program\n{\n log (Raw.Which) '\\n';\n}\n"),
                                         ("raw_linux.phi", LinuxRaw), ("raw_linux64.phi", "phi.Raw:Linux64\n{\n [Which]\n [end: 3]\n}\n"));
            Assert.Equal(builds, result.Success);
            if (!builds) Assert.Contains("Raw has a :Linux and a :Linux64 version, so which one 64-bit Linux builds is unclear", Errors(result));
        }

        [Fact]
        public void AFileForASystemHasOnlyThatSystemsClasses()
        {
            CompileResult result = Build(Host.Linux, ("main.phi", "use raw_linux;\nphi.Test:Program\n{\n log (Raw.Which) '\\n';\n}\n"),
                                         ("raw_linux.phi", LinuxRaw + "phi.Shared:Library\n{\n}\n"));
            Assert.Contains("this file is for :Linux (Raw), so each of its classes is too; Shared (:Library) goes in a file of its own", Errors(result));
        }

        const string Calls32 = "phi.Raw:Linux32\n{\n [Say]\n  int n: (sys.Write: 1 'hi' 2);\n [end]\n [Pid]\n [end: (sys.GetPid)]\n}\n" +
                               "sys.Write: 4  int fd  ptr<u8> data  u32 length;\nsys.GetPid: 20;\n";

        [Fact]
        public void ASystemCallsValuesGoInTheRegistersOfInt0x80()
        {
            CompileResult result = Build(Host.Linux, ("main.phi", "use raw;\nphi.Test:Program\n{\n call Raw.Say;\n log (Raw.Pid);\n}\n"), ("raw.phi", Calls32));
            Assert.True(result.Success, Errors(result));
            string text = result.Units.Single().Text;
            Assert.Contains("; sys.Write, system call 4", text);
            int at = text.IndexOf("; sys.Write, system call 4");
            string call = text[at..text.IndexOf("int 0x80", at)];
            // the values pushed in order, then taken off the last first: length, data, fd
            Assert.True(call.IndexOf("pop edx") < call.IndexOf("pop ecx") && call.IndexOf("pop ecx") < call.IndexOf("pop ebx"), call);
            Assert.Contains("mov eax, 4", call);
            Assert.Contains("mov eax, 20", text);
        }

        [Fact]
        public void A64BitSystemCallUsesSyscallAndItsRegisters()
        {
            string module = "phi.Raw:Linux64\n{\n [Open]\n [end: (sys.OpenAt: -100 'x' 0 0)]\n}\nsys.OpenAt: 257  int dir  ptr<u8> path  int flags  int mode;\n";
            CompileResult result = Build(Host.Linux64, ("main.phi", "use raw;\nphi.Test:Program\n{\n log (Raw.Open);\n}\n"), ("raw.phi", module));
            Assert.True(result.Success, Errors(result));
            string text = result.Units.Single().Text;
            int at = text.IndexOf("; sys.OpenAt, system call 257");
            Assert.True(at >= 0, text);
            string call = text[at..(text.IndexOf("syscall", at) + 7)];
            Assert.Contains("pop r10", call);   // the fourth value: r10, not rcx (syscall changes rcx)
            Assert.Contains("pop rdi", call);
            Assert.Contains("mov eax, 257", call);
            Assert.DoesNotContain("int 0x80", call);
        }

        [Fact]
        public void ASystemCallSaysWhatItGivesWhenItIsntAnInt()
        {
            string module = "phi.Raw:Linux64\n{\n [Page]\n  ptr<u8> page: (sys.Map: 0 4096 3 0x22 -1 0);\n [end: page]\n}\n" +
                            "sys.Map: 9  ptr<u8> address  u64 size  int protect  int flags  int fd  u64 offset: ptr<u8>;\n";
            CompileResult result = Build(Host.Linux64, ("main.phi", "use raw;\nphi.Test:Program\n{\n ptr<u8> p: (Raw.Page);\n p:0 is 7;\n}\n"), ("raw.phi", module));
            Assert.True(result.Success, Errors(result));
            // (whether all 64 bits come back is sys_calls_program's to show: its page is above 4 GB)
        }

        [Theory]
        // in the program's own class: its numbers belong to a system's module
        [InlineData("phi.Test:Program\n{\n}\nsys.GetPid: 20;\n", "", "sys.GetPid goes in a module for the system it calls")]
        // Windows has no system calls of its own
        [InlineData("use raw;\nphi.Test:Program\n{\n}\n", "phi.Raw:Windows\n{\n}\nsys.GetPid: 20;\n", "Windows has no system calls a program makes itself")]
        // used from outside the system's module
        [InlineData("use raw;\nphi.Test:Program\n{\n log (sys.GetPid);\n}\n", "phi.Raw:Linux\n{\n}\nsys.GetPid: 20;\n", "used only in a module for a system that has them")]
        // too few values
        [InlineData("use raw;\nphi.Test:Program\n{\n}\n", "phi.Raw:Linux\n{\n [Say]\n  int n: (sys.Write: 1);\n [end]\n}\nsys.Write: 4  int fd  ptr<u8> data  u32 length;\n",
                    "sys.Write takes 3 values (fd, data, length), but was given 1")]
        // one that isn't declared
        [InlineData("use raw;\nphi.Test:Program\n{\n}\n", "phi.Raw:Linux\n{\n [Say]\n  int n: (sys.Fork: 0);\n [end]\n}\n", "there's no system call sys.Fork for Linux")]
        public void ASystemCallGoesWhereItsNumbersBelong(string main, string module, string error)
        {
            var files = new List<(string, string)> { ("main.phi", main) };
            if (module.Length > 0) files.Add(("raw.phi", module));
            CompileResult result = Build(main.Contains("Windows") || module.Contains(":Windows") ? Host.Windows : Host.Linux, files.ToArray());
            Assert.False(result.Success);
            Assert.Contains(error, Errors(result));
        }

        const string User32 = "use 'user32.dll' as User32;\nphi.Win:Windows\n{\n [Beep]\n [end: (User32.MessageBeep: 0)]\n" +
                              " [Upper: ptr<u8> text: 0;]\n [end: (User32.CharUpperA: text)]\n}\n" +
                              "User32.MessageBeep: u32 kind;\nUser32.CharUpperA: ptr<u8> text: ptr<u8>;\n" +
                              "User32.MessageBoxA: ptr<u8> window  ptr<u8> text  ptr<u8> title  u32 kind;\n";

        [Theory]
        [InlineData(Host.Windows, "call [phi_outside_{0}_MessageBeep]")]
        [InlineData(Host.Windows64, "call [rel phi_outside_{0}_MessageBeep]")]
        public void AnOutsideFunctionIsInTheImportTableAndCalledThroughIt(Host host, string call)
        {
            CompileResult result = Build(host, ("main.phi", "use win;\nphi.Test:Program\n{\n log (Win.Beep);\n}\n"), ("win.phi", User32));
            Assert.True(result.Success, Errors(result));
            string text = result.Units.Single().Text;
            // (the runtime's own modules call kernel32.dll and winmm.dll too, so user32.dll's place in the list varies)
            var named = System.Text.RegularExpressions.Regex.Match(text, @"phi_outside_(\d+)_name: db 'user32.dll', 0");
            Assert.True(named.Success, text);
            Assert.Contains(string.Format(call, named.Groups[1].Value), text);
            Assert.Contains("PHI_OUTSIDE_DESCRIPTORS", text);
            Assert.Contains("db 'MessageBeep', 0", text);
            Assert.Contains("db 'CharUpperA', 0", text);
            Assert.DoesNotContain("'MessageBoxA'", text);  // declared but never called: not in the table
        }

        [Fact]
        public void A64BitOutsideCallKeepsTheStackAlignedWithRoomForTheFunction()
        {
            string module = "use 'kernel32.dll' as Kernel32;\nphi.Win:Windows\n{\n [Wide]\n [end: (Kernel32.MultiByteToWideChar: 65001 0 'hi' -1 0 0)]\n}\n" +
                            "Kernel32.MultiByteToWideChar: u32 page  u32 flags  ptr<u8> text  int length  ptr<u16> wide  int room;\n";
            CompileResult result = Build(Host.Windows64, ("main.phi", "use win;\nphi.Test:Program\n{\n log (Win.Wide);\n}\n"), ("win.phi", module));
            Assert.True(result.Success, Errors(result));
            string text = result.Units.Single().Text;
            int at = text.IndexOf("; Kernel32.MultiByteToWideChar, from kernel32.dll");
            string call = text[at..text.IndexOf("pop r12", at)];
            Assert.Contains("sub rsp, 48", call);          // 32 bytes of shadow space and two values on the stack
            Assert.Contains("and rsp, -16", call);
            Assert.Contains("mov [rsp + 40], rax", call);  // the sixth value
            Assert.Contains("mov r9, [r12 + 16]", call);   // the fourth
        }

        [Theory]
        // in a module for another system, or none
        [InlineData("use 'user32.dll' as User32;\nphi.Raw:Linux\n{\n}\n", Host.Linux, "'user32.dll' is an outside library, used only in a :Windows module for now")]
        [InlineData("phi.Win:Windows\n{\n [Beep]\n [end: (User32.MessageBeep: 0)]\n}\n", Host.Windows, "unknown method 'User32.MessageBeep'")]
        // a name that reads as a class's
        [InlineData("use 'user32.dll' as user32;\nphi.Win:Windows\n{\n}\n", Host.Windows, "a library's name reads as a class's does, so it starts with a capital: as User32")]
        // what it takes is checked, and only what goes in a register
        [InlineData("use 'user32.dll' as User32;\nphi.Win:Windows\n{\n [Beep]\n [end: (User32.MessageBeep)]\n}\nUser32.MessageBeep: u32 kind;\n", Host.Windows,
                    "User32.MessageBeep takes 1 value (kind), but was given 0")]
        [InlineData("use 'msvcrt.dll' as C;\nphi.Win:Windows\n{\n}\nC.sqrt: f64 x: f64;\n", Host.Windows, "C.sqrt's 'x' is a f64, and floats to and from outside functions aren't there yet")]
        public void AnOutsideFunctionGoesWhereItsLibraryIs(string module, Host host, string error)
        {
            CompileResult result = Build(host, ("main.phi", "use win;\nphi.Test:Program\n{\n}\n"), ("win.phi", module));
            Assert.False(result.Success);
            Assert.Contains(error, Errors(result));
        }

        [Fact]
        public void AnOutsideFunctionIsCalledOnlyFromItsModule()
        {
            CompileResult result = Build(Host.Windows, ("main.phi", "use win;\nphi.Test:Program\n{\n log (User32.MessageBeep: 0);\n}\n"), ("win.phi", User32));
            Assert.Contains("User32.MessageBeep is a function of 'user32.dll', used only from a :Windows module (for now), not from Test (:Program)", Errors(result));
        }

        [Fact]
        public void TwoModulesMayDeclareTheSameCallTheSameWayButNotDifferently()
        {
            // lib/phi/host/linux32.phi declares sys.Close: 6 int fd; and so may a program's own module
            string same = "phi.Raw:Linux32\n{\n [Shut]\n [end: (sys.Close: 3)]\n}\nsys.Close: 6  int handle;\n";
            CompileResult result = Build(Host.Linux, ("main.phi", "use raw;\nphi.Test:Program\n{\n call OS.Sleep: 1;\n log (Raw.Shut);\n}\n"), ("raw.phi", same));
            Assert.True(result.Success, Errors(result));

            string other = "phi.Raw:Linux32\n{\n [Shut]\n [end: (sys.Close: 3)]\n}\nsys.Close: 7  int handle;\n";
            result = Build(Host.Linux, ("main.phi", "use raw;\nphi.Test:Program\n{\n call OS.Sleep: 1;\n log (Raw.Shut);\n}\n"), ("raw.phi", other));
            Assert.Contains("sys.Close is declared differently in lib/phi/host/linux32.phi (number 6: int)", Errors(result));

            string function = "use 'kernel32.dll' as Kernel32;\nphi.Win:Windows\n{\n [Shut]\n [end: (Kernel32.CloseHandle: 0)]\n}\nKernel32.CloseHandle: u32 handle;\n";
            result = Build(Host.Windows, ("main.phi", "use win;\nphi.Test:Program\n{\n log (OS.FileSize: 'x');\n log (Win.Shut);\n}\n"), ("win.phi", function));
            Assert.Contains("Kernel32.CloseHandle is declared differently in lib/phi/host/files_windows.phi (ptr<u8>)", Errors(result));
        }

        [Theory]
        [InlineData("log 'hi';", new string[0], new[] { "M_HostTime", "M_HostFiles", "M_HostNet" })]
        [InlineData("call OS.Sleep: 1;", new[] { "M_HostTime_Sleep" }, new[] { "M_HostFiles", "M_HostNet" })]
        [InlineData("log (OS.FileSize: 'x');", new[] { "M_HostFiles_FileSize" }, new[] { "M_HostTime", "M_HostNet" })]
        [InlineData("log (OS.NetAddress);", new[] { "M_HostNet_NetAddress" }, new[] { "M_HostTime", "M_HostFiles" })]
        public void AProgramGetsOnlyThePartsOfItsSystemItUses(string body, string[] has, string[] hasNot)
        {
            foreach (Host host in new[] { Host.Linux, Host.Linux64, Host.Windows, Host.Windows64 })
            {
                CompileResult result = Build(host, ("main.phi", "phi.Test:Program\n{\n " + body + "\n}\n"));
                Assert.True(result.Success, Errors(result));
                string text = result.Units.Single().Text;
                foreach (string s in has) Assert.Contains(s, text);
                foreach (string s in hasNot) Assert.DoesNotContain(s, text);
                if (body.Contains("Net") == false) Assert.DoesNotContain("ws2_32", text);
            }
        }

        [Fact]
        public void APhiOsProgramHasSystemCallsToo()
        {
            string module = "phi.Raw:PhiOS\n{\n [Ticks]\n [end: (sys.Ticks)]\n}\nsys.Ticks: 3;\n";
            CompileResult result = Build(Host.PhiOS, ("main.phi", "use raw;\nphi.Test:Program\n{\n log (Raw.Ticks);\n}\n"), ("raw.phi", module));
            Assert.True(result.Success, Errors(result));
            Assert.Contains("int 0x80", result.Units.Single().Text);
        }
    }
}
