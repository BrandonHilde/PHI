using System.Diagnostics;
using Phi.Compiler;

namespace Phi.Cli
{
    /// <summary>
    /// Runs a program built for another system (phi build --target linux or windows) on this
    /// computer: a Windows program directly on Windows, a Linux program directly on Linux, or under
    /// WSL on Windows.
    /// </summary>
    public static class HostRunner
    {
        static bool? wsl;

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
                default:
                    why = $"{host.Name()} programs can't run on this computer";
                    return false;
            }
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

            bool viaWsl = OperatingSystem.IsWindows();
            var psi = new ProcessStartInfo(viaWsl ? "wsl.exe" : "/bin/sh");
            if (viaWsl) psi.ArgumentList.Add("-e");
            if (viaWsl) psi.ArgumentList.Add("/bin/sh");
            psi.ArgumentList.Add("-c");
            string own = pidFile != null ? $"echo $$ > '{(viaWsl ? WslPath(pidFile) : pidFile)}'; " : "";
            if (folder != null) own = $"cd '{(viaWsl ? WslPath(folder) : Path.GetFullPath(folder))}' || exit 1; " + own;
            psi.ArgumentList.Add(own + (merge ? "exec \"$0\" \"$@\" 2>&1" : "exec \"$0\" \"$@\""));
            psi.ArgumentList.Add(viaWsl ? WslPath(program) : Path.GetFullPath(program));
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
                        : pidFile != null && File.Exists(pidFile) ? Where.FromLinux(pidFile, host.Is64(), RunLinuxScript) : stopped;
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

        /// <summary>Runs a shell script on Linux (under WSL on Windows) with one argument, a file's path, and gives what it printed.</summary>
        static string RunLinuxScript(string script, string file)
        {
            bool viaWsl = OperatingSystem.IsWindows();
            var psi = new ProcessStartInfo(viaWsl ? "wsl.exe" : "/bin/sh")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            if (viaWsl) { psi.ArgumentList.Add("-e"); psi.ArgumentList.Add("/bin/sh"); }
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(script.Replace("\r", ""));
            psi.ArgumentList.Add("where");
            psi.ArgumentList.Add(viaWsl ? WslPath(file) : file);
            using Process p = Process.Start(psi)!;
            Task<string> output = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(20000)) try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return output.Wait(2000) ? output.Result : "";
        }
    }
}
