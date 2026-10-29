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
    ///   pixel X Y RRGGBB  check the color on the screen at (X, Y), in hex
    ///   screenshot NAME   save the screen as NAME.png in the test's build folder
    ///   mouse_to X Y      move the mouse pointer to (X, Y) on the screen
    ///   click X Y         move there and click the left button
    ///   dclick X Y        move there and double-click
    ///   drag X1 Y1 X2 Y2  press the left button at (X1, Y1), move to (X2, Y2) and let go
    ///   reboot            restart the machine on the same disk, so what the kernel saved is
    ///                     still there (the serial output carries on from before)
    ///   anything else     sent to the QEMU monitor as-is, e.g. sendkey ret, mouse_move 10 -5,
    ///                     mouse_button 1
    ///
    /// QEMU only moves the mouse by amounts, so the first of mouse_to, click, dclick and drag
    /// first pushes the pointer into the top left corner, where a driver stops it at (0, 0)
    /// (lib/phi/drivers/input.phi does); after that the script keeps track of where it is.
    ///
    /// Lines starting with # are comments.
    /// </summary>
    public static class InputScript
    {
        static readonly TimeSpan ExpectTimeout = TimeSpan.FromSeconds(10);

        /// <summary>A line that restarts the machine, which QEMU has to be started ready for.</summary>
        public static bool IsReboot(string line) => line.Trim() == "reboot";

        /// <summary>The most a single mouse_move goes along each axis; QEMU sends this much at once.</summary>
        const int MouseStep = 200;

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
        public static string? Run(int port, IReadOnlyList<string> lines, Func<string> serial, CancellationToken cancel,
                                  string? folder = null)
        {
            folder ??= Path.GetTempPath();
            Screenshot? screen = null; // consecutive pixel checks share one screendump
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

            // where the pointer is, once a mouse_to (or click, ...) has put it in the corner
            int? mouseX = null, mouseY = null;
            void MoveTo(int x, int y)
            {
                if (mouseX == null)
                {
                    // in steps: QEMU holds on to a move bigger than it can send at once, and would
                    // take what's left of it off the moves that come next
                    for (int i = 0; i < 10; i++) Send("mouse_move -250 -250");
                    Delay(200, cancel);
                    mouseX = mouseY = 0;
                }
                // a big move in steps, for the same reason: otherwise a click right after it can be
                // pressed while the pointer is still on its way
                int dx = x - mouseX.Value, dy = y - mouseY!.Value;
                do
                {
                    int stepX = Math.Clamp(dx, -MouseStep, MouseStep), stepY = Math.Clamp(dy, -MouseStep, MouseStep);
                    Send($"mouse_move {stepX} {stepY}");
                    dx -= stepX;
                    dy -= stepY;
                } while (dx != 0 || dy != 0);
                mouseX = x;
                mouseY = y;
                Delay(60, cancel);
            }

            // the line being run, so a script cut short can say where it was
            int lineNumber = 0;
            string? running = null;
            string Stopped() => running == null
                ? "the input script hadn't started"
                : $"the input script was at line {lineNumber}: {running}";

            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (cancel.IsCancellationRequested) return Stopped();
                lineNumber = i + 1;
                running = line;

                string word = line.Split(' ', 2)[0];
                string rest = line.Length > word.Length ? line[(word.Length + 1)..] : "";
                if (word != "pixel") screen = null;

                switch (word)
                {
                    case "expect":
                    {
                        var clock = Stopwatch.StartNew();
                        while (!serial().Contains(rest))
                        {
                            if (clock.Elapsed > ExpectTimeout) return $"input script: never saw \"{rest}\"";
                            if (cancel.IsCancellationRequested) return Stopped();
                            Thread.Sleep(20);
                        }
                        break;
                    }

                    case "wait":
                        Delay(int.Parse(rest), cancel);
                        break;

                    case "reboot":
                        Send("system_reset");
                        mouseX = mouseY = null;   // the mouse driver starts again in the middle
                        break;

                    case "type":
                        foreach (char c in rest) Send("sendkey " + KeyName(c));
                        break;

                    case "pixel":
                    {
                        string[] parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length != 3 || !int.TryParse(parts[0], out int x) || !int.TryParse(parts[1], out int y)
                            || !int.TryParse(parts[2], System.Globalization.NumberStyles.HexNumber, null, out int wanted))
                            return $"input script: expected 'pixel X Y RRGGBB' but found \"{line}\"";

                        screen ??= Capture(Send, Path.Combine(folder, "pixel-check.ppm"), cancel);
                        if (screen == null) return "input script: QEMU didn't save the screen";
                        if (x < 0 || y < 0 || x >= screen.Width || y >= screen.Height)
                            return $"input script: ({x}, {y}) is off the {screen.Width}x{screen.Height} screen";

                        int got = screen.Pixel(x, y);
                        if (got != wanted) return $"input script: the pixel at ({x}, {y}) is {got:X6}, not {wanted:X6}";
                        break;
                    }

                    case "screenshot":
                    {
                        Screenshot? shot = Capture(Send, Path.Combine(folder, rest + ".ppm"), cancel);
                        if (shot == null) return "input script: QEMU didn't save the screen";
                        shot.SavePng(Path.Combine(folder, rest + ".png"));
                        break;
                    }

                    case "mouse_to" or "click" or "dclick" or "drag":
                    {
                        int count = word == "drag" ? 4 : 2;
                        var numbers = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                            .Select(p => int.TryParse(p, out int n) && n >= 0 ? n : -1).ToList();
                        if (numbers.Count != count || numbers.Contains(-1))
                            return $"input script: expected '{word} {(count == 4 ? "X1 Y1 X2 Y2" : "X Y")}' but found \"{line}\"";

                        MoveTo(numbers[0], numbers[1]);
                        switch (word)
                        {
                            case "click":
                                Send("mouse_button 1");
                                Send("mouse_button 0");
                                Delay(100, cancel);
                                break;
                            case "dclick":
                                Send("mouse_button 1");
                                Send("mouse_button 0");
                                Send("mouse_button 1");
                                Send("mouse_button 0");
                                Delay(100, cancel);
                                break;
                            case "drag":
                                Send("mouse_button 1");
                                MoveTo(numbers[2], numbers[3]);
                                Send("mouse_button 0");
                                Delay(100, cancel);
                                break;
                        }
                        break;
                    }

                    default:
                        // a relative move written in the script keeps the position known
                        string[] move = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (mouseX != null && move.Length >= 3 && move[0] == "mouse_move"
                            && int.TryParse(move[1], out int dx) && int.TryParse(move[2], out int dy))
                        {
                            mouseX = Math.Max(0, mouseX.Value + dx);
                            mouseY = Math.Max(0, mouseY!.Value + dy);
                        }
                        Send(line);
                        break;
                }
            }

            return null;
        }

        /// <summary>Has QEMU save the screen as a PPM file, and reads it once it's complete.</summary>
        static Screenshot? Capture(Action<string> send, string ppm, CancellationToken cancel)
        {
            if (File.Exists(ppm)) File.Delete(ppm);
            send("screendump " + Path.GetFullPath(ppm).Replace('\\', '/'));

            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(5) && !cancel.IsCancellationRequested)
            {
                try
                {
                    if (File.Exists(ppm) && Screenshot.ReadPpm(File.ReadAllBytes(ppm)) is Screenshot shot)
                    {
                        File.Delete(ppm);
                        return shot;
                    }
                }
                catch (IOException) { } // still being written
                Thread.Sleep(50);
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
