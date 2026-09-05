# Put a Python that can run controller.py on a machine that has none.
#
# The embeddable distribution, deliberately, and it is the ONE case where it is
# the right choice. services/chatterbox/provision.ps1 explains at length why the
# embeddable zip is a dead end for a real service: no ensurepip, no venv, site
# disabled by its ._pth, and python.org stopped shipping new embeddable builds for
# a branch once it goes security-fix-only. Every one of those objections is about
# INSTALLING PACKAGES, and this service installs none. controller.py imports
# argparse, math, sys, time, json, pathlib, hashlib and threading, all of which are
# in the standard library that ships inside the zip.
#
# What that buys is the thing this service exists for: on a bare Windows machine
# with no Python at all, `idlegpu service install echo` costs about 25 MB and a few
# seconds, and then the whole submit / schedule / run / yield / artefact path can be
# exercised end to end before anybody downloads six gigabytes of speech model. The
# expensive part of this system is not the part most likely to be wrong.
#
# Everything lands in $Root. Uninstall is deleting it.
#
#   idlegpu service install echo
#
# or by hand:
#
#   powershell -ExecutionPolicy Bypass -File provision.ps1 -Root C:\some\dir

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Root,

    # PINNED, not "latest". An unpinned URL in a provisioning script is a build
    # that works today and fails on somebody else's machine next month, and this
    # repository's whole posture elsewhere (uv.lock, hash-pinned wheels) would be
    # theatre if the interpreter underneath it floated. 3.12.10 is the newest
    # embeddable build python.org publishes for the 3.12 branch; when that changes,
    # change this line and say so in the commit.
    [string]$PythonVersion = '3.12.10',

    # Overridable so that an air-gapped or mirrored install is a flag rather than a
    # patch. Nothing else in this script reaches the network.
    [string]$BaseUrl = 'https://www.python.org/ftp/python'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # a progress bar over SSH is noise

$statusFile = Join-Path $Root 'provision.status'

function Set-Status([string]$text) {
    try {
        New-Item -ItemType Directory -Force -Path $Root | Out-Null
        Set-Content -Path $statusFile -Value $text -Encoding UTF8
    } catch { }
    Write-Host "==> $text"
}

try {
    New-Item -ItemType Directory -Force -Path $Root | Out-Null

    $pyDir = Join-Path $Root 'python'
    $py = Join-Path $pyDir 'python.exe'

    if (-not (Test-Path $py)) {
        $zipName = "python-$PythonVersion-embed-amd64.zip"
        $url = "$BaseUrl/$PythonVersion/$zipName"
        Set-Status "downloading CPython $PythonVersion (embeddable, about 11 MB)"
        $zip = Join-Path $Root $zipName
        Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $zip
        Expand-Archive -Path $zip -DestinationPath $pyDir -Force
        Remove-Item -Force $zip
    }
    if (-not (Test-Path $py)) { throw "python.exe missing after extraction at $py" }

    # The embeddable build ships a python312._pth that disables site and pins
    # sys.path to the zip. controller.py adds ../lib itself, so nothing here needs
    # site-packages; this only confirms the interpreter runs.
    $version = (& $py -c "import sys; print(sys.version.split()[0])")
    if ($LASTEXITCODE -ne 0) { throw "the extracted interpreter did not run" }
    Set-Status "CPython $version is in place"

    # Prove the controller imports before declaring the service installed. An
    # interpreter that runs and a controller that imports are different claims, and
    # the second is the one the agent is about to depend on.
    $controller = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'controller.py'
    if (Test-Path $controller) {
        & $py $controller --help | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "controller.py did not start under $py" }
    }

    Remove-Item -Force $statusFile -ErrorAction SilentlyContinue

    # THE READY MARKER, AND IT IS THE LAST THING THIS SCRIPT DOES. An install that
    # is interrupted at 80 per cent must read as "not installed" rather than as
    # "installed and broken": the first is fixed by running this again, the second
    # is a support question. `idlegpu service list` reads exactly this file.
    Set-Content -Path (Join-Path $Root '.installed') -Encoding UTF8 -Value @"
service   = echo
python    = $version
installed = $((Get-Date).ToUniversalTime().ToString('o'))
"@

    $size = (Get-ChildItem -Recurse -File $Root | Measure-Object -Property Length -Sum).Sum
    Write-Host ""
    Write-Host ("echo is installed at {0} ({1:N1} MB)" -f $Root, ($size / 1MB))
    Write-Host "worker.ini needs, in [service.echo]:"
    Write-Host "  Command   = $py"
}
catch {
    Set-Status ("FAILED: " + $_.Exception.Message)
    Write-Error $_
    exit 1
}
