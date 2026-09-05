// HTTP/1.1 framing, hand written, with no sockets and no Windows in it.
//
// WHY THIS EXISTS AT ALL, given that .NET ships HttpListener. HttpListener sits
// on http.sys, and http.sys is administrator territory. Measured on spring under
// a genuinely non-elevated token (runas /trustlevel:0x20000, so
// IsInRole(Administrator) reported False, which is the token the agent always
// gets because it starts from an HKCU Run key):
//
//   http://localhost:P/   STARTED       https://localhost:P/  STARTED, and useless
//   http://+:P/           Access is denied
//   http://*:P/           Access is denied
//   https://+:P/          Access is denied
//   TcpListener 0.0.0.0:P STARTED, accepted a connection, no reservation needed
//
// The https://localhost row is the trap: Start() succeeds because http.sys defers
// the certificate lookup to the first connection, and then every connection is
// reset, because binding a certificate to a port is `netsh http add sslcert`,
// which is administrator, machine wide, and survives an uninstall. On spring
// `netsh http show sslcert` lists nothing to piggyback on. So a smoke test that
// only asserts Start() did not throw passes while the product is broken.
//
// TcpListener + SslStream needs no administrator and no reservation, and the
// price of that is this file: the framing has to be written by hand.
//
// WHAT IS DELIBERATELY NOT IMPLEMENTED, and why refusing beats guessing. A hand
// rolled server on a socket that may be exposed to a LAN must not pretend to be a
// full HTTP stack. Each of these is answered with a status code rather than an
// approximation:
//
//   Transfer-Encoding: chunked   501. Clients send it for streamed bodies without
//                                being asked. Half implementing it is a request
//                                smuggling bug.
//   Expect: 100-continue         417. curl sends this automatically for bodies
//                                over 1 KiB, and a server that ignores it hangs
//                                the client for its whole timeout.
//   keep alive and pipelining    every response says Connection: close. The cost
//                                is one TLS handshake per request, which is a
//                                socket, not GPU time.
//   duplicate Content-Length     400, never "take the first one".
//   bare LF line endings         400. CRLF is required.
//
// Every limit is a caller supplied number rather than a constant here, because a
// listener that may end up on somebody's LAN needs its slowloris knobs in the
// config file where a reader can find them, not buried in a parser.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace IdleGpu
{
    /// A refusal with a status code attached. Thrown by the parser, caught by the
    /// listener, turned into a response. Message text is safe to send back: it is
    /// written here and never contains anything the client supplied.
    public class HttpError : Exception
    {
        public readonly int Status;
        public HttpError(int status, string message) : base(message) { Status = status; }
    }

    public class HttpLimits
    {
        public int MaxRequestLineBytes = 8192;
        public int MaxHeaderLineBytes = 8192;
        public int MaxHeaderCount = 64;
        public int MaxRequestBytes = 8 * 1024 * 1024;
    }

    public class HttpRequest
    {
        public string Method = "";
        public string Target = "";     // as sent, path plus query
        public string Path = "";       // percent decoded, query removed
        public string Query = "";      // raw, without the '?'
        public string Version = "";
        public readonly Dictionary<string, string> Headers =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = new byte[0];

        public string Header(string name)
        {
            string v;
            return Headers.TryGetValue(name, out v) ? v : null;
        }

        public string BodyText()
        {
            return new UTF8Encoding(false).GetString(Body);
        }

        /// One query parameter, percent decoded. Absent returns null; present but
        /// empty returns "".
        public string Param(string name)
        {
            if (Query.Length == 0) return null;
            foreach (string pair in Query.Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                string k = eq < 0 ? pair : pair.Substring(0, eq);
                if (!string.Equals(Http.PercentDecode(k), name, StringComparison.Ordinal)) continue;
                return eq < 0 ? "" : Http.PercentDecode(pair.Substring(eq + 1));
            }
            return null;
        }

        /// The path split on '/', with empty segments dropped. Percent decoding
        /// happens before the split, so a client cannot smuggle a separator in as
        /// %2F and reach a different route than the one the router matched.
        public string[] Segments()
        {
            var outp = new List<string>();
            foreach (string seg in Path.Split('/'))
                if (seg.Length > 0) outp.Add(seg);
            return outp.ToArray();
        }
    }

    /// Reads lines and exact byte counts off a stream without over-reading.
    ///
    /// WHY NOT StreamReader. StreamReader buffers ahead into a char decoder, so
    /// the bytes of the body would be eaten while parsing the headers and there
    /// would be no way to get them back. This buffers bytes and hands the leftover
    /// to the body read, which is the whole reason it exists.
    public class ByteReader
    {
        readonly Stream _s;
        readonly byte[] _buf;
        int _len, _pos;

        public ByteReader(Stream s, int bufferBytes)
        {
            _s = s;
            _buf = new byte[bufferBytes < 1024 ? 1024 : bufferBytes];
        }

        int NextByte()
        {
            if (_pos >= _len)
            {
                _len = _s.Read(_buf, 0, _buf.Length);
                _pos = 0;
                if (_len <= 0) return -1;
            }
            return _buf[_pos++];
        }

        /// One CRLF terminated line, returned without the CRLF. A bare LF is a
        /// protocol error rather than a thing to be lenient about: leniency here
        /// is how two intermediaries end up disagreeing about where a request
        /// ends.
        /// `tooLongStatus` rather than a fixed 431.
        ///
        /// THE DEFECT THIS PREVENTS is a wrong diagnosis rather than a wrong
        /// outcome, which is worse: every overlong line used to answer 431 Request
        /// Header Fields Too Large, including an overlong REQUEST line. A client
        /// that sends a 40 KB URL and is told its headers are too big goes and
        /// trims its headers. 414 URI Too Long points at the thing that was
        /// actually too long.
        public string ReadLine(int maxBytes, string what, int tooLongStatus)
        {
            var sb = new StringBuilder();
            bool sawCr = false;
            while (true)
            {
                int b = NextByte();
                if (b < 0)
                {
                    if (sb.Length == 0 && !sawCr) return null;   // clean close
                    throw new HttpError(400, "connection closed inside a " + what);
                }
                if (sawCr)
                {
                    if (b == '\n') return sb.ToString();
                    throw new HttpError(400, "carriage return not followed by a line feed");
                }
                if (b == '\r') { sawCr = true; continue; }
                if (b == '\n') throw new HttpError(400, "line feed with no carriage return");
                if (sb.Length >= maxBytes)
                    throw new HttpError(tooLongStatus, what + " is longer than the configured limit");
                sb.Append((char)b);
            }
        }

        public byte[] ReadExact(int n)
        {
            var outp = new byte[n];
            int got = 0;
            while (got < n)
            {
                if (_pos < _len)
                {
                    int take = Math.Min(_len - _pos, n - got);
                    Buffer.BlockCopy(_buf, _pos, outp, got, take);
                    _pos += take; got += take;
                    continue;
                }
                int r = _s.Read(outp, got, n - got);
                if (r <= 0) throw new HttpError(400, "connection closed inside the request body");
                got += r;
            }
            return outp;
        }
    }

    public static class Http
    {
        /// Parse one request. Returns null when the peer closed cleanly before
        /// sending anything, which is what a port scanner and a TLS probe both do.
        public static HttpRequest Read(ByteReader r, HttpLimits lim)
        {
            string line = r.ReadLine(lim.MaxRequestLineBytes, "request line", 414);
            if (line == null) return null;
            // Tolerate the one lenient thing RFC 9112 actually asks for: leading
            // empty lines before a request line.
            int guard = 0;
            while (line.Length == 0)
            {
                if (++guard > 4) throw new HttpError(400, "too many empty lines before the request line");
                line = r.ReadLine(lim.MaxRequestLineBytes, "request line", 414);
                if (line == null) return null;
            }

            string[] parts = line.Split(' ');
            if (parts.Length != 3) throw new HttpError(400, "malformed request line");
            var q = new HttpRequest();
            q.Method = parts[0];
            q.Target = parts[1];
            q.Version = parts[2];
            if (q.Method.Length == 0 || !IsToken(q.Method))
                throw new HttpError(400, "malformed method");
            if (q.Version != "HTTP/1.1" && q.Version != "HTTP/1.0")
                throw new HttpError(505, "only HTTP/1.0 and HTTP/1.1 are understood");

            int qm = q.Target.IndexOf('?');
            string rawPath = qm < 0 ? q.Target : q.Target.Substring(0, qm);
            q.Query = qm < 0 ? "" : q.Target.Substring(qm + 1);
            q.Path = PercentDecode(rawPath);
            if (!q.Path.StartsWith("/")) throw new HttpError(400, "the request target must be an absolute path");

            int count = 0;
            while (true)
            {
                string h = r.ReadLine(lim.MaxHeaderLineBytes, "header line", 431);
                if (h == null) throw new HttpError(400, "connection closed inside the header block");
                if (h.Length == 0) break;
                if (h[0] == ' ' || h[0] == '\t')
                    throw new HttpError(400, "obsolete line folding in a header");
                if (++count > lim.MaxHeaderCount)
                    throw new HttpError(431, "more headers than the configured limit");
                int c = h.IndexOf(':');
                if (c <= 0) throw new HttpError(400, "malformed header line");
                string name = h.Substring(0, c);
                if (!IsToken(name)) throw new HttpError(400, "malformed header name");
                string value = h.Substring(c + 1).Trim(' ', '\t');
                if (q.Headers.ContainsKey(name))
                {
                    // Never "take the first one". A duplicate Content-Length is
                    // the classic request smuggling primitive, and for anything
                    // else a duplicate means the client and this server already
                    // disagree about the message.
                    throw new HttpError(400, "duplicate header: " + (IsToken(name) ? name : "?"));
                }
                q.Headers[name] = value;
            }

            if (q.Header("Transfer-Encoding") != null)
                throw new HttpError(501, "chunked and other transfer codings are not implemented; send Content-Length");
            if (q.Header("Expect") != null)
                throw new HttpError(417, "Expect is not implemented; send the body without waiting");

            string cl = q.Header("Content-Length");
            if (cl == null)
            {
                if (q.Method == "POST" || q.Method == "PUT" || q.Method == "PATCH")
                    throw new HttpError(411, "a body-bearing method needs a Content-Length");
                return q;
            }
            long n;
            if (!long.TryParse(cl, NumberStyles.None, CultureInfo.InvariantCulture, out n) || n < 0)
                throw new HttpError(400, "malformed Content-Length");
            if (n > lim.MaxRequestBytes)
                throw new HttpError(413, "the body is larger than MaxRequestBytes");
            if (n > 0) q.Body = r.ReadExact((int)n);
            return q;
        }

        static bool IsToken(string s)
        {
            if (s.Length == 0) return false;
            foreach (char ch in s)
            {
                if (ch >= 'a' && ch <= 'z') continue;
                if (ch >= 'A' && ch <= 'Z') continue;
                if (ch >= '0' && ch <= '9') continue;
                if ("!#$%&'*+-.^_`|~".IndexOf(ch) >= 0) continue;
                return false;
            }
            return true;
        }

        public static string PercentDecode(string s)
        {
            if (s.IndexOf('%') < 0) return s;
            var bytes = new List<byte>();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '%' && i + 2 < s.Length)
                {
                    int hi = Hex(s[i + 1]), lo = Hex(s[i + 2]);
                    if (hi >= 0 && lo >= 0) { bytes.Add((byte)(hi * 16 + lo)); i += 2; continue; }
                }
                // Not '+' to space: this is a path and a query, not a form body,
                // and a voice or file name with a plus in it must survive.
                foreach (byte b in Encoding.UTF8.GetBytes(s[i].ToString())) bytes.Add(b);
            }
            return new UTF8Encoding(false).GetString(bytes.ToArray());
        }

        static int Hex(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        /// The response head. Connection: close on every single response, which is
        /// what lets this file stay this short: there is no second message to
        /// frame on this socket, so there is nothing to get wrong about where the
        /// first one ended.
        public static byte[] Head(int status, string contentType, long length, string[] extra)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture))
              .Append(' ').Append(Reason(status)).Append("\r\n");
            sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("Connection: close\r\n");
            sb.Append("Cache-Control: no-store\r\n");
            sb.Append("X-Content-Type-Options: nosniff\r\n");
            if (extra != null)
                foreach (string e in extra)
                    if (!string.IsNullOrEmpty(e)) sb.Append(e).Append("\r\n");
            sb.Append("\r\n");
            return Encoding.ASCII.GetBytes(sb.ToString());
        }

        public static void Write(Stream s, int status, string contentType, byte[] body, string[] extra)
        {
            bool headOnly = body == null;
            byte[] b = headOnly ? new byte[0] : body;
            byte[] head = Head(status, contentType, b.Length, extra);
            s.Write(head, 0, head.Length);
            if (b.Length > 0) s.Write(b, 0, b.Length);
            s.Flush();
        }

        /// A response head with no body, for HEAD. Content-Length describes the
        /// body a GET would have returned, which is the whole reason a client
        /// sends HEAD in the first place.
        public static void WriteHeadOnly(Stream s, int status, string contentType, long length, string[] extra)
        {
            byte[] head = Head(status, contentType, length, extra);
            s.Write(head, 0, head.Length);
            s.Flush();
        }

        public static void WriteText(Stream s, int status, string contentType, string text)
        {
            Write(s, status, contentType, new UTF8Encoding(false).GetBytes(text), null);
        }

        /// An error as JSON, so a client never has to guess whether a failure came
        /// from this server or from something it is proxying.
        public static void WriteError(Stream s, int status, string message)
        {
            WriteText(s, status, "application/json",
                Json.Obj(Json.P("error", Json.Esc(message)),
                         Json.P("status", Json.Num(status))) + "\n");
        }

        /// A file, streamed with a real Content-Length and never buffered whole.
        /// A multi gigabyte artefact must not become a multi gigabyte allocation
        /// in an agent whose whole job is to stay out of the way.
        public static void WriteFile(Stream s, string contentType, Stream body, long length, string[] extra)
        {
            byte[] head = Head(200, contentType, length, extra);
            s.Write(head, 0, head.Length);
            var buf = new byte[64 * 1024];
            long left = length;
            while (left > 0)
            {
                int want = (int)Math.Min(buf.Length, left);
                int n = body.Read(buf, 0, want);
                if (n <= 0) break;
                s.Write(buf, 0, n);
                left -= n;
            }
            s.Flush();
        }

        public static string Reason(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 201: return "Created";
                case 202: return "Accepted";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 409: return "Conflict";
                case 411: return "Length Required";
                case 413: return "Content Too Large";
                case 417: return "Expectation Failed";
                case 429: return "Too Many Requests";
                case 431: return "Request Header Fields Too Large";
                case 500: return "Internal Server Error";
                case 501: return "Not Implemented";
                case 503: return "Service Unavailable";
                case 505: return "HTTP Version Not Supported";
                default: return "Status";
            }
        }
    }
}
