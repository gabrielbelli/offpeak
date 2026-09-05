// One attribute, and it is load bearing.
//
// This program is compiled by the csc.exe inside Windows with no project file, so
// unless the target framework is stated here the assembly carries no
// TargetFrameworkAttribute and the runtime applies pre-4.7 quirks to it. Measured
// on spring, .NET Framework 4.8.9221, with a clean registry (SchUseStrongCrypto
// and SystemDefaultTlsVersions both absent, so this is not a machine
// misconfiguration):
//
//   no TargetFramework attribute    ServicePointManager.SecurityProtocol = Ssl3, Tls
//   with the attribute below        ServicePointManager.SecurityProtocol = SystemDefault
//
// Microsoft's guidance that 4.7 and later default to SystemDefault is true only
// for assemblies that declare their target. Without this line the default for
// every outbound call in this program would include SSL 3.0.
//
// It is belt and braces rather than the only measure: every TLS call site in this
// repository also names SslProtocols.Tls12 | SslProtocols.Tls13 explicitly, so
// deleting this line degrades the default rather than the actual connections.
// Both are here because either one alone is a single edit away from silence.

using System.Reflection;
using System.Runtime.Versioning;

[assembly: TargetFramework(".NETFramework,Version=v4.8")]
[assembly: AssemblyTitle("idlegpu")]
[assembly: AssemblyDescription("Lend a gaming PC's GPU to whatever you like, and give it straight back")]
