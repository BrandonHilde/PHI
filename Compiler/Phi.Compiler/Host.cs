namespace Phi.Compiler
{
    /// <summary>
    /// Where a program runs: on PHI OS (a user program on its disk), or as an ordinary program on
    /// another system. Only Program classes run on another system; kernels and 16-bit programs
    /// are PHI OS's own.
    /// </summary>
    public enum Host { PhiOS, Linux, Windows }

    public static class Hosts
    {
        public static string Name(this Host host) => host switch
        {
            Host.Linux => "Linux",
            Host.Windows => "Windows",
            _ => "PHI OS",
        };

        /// <summary>The name a build flag uses (--target linux), or null if it isn't one.</summary>
        public static Host? Parse(string name) => name.ToLowerInvariant() switch
        {
            "linux" => Host.Linux,
            "windows" => Host.Windows,
            "phios" => Host.PhiOS,
            _ => null,
        };

        public static readonly string[] Names = { "phios", "linux", "windows" };
    }
}
