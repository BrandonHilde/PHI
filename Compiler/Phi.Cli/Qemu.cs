using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Phi.Cli
{
    public sealed class HeadlessResult
    {
        public string Serial { get; init; } = string.Empty;
        public bool TimedOut { get; init; }

        /// <summary>The value PHI wrote to the isa-debug-exit port, or null if QEMU didn't exit that way.</summary>
        public int? DebugExitValue { get; init; }

        /// <summary>Set when the input script couldn't run to the end.</summary>
        public string? InputError { get; init; }
    }

    public static class Qemu
    {
        // Writing value v to port 0xF4 makes QEMU exit with code (v << 1) | 1.
        public const int DebugExitPort = 0xF4;

        // The network: an RTL8139 card (drivers.rtl8139) on QEMU's user-mode network, which
        // gives the guest 10.0.2.15, a gateway at 10.0.2.2 (that is also the host's 127.0.0.1)
        // and a DNS server at 10.0.2.3. romfile= leaves out the card's boot ROM (iPXE).
        // The CPU is QEMU's usual one plus RDRAND, which crypto.random uses for random numbers
        // (QEMU answers it with the host's random numbers).
        static List<string> MachineArgs(string image) => new()
        {
            "-cpu", "qemu32,+rdrand",
            "-drive", $"file={image},format=raw,if=ide,index=0",
            "-device", $"isa-debug-exit,iobase=0x{DebugExitPort:x},iosize=0x04",
            "-netdev", "user,id=net0",
            "-device", "rtl8139,netdev=net0,romfile=",
            "-no-reboot", // a triple fault stops QEMU instead of looping forever
        };

        /// <summary>Runs QEMU with a window; serial output goes to this terminal.</summary>
        public static int RunInteractive(string image, bool debug)
        {
            var args = MachineArgs(image);
            args.AddRange(new[] { "-serial", "stdio" });

            if (debug)
            {
                // -s: gdb server on tcp::1234, -S: wait for the debugger before running
                args.AddRange(new[] { "-s", "-S", "-d", "int,cpu_reset", "-D", Path.ChangeExtension(image, ".qemu.log") });
                Console.WriteLine("QEMU is paused, waiting for a debugger on localhost:1234.");
                Console.WriteLine("  gdb -ex \"target remote localhost:1234\" -ex \"set architecture i8086\" -ex \"break *0x7c00\" -ex continue");
                Console.WriteLine($"Interrupt/reset trace: {Path.ChangeExtension(image, ".qemu.log")}");
            }

            var psi = new ProcessStartInfo(Toolchain.Qemu) { UseShellExecute = false };
            foreach (string a in args) psi.ArgumentList.Add(a);

            using Process p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode;
        }

        /// <summary>
        /// Runs QEMU with no display, collecting serial output until the guest exits through
        /// isa-debug-exit, <paramref name="isDone"/> returns true, or the timeout passes.
        /// <paramref name="input"/> is a script of QEMU monitor commands (see InputScript).
        /// </summary>
        public static HeadlessResult RunHeadless(string image, TimeSpan timeout, Func<string, bool>? isDone = null,
                                                 IReadOnlyList<string>? input = null)
        {
            int? monitorPort = input != null ? FreePort() : null;

            var args = MachineArgs(image);
            args.AddRange(new[] { "-display", "none", "-serial", "stdio", "-monitor",
                                  monitorPort != null ? $"tcp:127.0.0.1:{monitorPort},server,nowait" : "none" });

            var psi = new ProcessStartInfo(Toolchain.Qemu)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);

            var serial = new StringBuilder();
            object gate = new();

            using Process p = Process.Start(psi)!;

            Task reader = Task.Run(() =>
            {
                char[] buf = new char[256];
                int n;
                while ((n = p.StandardOutput.Read(buf, 0, buf.Length)) > 0)
                    lock (gate) serial.Append(buf, 0, n);
            });
            _ = p.StandardError.ReadToEndAsync();

            using var cancel = new CancellationTokenSource();
            Task<string?>? script = null;
            if (input != null)
            {
                string Serial() { lock (gate) return serial.ToString(); }
                script = Task.Run(() => InputScript.Run(monitorPort!.Value, input, Serial, cancel.Token));
            }

            var clock = Stopwatch.StartNew();
            bool timedOut = false;
            TimeSpan? doneAt = null;

            while (!p.HasExited)
            {
                string text;
                lock (gate) text = serial.ToString();

                bool inputFinished = script == null || script.IsCompleted;
                if (doneAt == null && inputFinished && isDone != null && isDone(text)) doneAt = clock.Elapsed;

                // after the expected output shows up, wait a moment to catch anything extra
                if (doneAt != null && clock.Elapsed - doneAt > TimeSpan.FromMilliseconds(300)) break;

                if (clock.Elapsed > timeout) { timedOut = true; break; }

                Thread.Sleep(25);
            }

            int? exitValue = null;
            if (p.HasExited)
            {
                // isa-debug-exit always produces an odd exit code
                if (p.ExitCode % 2 == 1) exitValue = p.ExitCode >> 1;
            }
            else
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit();
            }

            cancel.Cancel();
            reader.Wait(TimeSpan.FromSeconds(2));
            string? inputError = null;
            if (script != null)
            {
                try { inputError = script.Wait(TimeSpan.FromSeconds(2)) ? script.Result : "the input script did not finish"; }
                catch (AggregateException) { inputError = "the input script was stopped"; }
            }

            lock (gate)
                return new HeadlessResult { Serial = serial.ToString(), TimedOut = timedOut, DebugExitValue = exitValue, InputError = inputError };
        }

        static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
