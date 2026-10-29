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

        /// <summary>
        /// QEMU ended without the program writing to isa-debug-exit: a triple fault (which
        /// -no-reboot turns into an exit), a reboot, or QEMU failing to start.
        /// </summary>
        public bool StoppedByItself { get; init; }

        /// <summary>What QEMU printed to stderr.</summary>
        public string QemuErrors { get; init; } = string.Empty;

        /// <summary>
        /// Why a run that didn't produce the right output stopped, so a timeout or a triple fault
        /// isn't taken for something else; <paramref name="otherwise"/> when it stopped normally.
        /// </summary>
        public string Explain(TimeSpan timeout, string otherwise)
        {
            string? input = InputError != null ? $" ({InputError})" : null;
            if (TimedOut)
                return $"timed out after {timeout.TotalSeconds:0}s; use --timeout <sec> to allow longer{input}";
            if (StoppedByItself)
            {
                string why = "QEMU stopped by itself before the program called exit: most likely a triple fault " +
                             "(phi run --debug logs interrupts and resets to find it)";
                if (QemuErrors.Trim().Length > 0) why += $"\nQEMU said: {QemuErrors.Trim()}";
                return why + input;
            }
            return InputError ?? otherwise;
        }
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
        // The sound card is an AC'97 (drivers.ac97); audio is the -audiodev that says where its
        // sound goes (always with id=snd0).
        static List<string> MachineArgs(string image, string audio) => new()
        {
            "-cpu", "qemu32,+rdrand",
            "-drive", $"file={image},format=raw,if=ide,index=0",
            "-device", $"isa-debug-exit,iobase=0x{DebugExitPort:x},iosize=0x04",
            "-netdev", "user,id=net0",
            "-device", "rtl8139,netdev=net0,romfile=",
            "-audiodev", audio,
            "-device", "AC97,audiodev=snd0",
            "-no-reboot", // a triple fault stops QEMU instead of looping forever
        };

        // Where the sound goes when QEMU has a window: PHI_AUDIO if it's set (a QEMU audio
        // driver, such as none, sdl or wav), otherwise the first of these that this QEMU has.
        static readonly string[] SpeakerDrivers = { "dsound", "coreaudio", "pipewire", "pa", "alsa", "sdl" };

        static string SpeakerAudio()
        {
            string? chosen = Environment.GetEnvironmentVariable("PHI_AUDIO");
            if (string.IsNullOrWhiteSpace(chosen))
            {
                var (_, output) = Toolchain.Run(Toolchain.Qemu, new[] { "-audiodev", "help" });
                var available = output.Split('\n').Select(l => l.Trim()).ToHashSet();
                chosen = SpeakerDrivers.FirstOrDefault(available.Contains) ?? "none";
            }
            return chosen.Contains("id=") ? chosen : $"{chosen},id=snd0";
        }

        /// <summary>QEMU's -audiodev for recording the sound card to a WAV file.</summary>
        static string RecordAudio(string wavPath) => $"wav,id=snd0,path={wavPath.Replace(",", ",,")}";

        /// <summary>Runs QEMU with a window; serial output goes to this terminal.</summary>
        public static int RunInteractive(string image, bool debug)
        {
            var args = MachineArgs(image, SpeakerAudio());
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
        /// With <paramref name="recordTo"/>, what the sound card plays is saved there as a WAV file,
        /// and QEMU gets a few seconds to exit by itself (which finishes the file) once the output is done.
        /// </summary>
        public static HeadlessResult RunHeadless(string image, TimeSpan timeout, Func<string, bool>? isDone = null,
                                                 IReadOnlyList<string>? input = null, string? recordTo = null,
                                                 string? screenshots = null)
        {
            int? monitorPort = input != null ? FreePort() : null;

            var args = MachineArgs(image, recordTo != null ? RecordAudio(recordTo) : "none,id=snd0");
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
            Task<string> errors = p.StandardError.ReadToEndAsync();

            using var cancel = new CancellationTokenSource();
            Task<string?>? script = null;
            if (input != null)
            {
                string Serial() { lock (gate) return serial.ToString(); }
                script = Task.Run(() => InputScript.Run(monitorPort!.Value, input, Serial, cancel.Token, screenshots));
            }

            var clock = Stopwatch.StartNew();
            bool timedOut = false;
            TimeSpan? doneAt = null;

            while (!p.HasExited)
            {
                string text;
                lock (gate) text = serial.ToString();

                bool inputFinished = script == null || script.IsCompleted;
                bool matched = isDone != null && isDone(text);
                if (doneAt == null && inputFinished && matched) doneAt = clock.Elapsed;

                // after the expected output shows up, wait a moment to catch anything extra
                TimeSpan grace = TimeSpan.FromMilliseconds(recordTo != null ? 3000 : 300);
                if (doneAt != null && clock.Elapsed - doneAt > grace) break;

                // the right output with the input script still going isn't a hang: let the script
                // finish (each of its lines ends by itself)
                if (clock.Elapsed > timeout && !(matched && !inputFinished)) { timedOut = true; break; }

                Thread.Sleep(25);
            }

            int? exitValue = null;
            bool byItself = false;
            if (p.HasExited)
            {
                byItself = p.ExitCode % 2 == 0;
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

            string qemuErrors = errors.Wait(TimeSpan.FromSeconds(2)) ? errors.Result : "";

            lock (gate)
                return new HeadlessResult
                {
                    Serial = serial.ToString(), TimedOut = timedOut, DebugExitValue = exitValue, InputError = inputError,
                    StoppedByItself = byItself, QemuErrors = byItself ? qemuErrors : "",
                };
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
