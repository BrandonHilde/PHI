using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Phi.Cli
{
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
