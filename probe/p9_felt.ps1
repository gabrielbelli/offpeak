# Compile and run probe/p9_felt.cs, with the compiler that is already inside
# Windows. No SDK, no NuGet, no network.
#
# WHY THIS WRAPPER EXISTS. p9_felt.cs produced numbers that are now quoted as
# fact in Config.cs, Model.cs, worker.ini.example and docs/CPU-LIMITS.md. A
# measurement nobody else can re-run is an assertion, not a measurement, and the
# probe beside it was only reproducible by somebody who already knew to reach for
# csc.exe by its full path. p1 to p7 shipped as .ps1 for exactly this reason.
#
# What it answers: whether idle priority alone is enough (the gentle probe: it said yes, and was wrong).
#
# Everything is built into a fresh temporary directory and deleted afterwards, so
# running this leaves nothing behind. It loads the machine hard while it runs;
# that is the point, and it is why it is not run automatically by anything.

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $here "p9_felt.cs"
if (-not (Test-Path $src)) { throw "$src is missing" }

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }

$out = Join-Path ([IO.Path]::GetTempPath()) ("idlegpu-probe9-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $out | Out-Null
try {
    $exe = Join-Path $out "p9_felt.exe"
    & $csc /nologo /target:exe /platform:anycpu /out:$exe $src
    if ($LASTEXITCODE -ne 0) { throw "compile failed" }
    & $exe @args
    exit $LASTEXITCODE
}
finally {
    Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
}
