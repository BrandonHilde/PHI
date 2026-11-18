namespace Phi.Compiler
{
    /// <summary>
    /// Where a program runs: on PHI OS (a user program on its disk), or as an ordinary program on
    /// another system. Only Program classes run on another system; kernels and 16-bit programs
    /// are PHI OS's own. Linux64 and Windows64 are 64-bit programs: pointers are 8 bytes there,
    /// and every number keeps its size (int is 32 bits on every system). MacOS is a 64-bit macOS
    /// program for Apple Silicon (arm64): ARM code, assembled and linked by Apple's clang, that
    /// reaches the system through libSystem (macOS has no system calls a program may make).
    /// </summary>
    public enum Host { PhiOS, Linux, Windows, Linux64, Windows64, MacOS }

    public static class Hosts
    {
        public static string Name(this Host host) => host switch
        {
            Host.Linux => "Linux",
            Host.Windows => "Windows",
            Host.Linux64 => "64-bit Linux",
            Host.Windows64 => "64-bit Windows",
            Host.MacOS => "macOS",
            _ => "PHI OS",
        };

        /// <summary>The name a build flag uses (--target linux64), and a test mode's.</summary>
        public static string FlagName(this Host host) => host switch
        {
            Host.Linux => "linux",
            Host.Windows => "windows",
            Host.Linux64 => "linux64",
            Host.Windows64 => "windows64",
            Host.MacOS => "macos",
            _ => "phios",
        };

        /// <summary>A system whose programs are 64-bit code (pointers are 8 bytes).</summary>
        public static bool Is64(this Host host) => host is Host.Linux64 or Host.Windows64 or Host.MacOS;

        /// <summary>A system whose programs are ARM code (arm64), not x86: written by CodeGen/A64, not by X86Generator.</summary>
        public static bool IsArm(this Host host) => host is Host.MacOS;

        /// <summary>A macOS program (Apple Silicon): a Mach-O executable that reaches the system through libSystem.</summary>
        public static bool IsMacOS(this Host host) => host is Host.MacOS;

        /// <summary>A Linux program (32 or 64-bit): it runs directly on Linux, or under WSL on Windows.</summary>
        public static bool IsLinux(this Host host) => host is Host.Linux or Host.Linux64;

        /// <summary>A Windows program (32 or 64-bit): an .exe that reaches the system through kernel32.dll.</summary>
        public static bool IsWindows(this Host host) => host is Host.Windows or Host.Windows64;

        /// <summary>The name a build flag uses (--target linux), or null if it isn't one.</summary>
        public static Host? Parse(string name) => name.ToLowerInvariant() switch
        {
            "linux" => Host.Linux,
            "windows" => Host.Windows,
            "linux64" => Host.Linux64,
            "windows64" => Host.Windows64,
            "macos" => Host.MacOS,
            "phios" => Host.PhiOS,
            _ => null,
        };

        public static readonly string[] Names = { "phios", "linux", "windows", "linux64", "windows64", "macos" };
    }
}
