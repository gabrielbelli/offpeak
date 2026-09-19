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

$out = Join-Path ([IO.Path]::GetTempPath()) ("offpeak-tests-" + [Guid]::NewGuid().ToString("N"))
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
    $rc = $LASTEXITCODE

    # A SECOND BINARY, NOT A SECOND HARNESS. SharedTreeTests.cs links the same
    # sources and has its own Main, because two suites cannot share one entry
    # point. It is separate from Tests.cs so that work on the shared-install path
    # and work on the policy path cannot collide in one file - a merge conflict in
    # a test file is the cheapest possible way to lose a test nobody notices has
    # gone.
    $exe2 = Join-Path $out "sharedtree.exe"
    $src2 = @(
        (Join-Path $root "src\Model.cs"),
        (Join-Path $root "src\Config.cs"),
        (Join-Path $root "src\Policy.cs"),
        (Join-Path $root "src\Replay.cs"),
        (Join-Path $root "src\Jobs.cs"),
        (Join-Path $root "src\Http.cs"),
        (Join-Path $root "src\Install.cs"),
        (Join-Path $here "SharedTreeTests.cs")
    )
    & $csc /nologo /target:exe /platform:anycpu /warnaserror+ /out:$exe2 $src2
    if ($LASTEXITCODE -ne 0) { throw "compile failed (SharedTreeTests)" }

    Write-Output ""
    Write-Output "=== two services, one install directory ==="
    & $exe2 (Join-Path $here "fixtures")
    if ($LASTEXITCODE -ne 0) { $rc = $LASTEXITCODE }

    exit $rc
}
finally {
    Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
}
