// The TLS identity, minted on first run into the contained directory.
//
// THE TRUST ROOT IS A PINNED FINGERPRINT, NOT A CERTIFICATE AUTHORITY. A project
// somebody clones onto their own gaming PC cannot assume a public wildcard
// certificate exists, and asking them to obtain one before the thing will run is
// how "just turn verification off" gets typed. So the agent mints its own key
// pair, prints the SHA-256 of the certificate, and every client compares that
// digest byte for byte. Verification is never disabled anywhere in this
// repository: there is no verify=False, no ssl._create_unverified_context, no
// --insecure and no callback that returns true unconditionally. A digest that
// does not match is a configuration problem to fix, never a switch to flip.
//
// WHY SHA-256 AND NOT X509Certificate.GetCertHashString(). That method returns
// the SHA-1 thumbprint. SHA-1 is collision weak, and a pinned self signed
// certificate is precisely a case where an attacker gets to choose both sides of
// a collision. The pin here is SHA-256 over cert.RawData, computed in Pin().
//
// FOUR THINGS MEASURED ON spring ON 2026-09-05, .NET Framework 4.8.9221,
// Windows 11 10.0.26200, all of which shaped this file:
//
//  1. CertificateRequest.CreateSelfSigned works non-elevated and touches no
//     certificate store at all. The PowerShell route
//     (New-SelfSignedCertificate -CertStoreLocation Cert:\CurrentUser\My) also
//     works non-elevated, but Remove-Item from the store reported success and
//     left two files behind, one in Crypto\Keys and one in
//     SystemCertificates\My\Keys. That is exactly the pollution the containment
//     rule forbids, so the store is never used.
//
//  2. X509KeyStorageFlags.EphemeralKeySet, which is the obvious way to keep the
//     private key out of the filesystem entirely, DOES NOT WORK for an SslStream
//     server. AuthenticateAsServer throws Win32Exception "No credentials are
//     available in the security package". It is a Schannel limitation, tracked
//     upstream as dotnet/runtime#23749 and closed as external.
//
//     Worse, it produces a FALSE POSITIVE if tested carelessly: loading the same
//     certificate with UserKeySet first, in the same process, populates a
//     Schannel credential cache that the ephemeral attempt then reuses, and the
//     handshake succeeds. A test for this must load ephemerally first, in a fresh
//     process, or it passes for the wrong reason.
//
//  3. So the certificate is loaded UserKeySet and WITHOUT PersistKeySet, which
//     writes one transient CNG key container into
//     %APPDATA%\Microsoft\Crypto\Keys for the lifetime of the certificate object.
//     Redirecting it is not possible: setting the APPDATA environment variable
//     before launch was measured to change nothing, because
//     SpecialFolder.ApplicationData and CNG both follow the token's real profile.
//     THIS IS THE ONE FILE THIS PROGRAM WRITES OUTSIDE ITS OWN DIRECTORY. It is
//     documented in the README rather than hidden, and it is handled twice over:
//     Dispose() calls Reset(), which was measured to remove it, and the name of
//     every container this agent has ever created is recorded so a start after a
//     hard kill can sweep the one its predecessor left.
//
//     The sweep only ever deletes names this agent wrote down. Deleting anything
//     else in that directory would be destroying another application's keys.
//
//  4. PersistKeySet is never passed. Measured with it: the container survives the
//     process, permanently, one per load. That is a leak with no upper bound.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace IdleGpu
{
    public static class Certs
    {
        /// SHA-256 over the DER bytes, lower case hex. This is the string a client
        /// pins and the string the agent prints at provisioning time.
        public static string Pin(X509Certificate cert)
        {
            using (SHA256 h = SHA256.Create())
            {
                byte[] d = h.ComputeHash(cert.GetRawCertData());
                var sb = new StringBuilder(d.Length * 2);
                foreach (byte b in d) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// Human readable form of the same digest, colon separated, for printing
        /// into a README or an INI comment where somebody has to compare it by eye.
        public static string PinGrouped(string pin)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < pin.Length; i += 2)
            {
                if (i > 0) sb.Append(':');
                sb.Append(pin.Substring(i, 2));
            }
            return sb.ToString().ToUpperInvariant();
        }

        /// Create the PFX if it is not there. Returns true when it minted one.
        ///
        /// The certificate is written with no password. That is not a shortcut
        /// past a decision: a password stored beside the file it protects protects
        /// nothing, and inventing a key derivation ceremony around a file that
        /// already lives in a per-user directory would be theatre. The file is the
        /// key. Protect the contained directory the way an SSH private key is
        /// protected, and the README says so.
        public static bool EnsurePfx(string pfxPath, string serverName, string[] extraSans, int years)
        {
            if (File.Exists(pfxPath)) return false;
            string dir = Path.GetDirectoryName(pfxPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            using (RSA rsa = RSA.Create(2048))
            {
                var req = new CertificateRequest("CN=" + serverName, rsa,
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                req.CertificateExtensions.Add(new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
                req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, true));   // server auth

                var san = new SubjectAlternativeNameBuilder();
                var seen = new List<string>();
                AddName(san, seen, serverName);
                AddName(san, seen, "localhost");
                if (extraSans != null)
                    foreach (string s in extraSans) AddName(san, seen, s);
                req.CertificateExtensions.Add(san.Build());

                using (X509Certificate2 c = req.CreateSelfSigned(
                    DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(years)))
                {
                    File.WriteAllBytes(pfxPath, c.Export(X509ContentType.Pfx));
                }
            }
            return true;
        }

        static void AddName(SubjectAlternativeNameBuilder b, List<string> seen, string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            s = s.Trim();
            if (s.Length == 0 || seen.Contains(s)) return;
            seen.Add(s);
            IPAddress ip;
            if (IPAddress.TryParse(s, out ip)) b.AddIpAddress(ip);
            else b.AddDnsName(s);
        }

        /// Load for use as a TLS server credential.
        ///
        /// UserKeySet, and deliberately NOT PersistKeySet or EphemeralKeySet. See
        /// the measurements at the top of this file for why each of those is wrong
        /// here. The container name is recorded so a later start can sweep it if
        /// this process is killed before Dispose runs.
        public static X509Certificate2 LoadServerCert(string pfxPath, string keyLedgerPath)
        {
            var cert = new X509Certificate2(pfxPath, (string)null, X509KeyStorageFlags.UserKeySet);
            if (!cert.HasPrivateKey)
                throw new InvalidOperationException(
                    "the certificate at " + pfxPath + " has no private key; delete it and let the agent mint a new one");
            RecordKeyContainer(cert, keyLedgerPath);
            return cert;
        }

        static void RecordKeyContainer(X509Certificate2 cert, string ledger)
        {
            try
            {
                RSA k = cert.GetRSAPrivateKey();
                var cng = k as RSACng;
                if (cng == null) return;                 // CAPI key, no container file to track
                string name = cng.Key.UniqueName;
                if (string.IsNullOrEmpty(name)) return;
                string dir = Path.GetDirectoryName(ledger);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(ledger, name + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        /// Delete the CNG key containers this agent recorded on earlier runs.
        ///
        /// ONLY names in our own ledger. The CNG key directory belongs to every
        /// application the user runs, and a sweep that guessed by timestamp or by
        /// pattern would eventually delete somebody's signing key. Returns how
        /// many were removed, which the agent logs, because a number greater than
        /// zero means the previous run was killed rather than stopped.
        public static int SweepStaleKeyContainers(string ledger)
        {
            int removed = 0;
            try
            {
                if (!File.Exists(ledger)) return 0;
                string keyDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Microsoft", "Crypto", "Keys");
                foreach (string raw in File.ReadAllLines(ledger))
                {
                    string name = raw.Trim();
                    if (name.Length == 0) continue;
                    // Container names are hex and dashes and underscores. Anything
                    // else means the ledger was tampered with, and a delete driven
                    // by a tampered file is a delete of somebody else's key.
                    if (!LooksLikeContainerName(name)) continue;
                    string p = Path.Combine(keyDir, name);
                    try { if (File.Exists(p)) { File.Delete(p); removed++; } }
                    catch (Exception) { }
                }
                File.WriteAllText(ledger, "", new UTF8Encoding(false));
            }
            catch (Exception) { }
            return removed;
        }

        static bool LooksLikeContainerName(string s)
        {
            if (s.Length < 8 || s.Length > 200) return false;
            foreach (char c in s)
            {
                if (c >= '0' && c <= '9') continue;
                if (c >= 'a' && c <= 'f') continue;
                if (c >= 'A' && c <= 'F') continue;
                if (c == '-' || c == '_') continue;
                return false;
            }
            return true;
        }
    }
}
