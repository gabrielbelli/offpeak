# Build idlegpu.
#
# THIS IS THE WHOLE BUILD. No SDK to install, no NuGet restore, no network access
# and no build host: csc.exe ships inside Windows itself. Measured present on the
# test machine at
#   %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
# alongside .NET Framework 4.8.9221. Every assembly referenced below sits in the
# same directory. That is the argument for this packaging made executable: the
# runtime dependency does not exist, because the runtime is part of the operating
# system.
#
# THE OUTPUT IS TWO FILES FROM ONE SET OF SOURCES, and the reason is a defect
# measured on spring rather than a preference.
#
#   idlegpu.exe    console subsystem. The CLI, and the agent in every headless
#                  mode. This is the one you type.
#   idlegpuw.exe   Windows subsystem. The tray, and the only thing autostart
#                  points at. Identical code; it just has no console.
#
# python.exe and pythonw.exe are the same pair for the same reason, which is worth
# knowing because it means the convention is already familiar.
#
# WHAT GOES WRONG WITH ONE winexe. A Windows-subsystem binary is not waited for by
# a shell, because the shell has no console to give it. Measured over SSH on
# spring: `idlegpu service list` printed NOTHING, set no $LASTEXITCODE at all, and
# then dumped its output into the middle of the NEXT command two lines later.
# Redirecting to a file produced a zero byte file. AttachConsole and reopening the
# standard handles does not fix it, because the problem is the shell returning
# before the process has written anything, not where the writes go. Since SSH is
# how this thing is driven on a headless or remote machine, that made every client
# command unusable in the environment it matters most in.
#
#   powershell -ExecutionPolicy Bypass -File build.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# $env:WINDIR rather than C:\Windows. Windows is not always on C.
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    throw @"
csc.exe not found at $csc

This build needs the C# compiler that ships inside the .NET Framework, which is
part of Windows. If it is missing, the Framework 4.x feature is turned off:
  Settings > System > Optional features > More Windows features > .NET Framework 4.8
"@
}

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null

# System.Security.dll is what carries CertificateRequest and
# SubjectAlternativeNameBuilder on .NET Framework 4.7.2 and later. Without it the
# self-signed certificate cannot be minted without touching a certificate store,
# and touching a store leaves files behind that an uninstall cannot remove.
$refs = @(
  'System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll',
  'System.ServiceProcess.dll','System.Configuration.Install.dll','System.Security.dll'
) | ForEach-Object { "/r:$_" }

$src = Get-ChildItem (Join-Path $root 'src\*.cs') | ForEach-Object { $_.FullName }

foreach ($build in @(
    @{ name = 'idlegpu.exe';  target = 'exe' },
    @{ name = 'idlegpuw.exe'; target = 'winexe' }
)) {
    $out = Join-Path $dist $build.name
    & $csc /nologo "/target:$($build.target)" /platform:x64 /optimize+ /warn:4 `
           "/out:$out" $refs $src
    if ($LASTEXITCODE -ne 0) { throw "compile failed with $LASTEXITCODE" }
    Write-Output ("built {0} ({1:N0} bytes, {2})" -f $out, (Get-Item $out).Length, $build.target)
}
