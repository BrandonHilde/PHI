using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Phi.Cli
{
    /// <summary>
    /// A small HTTP/1.0 server for network tests: it serves the files in a folder on
    /// 127.0.0.1, which a guest on QEMU's user network reaches as 10.0.2.2. Responses
    /// have no date or server name, so a test's output is the same every time.
    /// </summary>
    public sealed class WebServer : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly string root;
        readonly Task loop;

        public int Port { get; }

        public WebServer(string root)
        {
            this.root = Path.GetFullPath(root);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            loop = Task.Run(AcceptLoop);
        }

        async Task AcceptLoop()
        {
            while (true)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); }
                catch (Exception e) when (e is SocketException or ObjectDisposedException) { return; }
                _ = Task.Run(() => Serve(client));
            }
        }

        void Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    NetworkStream stream = client.GetStream();
                    stream.ReadTimeout = 10000;
                    string request = ReadRequest(stream);
                    string[] words = request.Split(' ', 3);

                    byte[] body;
                    string status;
                    string? file = words.Length >= 2 && words[0] == "GET" ? Find(words[1]) : null;
                    if (file != null)
                    {
                        status = "200 OK";
                        body = File.ReadAllBytes(file);
                    }
                    else
                    {
                        status = "404 Not Found";
                        body = Encoding.ASCII.GetBytes("<html><body>not found</body></html>\n");
                    }

                    string type = file != null && !file.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ? "text/plain" : "text/html";
                    string header = $"HTTP/1.0 {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                    stream.Write(Encoding.ASCII.GetBytes(header));
                    stream.Write(body);
                    stream.Flush();
                    client.Client.Shutdown(SocketShutdown.Send);
                    // let the guest read everything before the socket goes away
                    byte[] rest = new byte[256];
                    while (stream.Read(rest, 0, rest.Length) > 0) { }
                }
                catch (IOException) { }
                catch (SocketException) { }
            }
        }

        // the request line; the headers after it are read and ignored
        static string ReadRequest(NetworkStream stream)
        {
            var text = new StringBuilder();
            int b;
            while ((b = stream.ReadByte()) >= 0)
            {
                text.Append((char)b);
                if (text.Length >= 4 && text.ToString(text.Length - 4, 4) == "\r\n\r\n") break;
                if (text.Length > 8192) break;
            }
            string all = text.ToString();
            int end = all.IndexOf("\r\n", StringComparison.Ordinal);
            return end >= 0 ? all[..end] : all;
        }

        // a file in the folder for a URL path ('/' is index.html), or null
        string? Find(string urlPath)
        {
            string relative = Uri.UnescapeDataString(urlPath.Split('?')[0]).TrimStart('/');
            if (relative.Length == 0) relative = "index.html";
            string full = Path.GetFullPath(Path.Combine(root, relative));
            bool inside = full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            return inside && File.Exists(full) ? full : null;
        }

        public void Dispose()
        {
            listener.Stop();
            try { loop.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        }
    }
}
