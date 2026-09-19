// Talking to a runner, over TLS, with the fingerprint pinned.
//
// THIS IS THE ONLY CLIENT IN THE REPOSITORY AND IT NEVER TURNS VERIFICATION OFF.
// There is no public certificate authority in this design, so the trust root is a
// SHA-256 digest of the server's certificate that the operator carries from the
// runner to the client. The validation callback below compares that digest and
// returns false on any mismatch. It does not look at SslPolicyErrors and decide
// to forgive them; it does not have an "insecure" flag; there is nothing to set
// to true. A digest that does not match is a configuration problem, and the error
// message says which two values disagree so it can be fixed rather than bypassed.
//
// The name in the certificate is not the identity here, the key is. That is
// stronger than name checking, not weaker: a matching digest means this is the
// exact certificate the operator saw, whatever it calls itself and whatever
// address it was reached at. Which is what makes reaching the same runner as
// 127.0.0.1 through an SSH tunnel and as a hostname on the LAN work with one
// pinned value.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace OffPeak
{
    public class Response
    {
        public int Status;
        public string Reason = "";
        public readonly Dictionary<string, string> Headers =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = new byte[0];
        public string Text { get { return new UTF8Encoding(false).GetString(Body); } }
        public bool Ok { get { return Status >= 200 && Status < 300; } }
    }

    public class RunnerClient
    {
        public string Host = "127.0.0.1";
        public int Port = 47600;
        public string Pin = "";           // lower case hex sha256, colons tolerated
        public string ApiKey = "";
        public int TimeoutMs = 30000;

        public string SeenPin { get; private set; }

        public static string NormalisePin(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder();
            foreach (char c in s)
                if (c != ':' && c != ' ' && c != '-') sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        public Response Send(string method, string target, byte[] body, string contentType, string[] extra)
        {
            return SendCore(method, target, body, contentType, extra, null);
        }

        Response SendCore(string method, string target, byte[] body, string contentType,
                          string[] extra, string savePath)
        {
            string want = NormalisePin(Pin);
            if (want.Length != 64)
                throw new InvalidOperationException(
                    "no certificate fingerprint to pin. Run `offpeak fingerprint` on the runner's own machine, " +
                    "then pass --fingerprint or set CertFingerprint in worker.ini. " +
                    "Verification is never disabled to work around this.");

            using (var tcp = new TcpClient())
            {
                tcp.SendTimeout = TimeoutMs;
                tcp.ReceiveTimeout = TimeoutMs;
                tcp.Connect(Host, Port);
                using (var ssl = new SslStream(tcp.GetStream(), false,
                    delegate(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errors)
                    {
                        SeenPin = Certs.Pin(cert);
                        return string.Equals(SeenPin, want, StringComparison.Ordinal);
                    }))
                {
                    ssl.ReadTimeout = TimeoutMs;
                    ssl.WriteTimeout = TimeoutMs;
                    try
                    {
                        // Named protocols, never the overload that inherits
                        // ServicePointManager.SecurityProtocol. See src/Listener.cs
                        // for the measurement behind that.
                        ssl.AuthenticateAsClient(Host, null, SslProtocols.Tls12 | SslProtocols.Tls13, false);
                    }
                    catch (AuthenticationException)
                    {
                        throw new InvalidOperationException(
                            "the runner's certificate fingerprint is not the one configured.\n" +
                            "  expected " + want + "\n" +
                            "  offered  " + (SeenPin ?? "(no certificate)") + "\n" +
                            "If the runner minted a new certificate, copy the new fingerprint. " +
                            "If it did not, stop and find out who is answering on that address.");
                    }

                    var head = new StringBuilder();
                    head.Append(method).Append(' ').Append(target).Append(" HTTP/1.1\r\n");
                    head.Append("Host: ").Append(Host).Append(':')
                        .Append(Port.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                    head.Append("User-Agent: offpeak-cli\r\n");
                    head.Append("Accept: application/json\r\n");
                    head.Append("Connection: close\r\n");
                    if (!string.IsNullOrEmpty(ApiKey))
                        head.Append("Authorization: Bearer ").Append(ApiKey).Append("\r\n");
                    if (extra != null)
                        foreach (string e in extra) if (!string.IsNullOrEmpty(e)) head.Append(e).Append("\r\n");
                    // Always sent, even for a GET with no body, because the server
                    // refuses body-bearing methods without one and refuses chunked
                    // outright.
                    int len = body == null ? 0 : body.Length;
                    if (len > 0 || method == "POST" || method == "PUT" || method == "PATCH")
                    {
                        head.Append("Content-Type: ").Append(
                            string.IsNullOrEmpty(contentType) ? "application/json" : contentType).Append("\r\n");
                        head.Append("Content-Length: ")
                            .Append(len.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                    }
                    head.Append("\r\n");
                    byte[] hb = Encoding.ASCII.GetBytes(head.ToString());
                    ssl.Write(hb, 0, hb.Length);
                    if (len > 0) ssl.Write(body, 0, len);
                    ssl.Flush();

                    return ReadResponse(ssl, savePath);
                }
            }
        }

        /// Fetch an artefact straight to a file.
        ///
        /// The buffering version above is fine for a status document and wrong for
        /// a result: an image batch or a potfile can be larger than anything a CLI
        /// should be holding in memory, and the server streams it precisely so that
        /// nobody has to.
        public Response Download(string target, string savePath)
        {
            return SendCore("GET", target, null, null, null, savePath);
        }

        static Response ReadResponse(Stream s, string savePath)
        {
            var r = new ByteReader(s, 16 * 1024);
            var resp = new Response();
            string status = r.ReadLine(8192, "status line", 431);
            if (status == null) throw new IOException("the runner closed the connection without answering");
            string[] p = status.Split(new char[] { ' ' }, 3);
            if (p.Length < 2) throw new IOException("the runner sent a malformed status line");
            resp.Status = int.Parse(p[1], CultureInfo.InvariantCulture);
            resp.Reason = p.Length > 2 ? p[2] : "";
            while (true)
            {
                string h = r.ReadLine(8192, "header line", 431);
                if (h == null || h.Length == 0) break;
                int c = h.IndexOf(':');
                if (c <= 0) continue;
                resp.Headers[h.Substring(0, c)] = h.Substring(c + 1).Trim();
            }
            string cl;
            long n = 0;
            if (resp.Headers.TryGetValue("Content-Length", out cl))
                n = long.Parse(cl, CultureInfo.InvariantCulture);
            if (savePath == null || !resp.Ok)
            {
                // An error body is small and is wanted in memory so the CLI can
                // print it; a success body only goes to a file when one was asked
                // for.
                if (n > 0) resp.Body = r.ReadExact((int)Math.Min(n, 16 * 1024 * 1024));
                return resp;
            }
            using (var fs = new FileStream(savePath, FileMode.Create, FileAccess.Write))
            {
                long left = n;
                var buf = new byte[64 * 1024];
                while (left > 0)
                {
                    int want = (int)Math.Min(buf.Length, left);
                    byte[] got = r.ReadExact(want);
                    fs.Write(got, 0, got.Length);
                    left -= got.Length;
                }
            }
            resp.Headers["X-Saved-To"] = savePath;
            resp.Headers["X-Saved-Bytes"] = n.ToString(CultureInfo.InvariantCulture);
            return resp;
        }
    }
}
