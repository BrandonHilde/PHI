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
    /// phi run and phi test boot them all at once on a VirtualNetwork. A line "lose 1 in N"
    /// makes the network lose every Nth frame, to test what happens when frames go missing,
    /// and "card virtio" gives every machine a virtio-net card in place of an RTL8139. As a
    /// cloud's network might: "mtu N" drops every frame whose IP packet is more than N bytes,
    /// and "dhcp" has the network answer DHCP itself, giving machine N its address
    /// (10.0.3.N) and the MTU, so a machine can Net.Start with DHCP.
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
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("lose ") || line.StartsWith("card ") ||
                    line.StartsWith("mtu ") || line == "dhcp") continue;
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                int copies = parts.Length > 1 && int.TryParse(parts[1], out int n) && n > 0 ? n : 1;
                machines.Add((Path.GetFullPath(Path.Combine(dir, parts[0])), copies));
            }
            return machines;
        }

        /// <summary>The card from a line "card virtio" (or "card rtl8139"), or null if there's none.</summary>
        public static string? Card(string manifest)
        {
            foreach (string raw in File.ReadAllLines(manifest))
            {
                string[] parts = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[0] == "card" && Qemu.NetworkCards.Contains(parts[1])) return parts[1];
            }
            return null;
        }

        /// <summary>N from a line "lose 1 in N", or 0 if there's none.</summary>
        public static int LoseEvery(string manifest)
        {
            foreach (string raw in File.ReadAllLines(manifest))
            {
                string[] parts = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 4 && parts[0] == "lose" && parts[1] == "1" && parts[2] == "in" &&
                    int.TryParse(parts[3], out int n) && n > 1)
                    return n;
            }
            return 0;
        }

        /// <summary>N from a line "mtu N" (the most an IP packet may hold), or 0 if there's none.</summary>
        public static int Mtu(string manifest)
        {
            foreach (string raw in File.ReadAllLines(manifest))
            {
                string[] parts = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[0] == "mtu" && int.TryParse(parts[1], out int n) && n >= 576 && n <= 1500)
                    return n;
            }
            return 0;
        }

        /// <summary>Whether there's a line "dhcp": the network answers DHCP itself.</summary>
        public static bool Dhcp(string manifest) => File.ReadAllLines(manifest).Any(l => l.Trim() == "dhcp");

        static string MachineClass(int number, int count) => MachineClass(number, count, 0x0A000300u + (uint)number, 0x0A000301);

        /// <summary>The class Machine with these values (phi load gives a server QEMU's user-network address).</summary>
        public static string MachineClass(int number, int count, uint address, uint first) =>
            $"\nphi.Machine:Library\n{{\n\tu32 number: {number};\n\tu32 count: {count};\n" +
            $"\tu32 address: 0x{address:X8};\n\tu32 first: 0x{first:X8};\n}}\n";

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
    /// with -netdev socket,connect=..., and it passes frames between them like a switch:
    /// a frame goes to the machine with its destination address, or to every other machine
    /// when that's a broadcast or an address it hasn't seen yet. QEMU's socket network sends
    /// a frame as its length (4 bytes, big endian) followed by the frame.
    /// </summary>
    public sealed class VirtualNetwork : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly List<NetworkStream> machines = new();
        readonly Dictionary<ulong, NetworkStream> where = new();   // which machine has each Ethernet address
        readonly CancellationTokenSource stop = new();

        public int Port { get; }

        /// <summary>Frames passed on so far (each counted once, however many machines got it).</summary>
        public long Frames => Interlocked.Read(ref frames);
        long frames;

        /// <summary>Every this many frames, one is lost on purpose (0: none), for testing what happens then.</summary>
        readonly int loseEvery;

        /// <summary>Frames lost on purpose so far.</summary>
        public long Lost => Interlocked.Read(ref lost);
        long lost;

        /// <summary>The card every machine gets: rtl8139 or virtio.</summary>
        readonly string card;

        /// <summary>The most an IP packet may hold here (0: as much as Ethernet takes); bigger ones are dropped.</summary>
        readonly int mtu;

        /// <summary>Frames dropped for being bigger than the MTU.</summary>
        public long TooBig => Interlocked.Read(ref tooBig);
        long tooBig;

        /// <summary>Whether the network answers DHCP itself (see DhcpAnswer).</summary>
        readonly bool dhcp;

        public VirtualNetwork(int loseEvery = 0, string? card = null, int mtu = 0, bool dhcp = false)
        {
            this.loseEvery = loseEvery;
            this.card = card ?? Qemu.NetworkCard;
            this.mtu = mtu;
            this.dhcp = dhcp;
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = Task.Run(Accept);
        }

        /// <summary>QEMU's arguments for machine number n (1 and up) on this network: its card, with a MAC address of its own.</summary>
        public string[] MachineArgs(int n) => new[]
        {
            "-netdev", $"socket,id=net0,connect=127.0.0.1:{Port}",
            "-device", $"{Qemu.CardDevice(card)},netdev=net0,romfile=,mac=52:54:00:12:34:{n:x2}",
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
                    long number = Interlocked.Increment(ref frames);
                    if (loseEvery > 0 && number % loseEvery == 0)
                    {
                        Interlocked.Increment(ref lost);
                        continue;
                    }
                    if (mtu > 0 && length - 14 > mtu)
                    {
                        Interlocked.Increment(ref tooBig);
                        continue;
                    }
                    if (dhcp && DhcpAnswer(frame) is byte[] answer)
                    {
                        try { lock (from) from.Write(answer); }
                        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
                        continue;
                    }

                    // a switch: it learns which machine has each Ethernet address from what
                    // they send, and sends a frame only to its destination once it knows it
                    // (broadcasts, and addresses it hasn't seen yet, go to every machine)
                    if (length < 12) continue;
                    ulong source = MacAt(frame, 4 + 6);
                    ulong destination = MacAt(frame, 4);
                    NetworkStream[] others;
                    lock (machines)
                    {
                        where[source] = from;
                        bool broadcast = (frame[4] & 1) != 0;
                        others = !broadcast && where.TryGetValue(destination, out NetworkStream? known)
                            ? (known != from ? new[] { known } : Array.Empty<NetworkStream>())
                            : machines.Where(m => m != from).ToArray();
                    }
                    foreach (NetworkStream to in others)
                    {
                        // one writer at a time on each machine's connection
                        try { lock (to) to.Write(frame); }
                        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
                    }
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            lock (machines)
            {
                machines.Remove(from);
                foreach (ulong mac in where.Where(w => w.Value == from).Select(w => w.Key).ToList()) where.Remove(mac);
            }
        }

        /// <summary>The network's own address, which its DHCP answers come from and name as the router and DNS server.</summary>
        const uint ServerIp = 0x0A0003FE;                   // 10.0.3.254
        static readonly byte[] ServerMac = { 0x02, 0x00, 0x00, 0x00, 0x03, 0xFE };

        /// <summary>
        /// The network's answer, as QEMU's socket network sends a frame, to a DHCP Discover (an
        /// Offer) or Request (an Ack) in frame (also as it came: its length, then the frame);
        /// null for any other frame. Machine N (its Ethernet address ends in N) gets 10.0.3.N,
        /// a /24 network with 10.0.3.254 as the router and DNS server, and the MTU if there is one.
        /// </summary>
        byte[]? DhcpAnswer(byte[] frame)
        {
            ReadOnlySpan<byte> f = frame.AsSpan(4);
            if (f.Length < 14 + 20 + 8 + 240 || f[12] != 0x08 || f[13] != 0x00 || f[14 + 9] != 17) return null;   // IPv4, UDP
            int udp = 14 + (f[14] & 0x0F) * 4;
            int bootp = udp + 8;
            if (f.Length < bootp + 240 || BinaryPrimitives.ReadUInt16BigEndian(f[(udp + 2)..]) != 67 || f[bootp] != 1 ||
                BinaryPrimitives.ReadUInt32BigEndian(f[(bootp + 236)..]) != 0x63825363) return null;
            int type = 0;
            for (int p = bootp + 240; p + 1 < f.Length && f[p] != 255;)
            {
                if (f[p] == 0) { p++; continue; }
                if (f[p] == 53 && f[p + 1] >= 1 && p + 2 < f.Length) type = f[p + 2];
                p += 2 + f[p + 1];
            }
            if (type != 1 && type != 3) return null;

            // BOOTP: a reply, with the address, then the options
            var options = new List<byte> { 53, 1, (byte)(type == 1 ? 2 : 5) };
            void Address(byte code, uint value) { options.AddRange(new byte[] { code, 4, (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value }); }
            Address(54, ServerIp);
            Address(51, 86400);                             // a lease of a day
            Address(1, 0xFFFFFF00);
            Address(3, ServerIp);
            Address(6, ServerIp);
            if (mtu > 0) options.AddRange(new byte[] { 26, 2, (byte)(mtu >> 8), (byte)mtu });
            options.Add(255);
            var reply = new byte[240 + options.Count];
            reply[0] = 2;
            reply[1] = 1;
            reply[2] = 6;
            f.Slice(bootp + 4, 4).CopyTo(reply.AsSpan(4));       // the transaction
            f.Slice(bootp + 10, 2).CopyTo(reply.AsSpan(10));     // flags
            BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(16), 0x0A000300u + f[11]);
            BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(20), ServerIp);
            f.Slice(bootp + 28, 16).CopyTo(reply.AsSpan(28));    // the client's hardware address
            BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(236), 0x63825363);
            options.CopyTo(reply, 240);

            // in UDP (no checksum), in IP, to everyone, in Ethernet
            int total = 20 + 8 + reply.Length;
            var answer = new byte[4 + 14 + total];
            Span<byte> a = answer.AsSpan(4);
            BinaryPrimitives.WriteInt32BigEndian(answer, 14 + total);
            a[..6].Fill(0xFF);
            ServerMac.CopyTo(a[6..]);
            a[12] = 0x08;
            a[14] = 0x45;
            BinaryPrimitives.WriteUInt16BigEndian(a[16..], (ushort)total);
            a[22] = 64;
            a[23] = 17;
            BinaryPrimitives.WriteUInt32BigEndian(a[26..], ServerIp);
            BinaryPrimitives.WriteUInt32BigEndian(a[30..], 0xFFFFFFFF);
            uint sum = 0;
            for (int i = 0; i < 20; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(a[(14 + i)..]);
            while (sum > 0xFFFF) sum = (sum & 0xFFFF) + (sum >> 16);
            BinaryPrimitives.WriteUInt16BigEndian(a[24..], (ushort)~sum);
            BinaryPrimitives.WriteUInt16BigEndian(a[34..], 67);
            BinaryPrimitives.WriteUInt16BigEndian(a[36..], 68);
            BinaryPrimitives.WriteUInt16BigEndian(a[38..], (ushort)(8 + reply.Length));
            reply.CopyTo(a[42..]);
            return answer;
        }

        /// <summary>The 6-byte Ethernet address at an offset, as a number.</summary>
        static ulong MacAt(byte[] frame, int offset)
        {
            ulong mac = 0;
            for (int i = 0; i < 6; i++) mac = (mac << 8) | frame[offset + i];
            return mac;
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
