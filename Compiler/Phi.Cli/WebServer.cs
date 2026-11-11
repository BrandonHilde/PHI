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
    /// A small HTTP server for network tests: it serves the files in a folder on 127.0.0.1,
    /// which a guest on QEMU's user network reaches as 10.0.2.2. Responses have no date or
    /// server name, so a test's output is the same every time. A Range header gets part of a file.
    /// /pattern/N is N bytes, each its place mod 251, /chunked/FILE is a file in chunks, and
    /// /moved/FILE redirects to it, for what a real server does that a folder can't show.
    /// A connection whose request says "Connection: keep-alive" (HTTP/1.1) is kept for the next
    /// request, until the client closes it or sends nothing for 10 seconds; any other is closed
    /// after its answer, which says so.
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
                    bool keep = true;
                    while (keep)
                    {
                        string request = ReadRequest(stream, out string headers);
                        string[] words = request.Split(' ', 3);
                        string path = words.Length >= 2 ? words[1] : "";
                        string version = words.Length >= 3 ? words[2].Trim() : "HTTP/1.0";
                        if (request.Length == 0) break;            // the client closed a kept connection
                        keep = version == "HTTP/1.1" && headers.Contains("Connection: keep-alive", StringComparison.OrdinalIgnoreCase);
                        string connection = keep ? "" : "Connection: close\r\n";

                        if (path.StartsWith("/moved/", StringComparison.Ordinal))
                        {
                            stream.Write(Encoding.ASCII.GetBytes($"{version} 302 Found\r\nLocation: /{path[7..]}\r\nContent-Length: 0\r\n{connection}\r\n"));
                        }
                        else
                        {
                            byte[] body;
                            string status;
                            bool chunked = path.StartsWith("/chunked/", StringComparison.Ordinal);
                            string? file = null;
                            bool found = true;
                            if (path.StartsWith("/pattern/", StringComparison.Ordinal) && int.TryParse(path[9..], out int size))
                            {
                                body = new byte[size];
                                for (int i = 0; i < size; i++) body[i] = (byte)(i % 251);
                            }
                            else if (words.Length >= 2 && words[0] == "GET" && (file = Find(chunked ? path[8..] : path)) != null)
                                body = File.ReadAllBytes(file);
                            else
                            {
                                found = false;
                                body = Encoding.ASCII.GetBytes("<html><body>not found</body></html>\n");
                            }
                            status = found ? "200 OK" : "404 Not Found";
                            string type = file == null || file.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ? "text/html" : "text/plain";
                            string extra = "";
                            int total = body.Length;
                            int from = found ? RangeStart(headers) : 0;
                            if (from > 0 && from < total)
                            {
                                status = "206 Partial Content";
                                extra = $"Content-Range: bytes {from}-{total - 1}/{total}\r\n";
                                body = body[from..];
                            }
                            if (chunked && found)
                            {
                                stream.Write(Encoding.ASCII.GetBytes($"{version} {status}\r\nContent-Type: {type}\r\nTransfer-Encoding: chunked\r\n{connection}\r\n"));
                                for (int at = 0; at < body.Length;)
                                {
                                    int n = Math.Min(7, body.Length - at);
                                    stream.Write(Encoding.ASCII.GetBytes($"{n:x}\r\n"));
                                    stream.Write(body, at, n);
                                    stream.Write(Encoding.ASCII.GetBytes("\r\n"));
                                    at += n;
                                }
                                stream.Write(Encoding.ASCII.GetBytes("0\r\n\r\n"));
                            }
                            else
                            {
                                stream.Write(Encoding.ASCII.GetBytes($"{version} {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\n{extra}{connection}\r\n"));
                                stream.Write(body);
                            }
                        }
                        stream.Flush();
                    }
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

        // where a Range: bytes=N- starts; 0 if there's none
        static int RangeStart(string headers)
        {
            foreach (string line in headers.Split("\r\n"))
                if (line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(line[13..].TrimEnd('-'), out int from))
                    return from;
            return 0;
        }

        // the request line, and the headers after it
        static string ReadRequest(Stream stream, out string headers)
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
            headers = end >= 0 ? all[end..] : "";
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

        /// <summary>The certificate the https server shows (self-signed, for 10.0.2.2 and localhost), as DER: a test trusts it as its root.</summary>
        public static byte[] CertificateDer => Certificate.Value.RawData;

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
