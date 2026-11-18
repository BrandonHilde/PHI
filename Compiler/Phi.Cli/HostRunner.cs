using System.Diagnostics;
using Phi.Compiler;

namespace Phi.Cli
{
    /// <summary>
    /// Runs a program built for another system (phi build --target linux or windows) on this
    /// computer: a Windows program directly on Windows, a Linux program directly on Linux, under
    /// WSL on Windows, or on a Mac in a Linux machine named by PHI_LINUX_RUN.
    /// </summary>
    public static class HostRunner
    {
        static bool? wsl;

        /// <summary>
        /// On a Mac, the command that runs one command in a Linux machine which sees this Mac's
        /// files at the same paths, from PHI_LINUX_RUN split at its spaces: "lima" (Lima),
        /// "orb" (OrbStack), or "docker run --rm -i -v /Users:/Users -w /Users ubuntu". Its
        /// arguments are the command's, as given. On Apple Silicon the machine is ARM, and a PHI
        /// program in it is run by qemu-user (or Rosetta) through binfmt_misc. Null when unset.
        /// </summary>
        static readonly string[]? LinuxMachine =
            OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("PHI_LINUX_RUN") is string run && run.Trim().Length > 0
                ? run.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                : null;

        /// <summary>Whether a Linux program runs here in another machine (PHI_LINUX_RUN), where its time isn't one to compare.</summary>
        public static bool InAnotherMachine => LinuxMachine != null;

        /// <summary>The Linux machine's processor (uname -m) once it has answered, "" when it didn't.</summary>
        static string? machineCpu;

        /// <summary>Whether this computer can run a program built for the host; if not, why not.</summary>
        public static bool CanRun(Host host, out string why)
        {
            why = "";
            switch (host)
            {
                case Host.Windows or Host.Windows64 when OperatingSystem.IsWindows():
                case Host.Linux or Host.Linux64 when OperatingSystem.IsLinux():
                    return true;
                case Host.Linux or Host.Linux64 when OperatingSystem.IsWindows():
                    wsl ??= HasWsl();
                    if (wsl == true) return true;
                    why = "Linux programs run here under WSL, which isn't installed (wsl --install)";
                    return false;
                case Host.Linux or Host.Linux64 when OperatingSystem.IsMacOS():
                    if (LinuxMachine == null)
                    {
                        why = "Linux programs run here in a Linux machine: set PHI_LINUX_RUN to the command that runs one in it (lima, say; see MacPlan.md)";
                        return false;
                    }
                    machineCpu ??= MachineCpu();
                    if (machineCpu.Length > 0) return true;
                    why = $"PHI_LINUX_RUN is '{string.Join(" ", LinuxMachine)}', but 'uname -m' didn't run with it: is the Linux machine started?";
                    return false;
                case Host.MacOS when OperatingSystem.IsMacOS()
                                     && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64:
                    return true;
                case Host.MacOS:
                    why = "macOS programs run on a Mac with Apple Silicon";
                    return false;
                default:
                    why = $"{host.Name()} programs can't run on this computer";
                    return false;
            }
        }

        static string MachineCpu()
        {
            // (its standard output alone: the machine's own warnings go to its standard error)
            var psi = new ProcessStartInfo(LinuxMachine![0])
            {
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false,
            };
            foreach (string a in LinuxMachine[1..].Append("uname").Append("-m")) psi.ArgumentList.Add(a);
            try
            {
                using Process p = Process.Start(psi)!;
                p.StandardInput.Close();
                Task<string> output = p.StandardOutput.ReadToEndAsync();
                _ = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(60000))
                {
                    try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    return "";
                }
                return p.ExitCode == 0 && output.Wait(2000) ? output.Result.Trim() : "";
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return "";
            }
        }

        /// <summary>
        /// Whether a stopped Linux program's /proc says where it was: not when an emulator runs it
        /// (an ARM machine's qemu-user or Rosetta), whose system call and registers are its own.
        /// </summary>
        static bool LinuxProcIsTheProgram => LinuxMachine == null || machineCpu is "x86_64" or "i686" or "i386";

        /// <summary>
        /// A shell command that uses a folder just made here, as the Linux machine runs it. Its
        /// shared folders (Lima's virtiofs) remember for a moment that a name wasn't there, so a
        /// build folder made a moment ago can be "Directory nonexistent" to the machine: tried
        /// again for up to 3 s, quietly, before it's let fail. Elsewhere, as it is.
        /// </summary>
        /// (The quiet tries are in braces with their 2>/dev/null outside: a redirection that fails is
        /// reported before one after it on the same command takes effect. The last try speaks,
        /// and the whole is its status.)
        static string Retried(string command, Host host) => LinuxMachine == null || !host.IsLinux() ? command
            : $"{{ n=0; until {{ {command}; }} 2>/dev/null; do n=$((n+1)); [ $n -ge 30 ] && break; sleep 0.1; done; [ $n -lt 30 ] || {command}; }}";

        /// <summary>
        /// How a Linux shell is started here: the command, the arguments that come before the
        /// shell's own (-c SCRIPT ...), and a path on this computer as that shell sees it.
        /// </summary>
        static (string Command, string[] Before, Func<string, string> PathOf) LinuxShell()
        {
            if (OperatingSystem.IsWindows()) return ("wsl.exe", new[] { "-e", "/bin/sh" }, WslPath);
            if (LinuxMachine != null) return (LinuxMachine[0], LinuxMachine[1..].Append("/bin/sh").ToArray(), Path.GetFullPath);
            return ("/bin/sh", Array.Empty<string>(), Path.GetFullPath);
        }

        static bool HasWsl()
        {
            try
            {
                var (code, _) = Toolchain.Run("wsl.exe", new[] { "-e", "true" });
                return code == 0;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        /// <summary>A Windows path as WSL sees it: C:\a\b is /mnt/c/a/b.</summary>
        static string WslPath(string path)
        {
            string full = Path.GetFullPath(path);
            return $"/mnt/{char.ToLowerInvariant(full[0])}{full[2..].Replace('\\', '/')}";
        }

        /// <summary>
        /// The process for the program, with its standard error going where its standard output
        /// does (in order, as a PHI OS program's debug lines are among its log lines on the serial
        /// port) when <paramref name="merge"/> is set. A Linux program's process number is written
        /// to <paramref name="pidFile"/>, so it can be found if it has to be stopped. With
        /// <paramref name="folder"/>, it runs there (where the files it opens are).
        /// </summary>
        static ProcessStartInfo Start(string program, Host host, IEnumerable<string> arguments, bool merge, string? pidFile = null,
                                      string? folder = null)
        {
            var args = arguments.ToList();
            if (host.IsWindows())
            {
                ProcessStartInfo windows;
                if (!merge)
                {
                    windows = new ProcessStartInfo(program);
                    foreach (string a in args) windows.ArgumentList.Add(a);
                }
                else
                {
                    // cmd joins the two; /s keeps the quotes around the program's path
                    string quoted = string.Join(" ", args.Select(a => $"\"{a}\""));
                    windows = new ProcessStartInfo("cmd.exe", $"/d /s /c \"\"{Path.GetFullPath(program)}\" {quoted} 2>&1\"");
                }
                if (folder != null) windows.WorkingDirectory = Path.GetFullPath(folder);
                return windows;
            }

            // (a macOS program runs here, as itself: only a Linux one goes to WSL or a Linux machine)
            var (command, before, pathOf) = host.IsLinux() ? LinuxShell() : ("/bin/sh", Array.Empty<string>(), (Func<string, string>)Path.GetFullPath);
            var psi = new ProcessStartInfo(command);
            foreach (string b in before) psi.ArgumentList.Add(b);
            psi.ArgumentList.Add("-c");
            string own = pidFile != null ? Retried($"echo $$ > '{pathOf(pidFile)}'", host) + "; " : "";
            if (folder != null) own = Retried($"cd '{pathOf(folder)}'", host) + " || exit 1; " + own;
            psi.ArgumentList.Add(own + (merge ? "exec \"$0\" \"$@\" 2>&1" : "exec \"$0\" \"$@\""));
            psi.ArgumentList.Add(pathOf(program));
            foreach (string a in args) psi.ArgumentList.Add(a);
            return psi;
        }

        /// <summary>Runs the program in this console, and gives its exit code.</summary>
        public static int RunInteractive(string program, Host host, IEnumerable<string> arguments)
        {
            using Process p = Process.Start(Start(program, host, arguments, merge: false))!;
            p.WaitForExit();
            return p.ExitCode;
        }

        /// <param name="Stopped">when it timed out: where each of its threads was (see Where)</param>
        public sealed record Result(string Output, int ExitCode, bool TimedOut, IReadOnlyList<StoppedThread> Stopped);

        /// <summary>
        /// Runs the program with nothing to read, collecting what it writes (both outputs). If it
        /// runs out of time, where each of its threads was is read before it's ended.
        /// </summary>
        public static Result Run(string program, Host host, TimeSpan timeout)
        {
            string? pidFile = host.IsLinux() ? program + ".pid" : null;
            if (pidFile != null && File.Exists(pidFile)) File.Delete(pidFile);
            ProcessStartInfo psi = Start(program, host, Array.Empty<string>(), merge: true, pidFile);
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardInput = true;
            psi.UseShellExecute = false;

            using Process p = Process.Start(psi)!;
            p.StandardInput.Close();
            Task<string> stdout = p.StandardOutput.ReadToEndAsync();
            Task<string> stderr = p.StandardError.ReadToEndAsync();
            bool finished = p.WaitForExit((int)timeout.TotalMilliseconds);
            List<StoppedThread> stopped = new();
            if (!finished)
            {
                stopped = host.IsWindows() ? Where.FromWindows(program, host.Is64())
                        : pidFile != null && File.Exists(pidFile) && LinuxProcIsTheProgram ? Where.FromLinux(pidFile, host.Is64(), RunLinuxScript) : stopped;
                // (in a Linux machine, ending the command that started it here needn't end it there)
                if (LinuxMachine != null && pidFile != null && File.Exists(pidFile))
                    RunLinuxScript("kill -KILL $(cat \"$1\") 2>/dev/null", pidFile);
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                p.WaitForExit();
                if (host.IsWindows()) EndLeftOver(program);
            }
            Task.WaitAll(stdout, stderr);
            return new Result(stdout.Result + stderr.Result, finished ? p.ExitCode : -1, !finished, stopped);
        }

        /// <summary>
        /// A program running beside others (phi load's server): what it has written so far
        /// (both outputs), until it's stopped.
        /// </summary>
        public sealed class Running : IDisposable
        {
            readonly Process process;
            readonly string program;
            readonly Host host;
            readonly string? pidFile;
            readonly System.Text.StringBuilder output = new();
            readonly object gate = new();

            internal Running(string program, Host host, string folder)
            {
                this.program = program;
                this.host = host;
                pidFile = host.IsLinux() ? program + ".pid" : null;
                if (pidFile != null && File.Exists(pidFile)) File.Delete(pidFile);
                ProcessStartInfo psi = HostRunner.Start(program, host, Array.Empty<string>(), merge: true, pidFile, folder);
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.RedirectStandardInput = true;
                psi.UseShellExecute = false;
                process = Process.Start(psi)!;
                process.StandardInput.Close();
                process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (gate) output.Append(e.Data).Append('\n'); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (gate) output.Append(e.Data).Append('\n'); };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }

            public string Output { get { lock (gate) return output.ToString(); } }

            public bool HasEnded => process.HasExited;

            /// <summary>Ends the program (if it hasn't ended), and waits until it has.</summary>
            public void Stop()
            {
                if (pidFile != null && File.Exists(pidFile))
                    RunLinuxScript("kill -KILL $(cat \"$1\") 2>/dev/null", pidFile);
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    process.WaitForExit(5000);
                }
                if (host.IsWindows()) EndLeftOver(program);
                process.WaitForExit(2000); // the last of its output
            }

            public void Dispose()
            {
                Stop();
                process.Dispose();
            }
        }

        /// <summary>Starts the program in <paramref name="folder"/>, with nothing to read, to run until it's stopped.</summary>
        public static Running StartBeside(string program, Host host, string folder) => new(program, host, folder);

        /// <summary>Ends whatever still runs from the program's file (one cmd.exe started, if it outlived cmd), and waits until it has.</summary>
        static void EndLeftOver(string program)
        {
            string full = Path.GetFullPath(program);
            foreach (Process left in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(program)))
            {
                try
                {
                    if (!string.Equals(left.MainModule?.FileName, full, StringComparison.OrdinalIgnoreCase)) continue;
                    left.Kill();
                    left.WaitForExit(5000);
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                finally { left.Dispose(); }
            }
        }

        /// <summary>Runs a shell script on Linux (under WSL on Windows, in PHI_LINUX_RUN's machine on a Mac) with one argument, a file's path, and gives what it printed.</summary>
        static string RunLinuxScript(string script, string file)
        {
            var (command, before, pathOf) = LinuxShell();
            var psi = new ProcessStartInfo(command)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            foreach (string b in before) psi.ArgumentList.Add(b);
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(script.Replace("\r", ""));
            psi.ArgumentList.Add("where");
            psi.ArgumentList.Add(pathOf(file));
            using Process p = Process.Start(psi)!;
            Task<string> output = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(20000)) try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return output.Wait(2000) ? output.Result : "";
        }
    }
}
