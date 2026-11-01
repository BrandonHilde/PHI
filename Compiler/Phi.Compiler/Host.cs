namespace Phi.Compiler
{
    /// <summary>
    /// Where a program runs: on PHI OS (a user program on its disk), or as an ordinary program on
    /// another system. Only Program classes run on another system; kernels and 16-bit programs
    /// are PHI OS's own. Linux64 is a 64-bit Linux program: pointers are 8 bytes there, and every
    /// number keeps its size (int is 32 bits on every system).
    /// </summary>
    public enum Host { PhiOS, Linux, Windows, Linux64 }

    public static class Hosts
    {
        public static string Name(this Host host) => host switch
        {
            Host.Linux => "Linux",
            Host.Windows => "Windows",
            Host.Linux64 => "64-bit Linux",
            _ => "PHI OS",
        };

        /// <summary>The name a build flag uses (--target linux64), and a test mode's.</summary>
        public static string FlagName(this Host host) => host switch
        {
            Host.Linux => "linux",
            Host.Windows => "windows",
            Host.Linux64 => "linux64",
            _ => "phios",
        };

        /// <summary>A system whose programs are 64-bit code.</summary>
        public static bool Is64(this Host host) => host == Host.Linux64;

        /// <summary>A Linux program (32 or 64-bit): it runs directly on Linux, or under WSL on Windows.</summary>
        public static bool IsLinux(this Host host) => host is Host.Linux or Host.Linux64;

        /// <summary>The name a build flag uses (--target linux), or null if it isn't one.</summary>
        public static Host? Parse(string name) => name.ToLowerInvariant() switch
        {
            "linux" => Host.Linux,
            "windows" => Host.Windows,
            "linux64" => Host.Linux64,
            "phios" => Host.PhiOS,
            _ => null,
        };

        public static readonly string[] Names = { "phios", "linux", "windows", "linux64" };
    }
}
