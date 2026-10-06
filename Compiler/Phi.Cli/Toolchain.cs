using System.Diagnostics;

namespace Phi.Cli
{
    /// <summary>
    /// Locates the external tools PHI depends on (nasm, qemu) and runs them.
    /// Set PHI_NASM or PHI_QEMU to override the executable paths.
    /// </summary>
    public static class Toolchain
    {
        static readonly string[] NasmFallbacks =
        {
            @"C:\Program Files\NASM\nasm.exe",
            @"C:\Program Files (x86)\NASM\nasm.exe",
        };

        static readonly string[] QemuFallbacks =
        {
            @"C:\Program Files\qemu\qemu-system-i386.exe",
        };

        public static string Nasm => Find("PHI_NASM", "nasm", NasmFallbacks);
        public static string Qemu => Find("PHI_QEMU", "qemu-system-i386", QemuFallbacks);

        static string Find(string envVar, string name, string[] fallbacks)
        {
            string? env = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrWhiteSpace(env)) return env;

            string exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
            string[] dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);

            foreach (string dir in dirs)
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string candidate = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }

            foreach (string candidate in fallbacks)
                if (File.Exists(candidate)) return candidate;

            throw new ToolNotFoundException(name, envVar);
        }

        /// <summary>Runs a tool to completion and captures its output.</summary>
        public static (int ExitCode, string Output) Run(string exe, IEnumerable<string> args)
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);

            using Process p = Process.Start(psi)!;
            Task<string> stdout = p.StandardOutput.ReadToEndAsync();
            Task<string> stderr = p.StandardError.ReadToEndAsync();
            p.WaitForExit();

            return (p.ExitCode, (stdout.Result + stderr.Result).Trim());
        }
    }

    public class ToolNotFoundException : Exception
    {
        public ToolNotFoundException(string tool, string envVar)
            : base($"Could not find '{tool}'. Install it and add it to PATH, or set {envVar} to its full path.") { }
    }
}
