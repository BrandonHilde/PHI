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
                case Host.Windows when OperatingSystem.IsWindows():
                case Host.Linux when OperatingSystem.IsLinux():
                    return true;
                case Host.Linux when OperatingSystem.IsWindows():
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
        /// port) when <paramref name="merge"/> is set.
        /// </summary>
        static ProcessStartInfo Start(string program, Host host, IEnumerable<string> arguments, bool merge)
        {
            var args = arguments.ToList();
            if (host == Host.Windows)
            {
                if (!merge)
                {
                    var direct = new ProcessStartInfo(program);
                    foreach (string a in args) direct.ArgumentList.Add(a);
                    return direct;
                }
                // cmd joins the two; /s keeps the quotes around the program's path
                string quoted = string.Join(" ", args.Select(a => $"\"{a}\""));
                return new ProcessStartInfo("cmd.exe", $"/d /s /c \"\"{Path.GetFullPath(program)}\" {quoted} 2>&1\"");
            }

            bool viaWsl = OperatingSystem.IsWindows();
            var psi = new ProcessStartInfo(viaWsl ? "wsl.exe" : "/bin/sh");
            if (viaWsl) psi.ArgumentList.Add("-e");
            if (viaWsl) psi.ArgumentList.Add("/bin/sh");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(merge ? "exec \"$0\" \"$@\" 2>&1" : "exec \"$0\" \"$@\"");
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

        public sealed record Result(string Output, int ExitCode, bool TimedOut);

        /// <summary>Runs the program with nothing to read, collecting what it writes (both outputs).</summary>
        public static Result Run(string program, Host host, TimeSpan timeout)
        {
            ProcessStartInfo psi = Start(program, host, Array.Empty<string>(), merge: true);
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardInput = true;
            psi.UseShellExecute = false;

            using Process p = Process.Start(psi)!;
            p.StandardInput.Close();
            Task<string> stdout = p.StandardOutput.ReadToEndAsync();
            Task<string> stderr = p.StandardError.ReadToEndAsync();
            bool finished = p.WaitForExit((int)timeout.TotalMilliseconds);
            if (!finished)
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                p.WaitForExit();
            }
            Task.WaitAll(stdout, stderr);
            return new Result(stdout.Result + stderr.Result, finished ? p.ExitCode : -1, !finished);
        }
    }
}
