using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Phi.Cli
{
    /// <summary>
    /// The machines of a program that has a NAME.machines file: the program is machine 1, and
    /// each line of NAME.machines names a .phi file next to it and how many copies of it run
    /// (1 if left out); # starts a comment. Each machine gets a disk of its own and a class
    /// Machine saying which it is: Machine.number (1, 2, 3, ... in that order), Machine.count,
    /// Machine.address (10.0.3.number, for Net.Start) and Machine.first (machine 1's address).
    /// phi run and phi test boot them all at once on a VirtualNetwork.
    /// </summary>
    public static class NetworkMachines
    {
        public sealed record Machine(string File, int Number, string Image);

        /// <summary>NAME.machines next to the program, or null if it has none.</summary>
        public static string? ManifestFor(string phiFile)
        {
            string manifest = Path.ChangeExtension(Path.GetFullPath(phiFile), ".machines");
            return File.Exists(manifest) ? manifest : null;
        }

        public static List<(string File, int Copies)> Read(string manifest)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(manifest))!;
            var machines = new List<(string, int)>();
            foreach (string raw in File.ReadAllLines(manifest))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                int copies = parts.Length > 1 && int.TryParse(parts[1], out int n) && n > 0 ? n : 1;
                machines.Add((Path.GetFullPath(Path.Combine(dir, parts[0])), copies));
            }
            return machines;
        }

        static string MachineClass(int number, int count) =>
            $"\nphi.Machine:Library\n{{\n\tu32 number: {number};\n\tu32 count: {count};\n" +
            $"\tu32 address: 0x0A0003{number:X2};\n\tu32 first: 0x0A000301;\n}}\n";

        /// <summary>
        /// Builds every machine, each into build/FILE (machine 1) or build/FILE.N next to its
        /// file; null, with the errors added, if one doesn't build.
        /// </summary>
        /// <param name="wrapper">more PHI text for every machine (the test runner's)</param>
        /// <param name="warnings">gets the machines' warnings, each once</param>
        public static List<Machine>? Build(string phiFile, string manifest, string wrapper, List<string> errors,
                                           List<string>? warnings = null)
        {
            var files = new List<string> { Path.GetFullPath(phiFile) };
            foreach (var (file, copies) in Read(manifest))
            {
                if (!File.Exists(file))
                {
                    errors.Add($"{Path.GetFileName(manifest)} lists {file}, which isn't there");
                    return null;
                }
                files.AddRange(Enumerable.Repeat(file, copies));
            }
            if (files.Count > 254)
            {
                errors.Add("a network can have at most 254 machines");
                return null;
            }

            var machines = new List<Machine>();
            for (int i = 0; i < files.Count; i++)
            {
                string file = files[i];
                int number = i + 1;
                BuildResult build = Builder.Build(file, Path.Combine(Path.GetDirectoryName(file)!, "build", Variant(file, number)),
                                                  wrapper + MachineClass(number, files.Count));
                if (warnings != null) warnings.AddRange(build.Warnings.Where(w => !warnings.Contains(w)));
                if (!build.Success)
                {
                    errors.Add($"machine {number} ({Path.GetFileName(file)}) didn't build:");
                    errors.AddRange(build.Errors);
                    return null;
                }
                machines.Add(new Machine(file, number, build.ImagePath));
            }
            return machines;
        }

        /// <summary>FILE for machine 1, FILE.N for the others: their build folders and .actual files.</summary>
        public static string Variant(string file, int number) =>
            number == 1 ? Path.GetFileNameWithoutExtension(file) : $"{Path.GetFileNameWithoutExtension(file)}.{number}";
    }

    /// <summary>
    /// One network for several QEMU machines in a test (NAME.machines): each connects to it
    /// with -netdev socket,connect=..., and every frame one of them sends goes to all the
    /// others, like a hub. QEMU's socket network sends a frame as its length (4 bytes, big
    /// endian) followed by the frame.
    /// </summary>
    public sealed class VirtualNetwork : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly List<NetworkStream> machines = new();
        readonly CancellationTokenSource stop = new();

        public int Port { get; }

        /// <summary>Frames passed on so far (each counted once, however many machines got it).</summary>
        public long Frames => Interlocked.Read(ref frames);
        long frames;

        public VirtualNetwork()
        {
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = Task.Run(Accept);
        }

        /// <summary>QEMU's arguments for machine number n (1 and up) on this network: its card, with a MAC address of its own.</summary>
        public string[] MachineArgs(int n) => new[]
        {
            "-netdev", $"socket,id=net0,connect=127.0.0.1:{Port}",
            "-device", $"rtl8139,netdev=net0,romfile=,mac=52:54:00:12:34:{n:x2}",
        };

        async Task Accept()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    TcpClient client = await listener.AcceptTcpClientAsync(stop.Token);
                    client.NoDelay = true;
                    NetworkStream stream = client.GetStream();
                    lock (machines) machines.Add(stream);
                    _ = Task.Run(() => Forward(stream));
                }
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { }
        }

        void Forward(NetworkStream from)
        {
            var header = new byte[4];
            try
            {
                while (true)
                {
                    if (!ReadAll(from, header)) break;
                    int length = BinaryPrimitives.ReadInt32BigEndian(header);
                    if (length <= 0 || length > 65536) break;
                    var frame = new byte[4 + length];
                    header.CopyTo(frame, 0);
                    if (!ReadAll(from, frame.AsMemory(4).Span)) break;
                    Interlocked.Increment(ref frames);

                    NetworkStream[] others;
                    lock (machines) others = machines.Where(m => m != from).ToArray();
                    foreach (NetworkStream to in others)
                    {
                        // one writer at a time on each machine's connection
                        try { lock (to) to.Write(frame); }
                        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
                    }
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            lock (machines) machines.Remove(from);
        }

        static bool ReadAll(NetworkStream stream, Span<byte> buffer)
        {
            int read = 0;
            while (read < buffer.Length)
            {
                int n = stream.Read(buffer[read..]);
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }

        public void Dispose()
        {
            stop.Cancel();
            listener.Stop();
            lock (machines)
                foreach (NetworkStream m in machines) m.Dispose();
        }
    }
}
