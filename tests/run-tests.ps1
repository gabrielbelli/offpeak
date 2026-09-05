# Compile and run the policy tests.
#
# This links Model.cs, Config.cs, Policy.cs, Replay.cs, Jobs.cs, Http.cs,
# Install.cs and Tests.cs and NOTHING else. No Signals.cs, so no user32, no
# kernel32, no PDH, no registry and no service control manager; no Listener.cs,
# so no socket is opened and no certificate is minted. These tests need no GPU, no
# NVIDIA driver, no game, no console session and no network. They run on any
# Windows machine, including a headless one, in about a second.
#
# The compiler is the one inside Windows. No SDK, no NuGet, no network.

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }

$out = Join-Path ([IO.Path]::GetTempPath()) ("idlegpu-tests-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $out | Out-Null
try {
    $exe = Join-Path $out "tests.exe"
    $src = @(
        (Join-Path $root "src\Model.cs"),
        (Join-Path $root "src\Config.cs"),
        (Join-Path $root "src\Policy.cs"),
        (Join-Path $root "src\Replay.cs"),
        (Join-Path $root "src\Jobs.cs"),
        (Join-Path $root "src\Http.cs"),
        (Join-Path $root "src\Install.cs"),
        (Join-Path $here "Tests.cs")
    )
    & $csc /nologo /target:exe /platform:anycpu /warnaserror+ /out:$exe $src
    if ($LASTEXITCODE -ne 0) { throw "compile failed" }

    & $exe (Join-Path $here "fixtures")
    exit $LASTEXITCODE
}
finally {
    Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
}
