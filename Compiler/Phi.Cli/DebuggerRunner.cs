using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Phi.Compiler;

namespace Phi.Cli
{
    /// <summary>
    /// Runs a --debug build in Microsoft's console debugger, cdb, as a test does (NAME.cdb), and
    /// keeps what doesn't change from run to run. It's given no symbols but the program's own, so
    /// it doesn't go to Microsoft's server or keep a copy in the computer's symbol cache, and it
    /// has a console of its own that nobody sees.
    /// </summary>
    public static class DebuggerRunner
    {
        static readonly Regex Noise = new(@"^(eax=|eip=|cs=|WARNING|\*\*\*|NatVis|ModLoad|DBGHELP|SYMSRV|Child-SP|ChildEBP)|KERNEL32!|ntdll!");
        static readonly Regex StackLine = new(@"^(?:\d\d )?[0-9a-f`]+ [0-9a-f`]+\s+(\w+!\S.*)$");
        static readonly Regex CodeLine = new(@"^[0-9a-f`]{8,17} [0-9a-f]+ +\S");
        static readonly Regex Offset = new(@"(?<=\w)\+0x[0-9a-f]+");
        static readonly Regex Pointer = new(@"0x[0-9a-f`]{1,17}");
        static readonly Regex SourcePath = new(@"\[[A-Za-z]:\\.*\\([^\\]+ @ \d+)\]");

        /// <summary>cdb for this kind of program, or null if it isn't installed.</summary>
        public static string? Find(Host host)
        {
            string? chosen = Environment.GetEnvironmentVariable("PHI_CDB");
            if (!string.IsNullOrWhiteSpace(chosen)) return chosen;
            string kits = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)";
            string path = Path.Combine(kits, "Windows Kits", "10", "Debuggers", host.Is64() ? "x64" : "x86", "cdb.exe");
            return File.Exists(path) ? path : null;
        }

        /// <summary>What cdb said from its first .echo on, with what changes from run to run taken out.</summary>
        public static string Run(string executable, Host host, string script, TimeSpan timeout)
        {
            string folder = Path.GetFullPath(Path.GetDirectoryName(executable)!);
            var start = new ProcessStartInfo(Find(host)!)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string a in new[] { "-y", folder, "-cf", Path.GetFullPath(script), Path.GetFullPath(executable) })
                start.ArgumentList.Add(a);

            using Process p = Process.Start(start)!;
            Task<string> output = p.StandardOutput.ReadToEndAsync();
            Task<string> errors = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit((int)timeout.TotalMilliseconds))
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit();
            }
            return Keep(output.Result + errors.Result);
        }

        static string Keep(string said)
        {
            var kept = new StringBuilder();
            bool started = false;
            foreach (string raw in said.Split('\n'))
            {
                string line = raw.TrimEnd();
                if (line.StartsWith("---- ")) started = true;
                if (!started || line.Length == 0) continue;
                if (line.StartsWith("0:000> ")) line = line[7..];
                if (line.StartsWith(".echo ") || line == "q" || line == "quit:" || Noise.IsMatch(line)) continue;
                Match stack = StackLine.Match(line);
                if (stack.Success) line = stack.Groups[1].Value;
                else if (CodeLine.IsMatch(line)) continue;
                line = SourcePath.Replace(line, "[$1]");
                line = Offset.Replace(line, "");
                line = Pointer.Replace(line, "0x...");
                kept.Append(line).Append('\n');
            }
            return kept.ToString();
        }
    }
}
