using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Phi.Cli
{
    /// <summary>One thread (or CPU) as it was when a run was stopped.</summary>
    /// <param name="Name">how the report names it: "cpu 0", "thread 2"</param>
    /// <param name="Ip">the address of its next instruction</param>
    /// <param name="Stack">the words from the top of its stack up, as many as could be read</param>
    /// <param name="Note">what else is known about it: "waiting for an interrupt (hlt)", "in system call 240"</param>
    /// <param name="User">a PHI OS program's code (ring 3), not the kernel's</param>
    /// <param name="Always">shown even with no PHI code anywhere on it (a CPU; not a thread the system made)</param>
    public sealed record StoppedThread(string Name, ulong Ip, IReadOnlyList<ulong> Stack, string? Note = null,
                                       bool User = false, bool Always = false);

    /// <summary>A unit's address map, and the name a report gives it (a PHI OS program's, say).</summary>
    public sealed record NamedMap(string Name, AddressMap Map, bool User = false);

    /// <summary>
    /// Where a run that timed out was: each thread's next instruction and the calls that led
    /// there, as PHI lines (or library routines). The calls come from the thread's stack: each
    /// value on it that is where one of the program's calls returns to (see AddressMap.IsReturn).
    /// That finds them through assembly and system code, which keep no chain of frames, though
    /// once in a while a value a call left behind shows up too.
    /// </summary>
    public static class Where
    {
        const int MostCalls = 8;

        /// <summary>The report, or "" when there's nothing to say.</summary>
        public static string Report(IReadOnlyList<StoppedThread> threads, IReadOnlyList<NamedMap> maps)
        {
            if (threads.Count == 0 || maps.Count == 0) return "";
            // each thread's lines after its name; threads with the same ones are shown together
            var shown = new List<(List<string> Names, string Lines)>();
            int foreign = 0;
            foreach (StoppedThread t in threads)
            {
                // every PHI OS program's code starts at the same address: the one whose code fits best
                var candidates = maps.Where(m => m.User == t.User).OrderByDescending(m => Score(t, m.Map)).ToList();
                NamedMap? map = candidates.FirstOrDefault();
                if (map == null || Score(t, map.Map) == 0)
                {
                    if (!t.Always) { foreign++; continue; }
                    map = null;
                }

                var lines = new StringBuilder();
                if (map != null && map.Name.Length > 0) lines.Append($"(in {map.Name}) ");
                CodePlace? here = map?.Map.Find(t.Ip);
                lines.Append(here != null ? Describe(here)
                             : t.Always ? $"outside the program's code (0x{t.Ip:X})" : $"in the system's code (0x{t.Ip:X})");
                if (t.Note != null) lines.Append($", {t.Note}");
                lines.Append('\n');

                CodePlace? last = null;
                int calls = 0;
                foreach (ulong value in map != null ? t.Stack.Where(map.Map.IsReturn) : Enumerable.Empty<ulong>())
                {
                    CodePlace? from = map!.Map.Find(value - 1);
                    if (from == null || from == last) continue;
                    if (calls == MostCalls) { lines.Append("      ...\n"); break; }
                    lines.Append($"      called from {Describe(from)}\n");
                    last = from;
                    calls++;
                }

                string said = lines.ToString();
                int same = shown.FindIndex(s => s.Lines == said);
                if (same >= 0) shown[same].Names.Add(t.Name);
                else shown.Add((new List<string> { t.Name }, said));
            }

            var text = new StringBuilder();
            foreach (var (names, said) in shown)
                text.Append($"  {string.Join(", ", names)}: {said}");
            if (foreign > 0)
                text.Append($"  ({foreign} other thread{(foreign == 1 ? "" : "s")}, with no PHI code on {(foreign == 1 ? "its" : "their")} stack: the system's own)\n");
            return text.Length == 0 ? "" : "where it was when it was stopped:\n" + text.ToString().TrimEnd('\n');
        }

        static int Score(StoppedThread t, AddressMap map) => (map.Find(t.Ip) != null ? 2 : 0) + t.Stack.Count(map.IsReturn);

        static string Describe(CodePlace p) =>
            p.Line > 0 ? $"{p.Routine}, {Short(p.File)}:{p.Line}: {p.Text}"
            : p.File.Length > 0 ? $"{p.Routine} ({Short(p.File)})" : p.Routine;

        /// <summary>A file as the compiler was given it (tests/x.phi, lib/...), or just its name when that was a long way round.</summary>
        static string Short(string file) => file.StartsWith("../") || Path.IsPathRooted(file) ? Path.GetFileName(file) : file;

        /// <summary>
        /// The maps of what a test built: a kernel's (or a 16-bit program's) units, and the PHI OS
        /// programs on its disk (programs/NAME/program.lst), or an ordinary program's own.
        /// </summary>
        public static List<NamedMap> MapsIn(string outDir)
        {
            var maps = new[] { "kernel.lst", "os.lst", "boot.lst", "program.lst" }
                .Select(n => Path.Combine(outDir, n)).Where(File.Exists)
                .Select(f => new NamedMap("", AddressMap.Load(f))).ToList();
            string programs = Path.Combine(outDir, "programs");
            if (Directory.Exists(programs))
                foreach (string dir in Directory.GetDirectories(programs).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    string listing = Path.Combine(dir, "program.lst");
                    if (File.Exists(listing)) maps.Add(new NamedMap(Path.GetFileName(dir), AddressMap.Load(listing), User: true));
                }
            return maps;
        }

        // ---- QEMU: each CPU, through a monitor

        static readonly Regex CpuNumber = new(@"CPU #(\d+)");
        static readonly Regex Esp = new(@"ESP=([0-9a-fA-F]{8})");
        static readonly Regex Ss = new(@"SS =[0-9a-fA-F]{4} ([0-9a-fA-F]{8})");
        static readonly Regex Escape = new(@"\x1b\[[0-9;]*[A-Za-z]");
        static readonly Regex MemoryLine = new(@"^\s*[0-9a-fA-F]+:((?:\s+0x[0-9a-fA-F]+)+)\s*$", RegexOptions.Multiline);

        /// <summary>Stops the machine, then reads each CPU's registers and the top of its stack through the monitor at port.</summary>
        public static List<StoppedThread> FromQemu(int port)
        {
            var threads = new List<StoppedThread>();
            try
            {
                using var give = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using TcpClient client = InputScript.Connect(port, give.Token);
                client.ReceiveTimeout = 3000;
                NetworkStream stream = client.GetStream();
                if (Profiler.ReadPrompt(stream) == null) return threads; // the banner
                string Ask(string command)
                {
                    stream.Write(Encoding.ASCII.GetBytes(command + "\n"));
                    return Escape.Replace(Profiler.ReadPrompt(stream) ?? "", "");
                }

                Ask("stop"); // every CPU's registers from the same moment
                var cpus = CpuNumber.Matches(Ask("info cpus")).Select(m => int.Parse(m.Groups[1].Value)).Distinct().ToList();
                foreach (int cpu in cpus)
                {
                    Ask($"cpu {cpu}");
                    string registers = Ask("info registers");
                    if (Profiler.ParseRegisters(registers) is not Profiler.Sample sample) continue;
                    Match esp = Esp.Match(registers), ss = Ss.Match(registers);
                    var stack = new List<ulong>();
                    if (esp.Success)
                    {
                        uint top = uint.Parse(esp.Groups[1].Value, NumberStyles.HexNumber);
                        if (ss.Success) top += uint.Parse(ss.Groups[1].Value, NumberStyles.HexNumber); // real mode: ss * 16
                        foreach (Match line in MemoryLine.Matches(Ask($"x /256xw 0x{top:x}")))
                            stack.AddRange(line.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                               .Select(w => ulong.Parse(w[2..], NumberStyles.HexNumber)));
                    }
                    threads.Add(new StoppedThread($"cpu {cpu}", sample.Address, stack,
                                                  sample.Halted ? "waiting for an interrupt (hlt)" : null, User: sample.Cpl == 3, Always: true));
                }
            }
            catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException) { }
            return threads;
        }

        // ---- Windows: each thread of the process, stopped where it was

        const uint ThreadAccess = 0x0002 | 0x0008 | 0x0040; // suspend/resume, get context, query information
        const int ContextSize64 = 0x4D0, Flags64At = 0x30, Rsp64At = 0x98, Rip64At = 0xF8;
        const uint Context64 = 0x100003;                    // CONTEXT_AMD64 | CONTROL | INTEGER
        const int ContextSize32 = 0x2CC, Esp32At = 0xC4, Eip32At = 0xB8;
        const uint Context32 = 0x10003;                     // WOW64_CONTEXT_i386 | CONTROL | INTEGER

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenThread(uint access, bool inherit, uint id);
        [DllImport("kernel32.dll")] static extern uint SuspendThread(IntPtr thread);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetThreadContext(IntPtr thread, IntPtr context);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool Wow64GetThreadContext(IntPtr thread, IntPtr context);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, IntPtr size, out IntPtr read);

        /// <summary>Stops each thread of the program running from <paramref name="program"/>, and reads where it was.</summary>
        public static List<StoppedThread> FromWindows(string program, bool is64)
        {
            var threads = new List<StoppedThread>();
            if (!OperatingSystem.IsWindows()) return threads;
            return FromWindowsProcess(Path.GetFullPath(program), is64);
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        static List<StoppedThread> FromWindowsProcess(string full, bool is64)
        {
            var threads = new List<StoppedThread>();
            string program = full;
            foreach (Process p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(program)))
            {
                try
                {
                    if (!string.Equals(p.MainModule?.FileName, full, StringComparison.OrdinalIgnoreCase)) continue;
                    var each = p.Threads.Cast<ProcessThread>().OrderBy(t => t.StartTime).ToList();
                    var handles = each.Select(t => OpenThread(ThreadAccess, false, (uint)t.Id)).ToList();
                    foreach (IntPtr h in handles) if (h != IntPtr.Zero) SuspendThread(h);
                    for (int i = 0; i < handles.Count; i++)
                    {
                        if (handles[i] == IntPtr.Zero) continue;
                        if (Registers(handles[i], is64) is (ulong ip, ulong sp))
                            threads.Add(new StoppedThread($"thread {i + 1}", ip, ReadStack(p.Handle, sp, is64)));
                        CloseHandle(handles[i]);
                    }
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                finally { p.Dispose(); }
            }
            return threads;
        }

        static (ulong Ip, ulong Sp)? Registers(IntPtr thread, bool is64)
        {
            int size = is64 ? ContextSize64 : ContextSize32;
            IntPtr raw = Marshal.AllocHGlobal(size + 16);
            try
            {
                IntPtr context = (IntPtr)(((long)raw + 15) & ~15L); // CONTEXT is 16-byte aligned
                for (int k = 0; k < size; k += 4) Marshal.WriteInt32(context, k, 0);
                if (is64)
                {
                    Marshal.WriteInt32(context, Flags64At, (int)Context64);
                    if (!GetThreadContext(thread, context)) return null;
                    return ((ulong)Marshal.ReadInt64(context, Rip64At), (ulong)Marshal.ReadInt64(context, Rsp64At));
                }
                Marshal.WriteInt32(context, 0, (int)Context32);
                if (!Wow64GetThreadContext(thread, context)) return null;
                return ((uint)Marshal.ReadInt32(context, Eip32At), (uint)Marshal.ReadInt32(context, Esp32At));
            }
            finally { Marshal.FreeHGlobal(raw); }
        }

        /// <summary>The words from sp up, a piece at a time until the stack ends (up to 8 KB).</summary>
        static List<ulong> ReadStack(IntPtr process, ulong sp, bool is64)
        {
            var bytes = new List<byte>();
            var piece = new byte[512];
            for (int k = 0; k < 16; k++)
            {
                if (!ReadProcessMemory(process, (IntPtr)(long)(sp + (ulong)(k * piece.Length)), piece, piece.Length, out IntPtr read)
                    || (int)read != piece.Length) break;
                bytes.AddRange(piece);
            }
            return Words(bytes, is64 ? 8 : 4);
        }

        static List<ulong> Words(List<byte> bytes, int size)
        {
            var words = new List<ulong>();
            for (int i = 0; i + size <= bytes.Count; i += size)
            {
                ulong w = 0;
                for (int b = size - 1; b >= 0; b--) w = (w << 8) | bytes[i + b];
                words.Add(w);
            }
            return words;
        }

        // ---- Linux: each thread of the process, through /proc

        /// <summary>
        /// Stops the program (SIGSTOP) and, for each of its threads, prints "T tid" and what
        /// /proc/PID/task/TID/syscall says (the system call it's in, or -1, then its stack
        /// pointer and next instruction), then the top of its stack in hex; then ends it.
        /// </summary>
        const string LinuxScript = """
            p=$(cat "$1") || exit 0
            kill -STOP "$p" 2>/dev/null || exit 0
            sleep 0.2
            for t in /proc/$p/task/*; do
              s=$(cat "$t/syscall" 2>/dev/null)
              echo "T ${t##*/} $s"
              set -- $s
              if [ "$1" != running ] && [ $# -ge 3 ]; then
                eval sp=\${$(($# - 1))}
                dd if=/proc/$p/mem bs=512 iflag=skip_bytes,count_bytes skip=$((sp)) count=4096 2>/dev/null | od -An -v -tx1
              fi
            done
            kill -KILL "$p" 2>/dev/null
            """;

        /// <summary>Where each thread of the Linux program whose process number is in pidFile was.</summary>
        public static List<StoppedThread> FromLinux(string pidFile, bool is64, Func<string, string, string> runScript)
        {
            var threads = new List<StoppedThread>();
            string output;
            try { output = runScript(LinuxScript, pidFile); }
            catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) { return threads; }

            var found = new List<(int Tid, ulong Ip, string? Note, List<byte> Bytes)>();
            foreach (string line in output.Replace("\r", "").Split('\n'))
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                if (parts[0] == "T" && parts.Length >= 3 && int.TryParse(parts[1], out int tid))
                {
                    string[] s = parts[2..];
                    if (s[0] == "running" || s.Length < 3) { found.Add((tid, 0, "running", new List<byte>())); continue; }
                    ulong ip = ParseHex(s[^1]);
                    string? note = int.TryParse(s[0], out int number) && number >= 0 ? $"in system call {SystemCall(number, is64)}" : null;
                    found.Add((tid, ip, note, new List<byte>()));
                }
                else if (found.Count > 0)
                    found[^1].Bytes.AddRange(parts.Select(b => byte.Parse(b, NumberStyles.HexNumber)));
            }

            int n = 0;
            foreach (var f in found.OrderBy(f => f.Tid))
                threads.Add(new StoppedThread($"thread {++n}", f.Ip, Words(f.Bytes, is64 ? 8 : 4), f.Note));
            return threads;
        }

        /// <summary>A Linux system call's number, with its name if it's one a program waits in.</summary>
        static string SystemCall(int number, bool is64)
        {
            string? name = (is64, number) switch
            {
                (false, 3) or (true, 0) => "read",
                (false, 4) or (true, 1) => "write",
                (false, 162) or (true, 35) => "nanosleep",
                (false, 267) or (true, 230) => "clock_nanosleep",
                (false, 240) or (true, 202) => "futex",
                (false, 168) or (true, 7) => "poll",
                (false, 142) or (true, 23) => "select",
                (false, 158) or (true, 24) => "sched_yield",
                (false, 102) => "socketcall",
                (true, 45) => "recvfrom",
                (true, 43) => "accept",
                _ => null,
            };
            return name != null ? $"{number} ({name})" : number.ToString();
        }

        static ulong ParseHex(string s) =>
            s.StartsWith("0x") ? ulong.Parse(s[2..], NumberStyles.HexNumber) : ulong.Parse(s, NumberStyles.HexNumber);
    }
}
