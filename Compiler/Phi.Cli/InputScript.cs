using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace Phi.Cli
{
    /// <summary>
    /// Drives a test program's keyboard and mouse through the QEMU monitor.
    /// One command per line in NAME.input:
    ///
    ///   expect TEXT       wait until the serial output contains TEXT (up to 10 s)
    ///   wait MS           pause
    ///   type TEXT         press the keys for TEXT, one at a time
    ///   anything else     sent to the QEMU monitor as-is, e.g. sendkey ret, mouse_move 10 -5,
    ///                     mouse_button 1
    ///
    /// Lines starting with # are comments.
    /// </summary>
    public static class InputScript
    {
        static readonly TimeSpan ExpectTimeout = TimeSpan.FromSeconds(10);

        static readonly Dictionary<char, string> KeyNames = new()
        {
            [' '] = "spc", ['.'] = "dot", [','] = "comma", ['-'] = "minus", ['='] = "equal",
            ['/'] = "slash", [';'] = "semicolon", ['\''] = "apostrophe", ['['] = "bracket_left",
            [']'] = "bracket_right", ['\\'] = "backslash", ['`'] = "grave_accent",
            // symbols typed with Shift (on a US keyboard)
            [':'] = "shift-semicolon", ['?'] = "shift-slash", ['_'] = "shift-minus", ['+'] = "shift-equal",
            ['!'] = "shift-1", ['@'] = "shift-2", ['#'] = "shift-3", ['$'] = "shift-4", ['%'] = "shift-5",
            ['^'] = "shift-6", ['&'] = "shift-7", ['*'] = "shift-8", ['('] = "shift-9", [')'] = "shift-0",
            ['"'] = "shift-apostrophe", ['<'] = "shift-comma", ['>'] = "shift-dot", ['~'] = "shift-grave_accent",
            ['{'] = "shift-bracket_left", ['}'] = "shift-bracket_right", ['|'] = "shift-backslash",
        };

        /// <returns>null when every line ran, otherwise what went wrong</returns>
        public static string? Run(int port, IReadOnlyList<string> lines, Func<string> serial, CancellationToken cancel)
        {
            using TcpClient client = Connect(port, cancel);
            NetworkStream stream = client.GetStream();

            // the monitor talks back (banner, prompts, echo); read it so it never blocks
            _ = Task.Run(() =>
            {
                var buffer = new byte[1024];
                try { while (stream.Read(buffer, 0, buffer.Length) > 0) { } } catch { }
            });

            void Send(string command)
            {
                byte[] bytes = Encoding.ASCII.GetBytes(command + "\n");
                stream.Write(bytes, 0, bytes.Length);
                Delay(150, cancel); // sendkey holds a key for 100 ms
            }

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (cancel.IsCancellationRequested) return "stopped before the input script finished";

                string word = line.Split(' ', 2)[0];
                string rest = line.Length > word.Length ? line[(word.Length + 1)..] : "";

                switch (word)
                {
                    case "expect":
                    {
                        var clock = Stopwatch.StartNew();
                        while (!serial().Contains(rest))
                        {
                            if (clock.Elapsed > ExpectTimeout) return $"input script: never saw \"{rest}\"";
                            if (cancel.IsCancellationRequested) return $"stopped while waiting for \"{rest}\"";
                            Thread.Sleep(20);
                        }
                        break;
                    }

                    case "wait":
                        Delay(int.Parse(rest), cancel);
                        break;

                    case "type":
                        foreach (char c in rest) Send("sendkey " + KeyName(c));
                        break;

                    default:
                        Send(line);
                        break;
                }
            }

            return null;
        }

        static string KeyName(char c)
        {
            if (KeyNames.TryGetValue(c, out string? name)) return name;
            if (char.IsUpper(c)) return "shift-" + char.ToLower(c);
            return c.ToString();
        }

        static TcpClient Connect(int port, CancellationToken cancel)
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    return new TcpClient("127.0.0.1", port);
                }
                catch (SocketException) when (clock.Elapsed < TimeSpan.FromSeconds(5) && !cancel.IsCancellationRequested)
                {
                    Thread.Sleep(50);
                }
            }
        }

        static void Delay(int ms, CancellationToken cancel) => cancel.WaitHandle.WaitOne(ms);
    }
}
