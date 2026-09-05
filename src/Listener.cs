// The socket, the handshake and the connection budget.
//
// TcpListener and SslStream, never HttpListener. See the measurement table at the
// top of src/Http.cs for why: everything except a loopback prefix on http.sys is
// administrator territory, and binding a certificate to a port is administrator,
// machine wide, and survives an uninstall.
//
// THE TLS CALL IS THE FOUR ARGUMENT OVERLOAD, ON PURPOSE.
//
//     AuthenticateAsServer(cert, false, SslProtocols.Tls12 | SslProtocols.Tls13, false)
//
// The one argument overload inherits ServicePointManager.SecurityProtocol, and
// this program is compiled by a bare csc.exe with no project file. Measured on
// spring: an assembly with no TargetFrameworkAttribute gets
// `ServicePointManager.SecurityProtocol default = Ssl3, Tls`, and the same
// assembly with the attribute gets `SystemDefault`. The registry was clean
// (SchUseStrongCrypto and SystemDefaultTlsVersions both absent), so that is a
// property of how this thing is built, not of the machine. src/AssemblyInfo.cs
// carries the attribute AND every call site names its protocols, because either
// alone is one edit away from silently re-enabling SSL 3.0.
//
// SslProtocols.None, which means "let the OS decide" and is the modern advice,
// THROWS on .NET Framework 4.8: "The specified value is not valid in the
// 'SslProtocolType' enumeration." So it is never passed. Tls13 is a real value
// here (net48 added it) and was measured negotiating on this Windows 11 box;
// Windows 10 falls back to Tls12, which is the floor.
//
// THE FIREWALL IS THE WALL, AND IT IS NOT ONE THIS PROGRAM CAN CLIMB. Measured:
// a non-elevated process binds 0.0.0.0 and gets LISTENING, and every connection
// from another machine on the same LAN failed, silently, with no rule created and
// no prompt shown. New-NetFirewallRule returned "Cannot connect to CIM server.
// Access denied" and netsh advfirewall returned "The requested operation requires
// elevation". Microsoft documents the rest: if a user without administrator
// rights is prompted, BLOCK rules are created whatever they click, and if
// notifications are off there is no prompt at all and the default block rule
// applies. That failure is permanent and silent. Hence Bind = 127.0.0.1 by
// default, and a README that names the one admin click LAN costs.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

namespace IdleGpu
{
    public class Listener : IDisposable
    {
        readonly Config _c;
        readonly Agent _agent;
        readonly Action<string> _log;
        readonly HttpLimits _limits;
        readonly List<IPNet> _allowed = new List<IPNet>();

        TcpListener _tcp;
        X509Certificate2 _cert;
        Thread _accept;
        volatile bool _stop;
        int _open;

        public string Pin { get; private set; }
        public int BoundPort { get; private set; }

        public Listener(Config c, Agent agent, Action<string> log)
        {
            _c = c; _agent = agent; _log = log;
            _limits = new HttpLimits();
            _limits.MaxRequestBytes = c.MaxRequestBytes;
            _limits.MaxHeaderCount = c.MaxHeaderCount;
            _limits.MaxHeaderLineBytes = c.MaxHeaderLineBytes;
            _limits.MaxRequestLineBytes = c.MaxHeaderLineBytes;
            foreach (string s in c.AllowedCidrs)
            {
                IPNet n = IPNet.Parse(s);
                if (n != null) _allowed.Add(n);
            }
        }

        /// Refuse to start rather than serve a LAN by accident.
        ///
        /// An operator who types Bind = 0.0.0.0 and forgets the token has made a
        /// mistake that no log line will make them notice, because the thing will
        /// appear to work. Failing at start is the only feedback that arrives
        /// before the exposure does.
        public string Validate()
        {
            if (_c.BindIsLoopback()) return null;
            if (string.IsNullOrEmpty(_c.EffectiveApiKey()))
                return "Bind is " + _c.Bind + ", which is not loopback, and no ApiKey or ApiKeyFile is set. " +
                       "Set one, or bind to 127.0.0.1 and reach the runner over an SSH tunnel.";
            if (_allowed.Count == 0)
                return "Bind is " + _c.Bind + ", which is not loopback, and AllowedCidrs is empty. " +
                       "List the networks allowed to reach this runner, for example 192.0.2.0/24.";
            return null;
        }

        public void Start()
        {
            string bad = Validate();
            if (bad != null) throw new InvalidOperationException(bad);

            int swept = Certs.SweepStaleKeyContainers(_c.CertKeyLedgerPath);
            if (swept > 0)
                _log("swept " + swept.ToString(CultureInfo.InvariantCulture) +
                     " stale TLS key container(s) left by a previous run that was killed rather than stopped");

            bool minted = Certs.EnsurePfx(_c.CertPath, _c.ServerName, _c.CertExtraSans, _c.CertYears);
            _cert = Certs.LoadServerCert(_c.CertPath, _c.CertKeyLedgerPath);
            Pin = Certs.Pin(_cert);
            if (minted) _log("minted a TLS certificate at " + _c.CertPath);
            _log("TLS fingerprint (sha256): " + Pin);

            IPAddress addr;
            if (!IPAddress.TryParse(_c.Bind, out addr))
            {
                if (_c.Bind.Trim().ToLowerInvariant() == "localhost") addr = IPAddress.Loopback;
                else throw new InvalidOperationException("Bind is not an IP address: " + _c.Bind);
            }
            _tcp = new TcpListener(addr, _c.Port);
            _tcp.Start();
            BoundPort = ((IPEndPoint)_tcp.LocalEndpoint).Port;
            _log("listening on https://" + _c.Bind + ":" + BoundPort.ToString(CultureInfo.InvariantCulture) + "/");
            if (!_c.BindIsLoopback())
                _log("this is a non-loopback bind: Windows Firewall must allow it once, " +
                     "and a dismissed prompt writes a permanent block rule");

            _accept = new Thread(AcceptLoop);
            _accept.IsBackground = true;
            _accept.Start();
        }

        void AcceptLoop()
        {
            while (!_stop)
            {
                TcpClient client = null;
                try { client = _tcp.AcceptTcpClient(); }
                catch (Exception) { if (_stop) return; Thread.Sleep(100); continue; }

                // The connection budget, checked before a thread is spent on the
                // socket. A slow or hostile client can exhaust THIS and nothing
                // else: the policy loop is on a different thread and shares no lock
                // with anything below, so the worst outcome of a flood is that the
                // runner stops answering questions while it carries on yielding
                // correctly. That is the right thing to lose first.
                if (Interlocked.Increment(ref _open) > _c.MaxConnections)
                {
                    Interlocked.Decrement(ref _open);
                    try { client.Close(); } catch (Exception) { }
                    continue;
                }
                TcpClient c2 = client;
                ThreadPool.QueueUserWorkItem(delegate(object ignored)
                {
                    try { Serve(c2); }
                    catch (Exception) { }
                    finally { Interlocked.Decrement(ref _open); }
                });
            }
        }

        void Serve(TcpClient client)
        {
            IPAddress peer = null;
            try
            {
                var ep = client.Client.RemoteEndPoint as IPEndPoint;
                if (ep != null) peer = ep.Address;
            }
            catch (Exception) { }

            using (client)
            {
                if (!PeerAllowed(peer))
                {
                    // Dropped before the handshake. An address that is not allowed
                    // to talk to this runner should not get to spend its CPU on an
                    // RSA operation either.
                    try { client.Close(); } catch (Exception) { }
                    return;
                }
                client.NoDelay = true;
                client.ReceiveTimeout = _c.RequestTimeoutMs;
                client.SendTimeout = _c.RequestTimeoutMs;

                using (var ssl = new SslStream(client.GetStream(), false))
                {
                    try
                    {
                        ssl.ReadTimeout = _c.HandshakeTimeoutMs;
                        ssl.WriteTimeout = _c.HandshakeTimeoutMs;
                        ssl.AuthenticateAsServer(_cert, false,
                            SslProtocols.Tls12 | SslProtocols.Tls13, false);
                    }
                    catch (Exception)
                    {
                        // A failed handshake is a port scanner, a browser that
                        // refused the self-signed certificate, or a client with the
                        // wrong pin. None of them is worth a log line at volume.
                        return;
                    }

                    ssl.ReadTimeout = _c.RequestTimeoutMs;
                    ssl.WriteTimeout = _c.RequestTimeoutMs;

                    var ctx = new ApiContext();
                    ctx.Agent = _agent;
                    ctx.Config = _c;
                    ctx.Peer = peer;
                    ctx.PeerIsLoopback = peer != null && IPAddress.IsLoopback(peer);

                    try
                    {
                        var reader = new ByteReader(ssl, 16 * 1024);
                        HttpRequest q = Http.Read(reader, _limits);
                        if (q == null) return;                 // clean close before a request
                        Api.Handle(q, ssl, ctx);
                    }
                    catch (HttpError he)
                    {
                        try { Http.WriteError(ssl, he.Status, he.Message); } catch (Exception) { }
                    }
                    catch (IOException) { /* the client went away mid-request */ }
                    catch (Exception ex)
                    {
                        _log("request failed: " + ex.GetType().Name + ": " + ex.Message);
                        try { Http.WriteError(ssl, 500, "the request could not be handled; see worker.log"); }
                        catch (Exception) { }
                    }
                }
            }
        }

        bool PeerAllowed(IPAddress peer)
        {
            if (peer == null) return false;
            if (IPAddress.IsLoopback(peer)) return true;
            foreach (IPNet n in _allowed) if (n.Contains(peer)) return true;
            return false;
        }

        public void Dispose()
        {
            _stop = true;
            try { if (_tcp != null) _tcp.Stop(); } catch (Exception) { }
            // Deterministic, and it matters. Reset() removes the transient CNG key
            // container that loading the certificate created; measured on spring,
            // the file was gone afterwards. Without it the container survives until
            // the finaliser runs, and a hard kill leaves it for ever, which is what
            // the ledger and the startup sweep exist to catch.
            try { if (_cert != null) _cert.Reset(); } catch (Exception) { }
            _cert = null;
        }
    }

    /// An address range from AllowedCidrs.
    ///
    /// IPv4 only, and it says so rather than pretending. An IPv6 entry is compared
    /// as an exact address, which is enough for the case that actually arises (one
    /// known machine on a home LAN) and is honest about the case that does not.
    public class IPNet
    {
        byte[] _net;
        int _bits;
        IPAddress _exact;

        public static IPNet Parse(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            s = s.Trim();
            var n = new IPNet();
            int slash = s.IndexOf('/');
            if (slash < 0)
            {
                IPAddress a;
                if (!IPAddress.TryParse(s, out a)) return null;
                n._exact = a;
                return n;
            }
            IPAddress net;
            int bits;
            if (!IPAddress.TryParse(s.Substring(0, slash), out net)) return null;
            if (!int.TryParse(s.Substring(slash + 1), NumberStyles.None,
                              CultureInfo.InvariantCulture, out bits)) return null;
            byte[] raw = net.GetAddressBytes();
            if (raw.Length != 4 || bits < 0 || bits > 32) return null;
            n._net = raw;
            n._bits = bits;
            return n;
        }

        public bool Contains(IPAddress a)
        {
            if (_exact != null) return _exact.Equals(a);
            byte[] raw = a.GetAddressBytes();
            if (raw.Length != 4) return false;
            int full = _bits / 8, rest = _bits % 8;
            for (int i = 0; i < full; i++) if (raw[i] != _net[i]) return false;
            if (rest == 0) return true;
            int mask = 0xFF << (8 - rest);
            return (raw[full] & mask) == (_net[full] & mask);
        }
    }

    /// Generates the bearer token, so nobody has to invent one.
    public static class ApiKeys
    {
        public static string Generate()
        {
            var b = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            var sb = new StringBuilder(64);
            foreach (byte x in b) sb.Append(x.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        /// Write one if there is not one already. Returns true when it created it.
        public static bool EnsureFile(string path)
        {
            if (string.IsNullOrEmpty(path) || File.Exists(path)) return false;
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, Generate() + Environment.NewLine, new UTF8Encoding(false));
            return true;
        }
    }
}
