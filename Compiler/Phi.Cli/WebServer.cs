using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Phi.Cli
{
    /// <summary>
    /// A small HTTP/1.0 server for network tests: it serves the files in a folder on
    /// 127.0.0.1, which a guest on QEMU's user network reaches as 10.0.2.2. Responses
    /// have no date or server name, so a test's output is the same every time.
    ///
    /// A secure server speaks HTTPS (TLS 1.3 only) with a self-signed certificate made when
    /// phi starts, for 10.0.2.2 and localhost.
    /// </summary>
    public sealed class WebServer : IDisposable
    {
        readonly TcpListener listener;
        readonly string root;
        readonly bool secure;
        readonly Task loop;

        public int Port { get; }

        public WebServer(string root, bool secure = false, int port = 0)
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            this.root = Path.GetFullPath(root);
            this.secure = secure;
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
                    NetworkStream network = client.GetStream();
                    network.ReadTimeout = 10000;
                    Stream stream = network;
                    SslStream? tls = null;
                    if (secure)
                    {
                        tls = new SslStream(network);
                        tls.AuthenticateAsServer(new SslServerAuthenticationOptions
                        {
                            ServerCertificate = Certificate.Value,
                            EnabledSslProtocols = SslProtocols.Tls13,
                        });
                        stream = tls;
                    }
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
                    tls?.ShutdownAsync().Wait();       // TLS's close_notify
                    client.Client.Shutdown(SocketShutdown.Send);
                    // let the guest read everything before the socket goes away
                    byte[] rest = new byte[256];
                    while (stream.Read(rest, 0, rest.Length) > 0) { }
                }
                catch (IOException) { }
                catch (SocketException) { }
                catch (AuthenticationException) { }
                catch (AggregateException) { }
            }
        }

        // the request line; the headers after it are read and ignored
        static string ReadRequest(Stream stream)
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

        static readonly Lazy<X509Certificate2> Certificate = new(() =>
        {
            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=phi test server", key, HashAlgorithmName.SHA256);
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Parse("10.0.2.2"));
            names.AddDnsName("localhost");
            request.CertificateExtensions.Add(names.Build());
            using X509Certificate2 made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            // Windows' TLS needs the key in a form it can load, which a PFX round trip gives it
            return new X509Certificate2(made.Export(X509ContentType.Pfx));
        });

        public void Dispose()
        {
            listener.Stop();
            try { loop.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        }
    }
}
