# Put a working Chatterbox GPU runtime on a machine with no Python.
#
# WHAT THIS REPLACED, AND WHY. The first version of this script installed
# unpinned "torch torchaudio" from the cu124 index and then chatterbox-tts. Because
# chatterbox-tts pins torch==2.6.0, pip then resolved 2.6.0 FROM PYPI - which on
# Windows is the CPU-ONLY wheel - and silently replaced the GPU one that had just
# been downloaded. Its torch.cuda.is_available() gate caught the result, so it
# failed loudly rather than shipping a worker slower than the NAS it was meant to
# relieve. But it failed, every time.
#
# The same trap exists in uv and it was hit while writing this: a source mapping
# only applies to a DIRECT dependency, so with torch left transitive the lock
# resolved torch 2.6.0 from pypi.org. runtime/pyproject.toml therefore names torch
# and torchaudio explicitly. uv.lock is committed and hash-pinned, so what gets
# installed here is what was resolved and checked, not whatever resolves today.
#
# NOTHING IS INSTALLED ON THE MACHINE. No Python, no CUDA Toolkit, no admin, no
# reboot, no PATH edit, no registry write. Everything lands in one directory:
#
#   %LOCALAPPDATA%\idlegpu\runtime\
#     uv.exe        a single ~17 MB binary with no prerequisites of its own
#     python\       CPython 3.12, fetched by uv from python-build-standalone
#     .venv\        the 112-package locked environment
#     models\       HF_HOME. ~3 GiB of weights, content-addressed
#     cache\        uv's wheel cache
#
# Uninstall is: delete that directory.
#
# WHY NOT THE EMBEDDABLE PYTHON ZIP, which this script used to use. It has no
# ensurepip and no venv, its ._pth disables site so Lib\site-packages is not
# importable until you hand-edit it, and pip has to be bootstrapped from
# get-pip.py. It is also a dead end: python.org ships 3.12.10 as the newest
# embeddable build and 3.12.11 is a 404, because the 3.12 branch is
# security-fix-only. uv fetched CPython 3.12.14 - four patch releases newer -
# in 1.14 seconds, verified with a completely empty environment and no system
# Python reachable at all.
#
#   powershell -ExecutionPolicy Bypass -File provision.ps1

[CmdletBinding()]
param(
    # Mandatory, and passed in by `idlegpu service install chatterbox`. There is no
    # default on purpose: a provisioning script that guesses where to put six
    # gigabytes is a provisioning script that will one day put them somewhere the
    # uninstall does not reach.
    [Parameter(Mandatory = $true)]
    [string]$Root,
    # The project definition and lock. Defaults to the directory this script is in,
    # which is how it works both from a git checkout and from an installed copy.
    #
    # $PSScriptRoot, not $MyInvocation.MyCommand.Path. Inside a param() default the
    # latter is NULL, because $MyInvocation there describes the caller rather than
    # this script - so the default silently became empty and the whole install died
    # with "Cannot bind argument to parameter 'Path' because it is null". Measured
    # on spring, and $PSScriptRoot is empty there too under -File. Resolved in the
    # body instead, where both are populated.
    [string]$Project = '',
    [switch]$SkipModels
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # a progress bar over SSH is noise

$statusFile = Join-Path $Root 'provision.status'

function Set-Status([string]$text) {
    # The tray polls this file and shows it in its own colour. First run pulls
    # about 5.6 GiB; a grey icon for twenty minutes reads as broken and gets
    # killed at 4 GB, which is the worst possible moment to stop.
    try {
        New-Item -ItemType Directory -Force -Path $Root | Out-Null
        Set-Content -Path $statusFile -Value $text -Encoding UTF8
    } catch { }
    Write-Host "==> $text"
}

function Clear-Status {
    try { Remove-Item -Force $statusFile -ErrorAction SilentlyContinue } catch { }
}

if (-not $Project) { $Project = Split-Path -Parent $MyInvocation.MyCommand.Path }

try {
    New-Item -ItemType Directory -Force -Path $Root | Out-Null

    # Everything uv does stays inside $Root. HF_HOME matters most: the weights are
    # content-addressed, are shared across versions, and are the one part of the
    # payload that must survive a reinstall - so they must never land inside the
    # virtual environment where a rebuild would take them with it.
    #
    # DEFERS TO THE ENVIRONMENT WHEN THERE IS ONE. `idlegpu service install` sets
    # all of these before launching this script, and sets the SAME values again for
    # the controller at run time (Install.ContainedEnvironment). If this script
    # overrode them, provisioning would download weights to one path and the
    # controller would look for them at another, and the first job would silently
    # re-download three gigabytes. Setting them here is the fallback for running
    # this script by hand.
    function Default-Env([string]$name, [string]$value) {
        if (-not [Environment]::GetEnvironmentVariable($name)) {
            [Environment]::SetEnvironmentVariable($name, $value)
        }
    }
    Default-Env 'UV_PYTHON_INSTALL_DIR'  (Join-Path $Root 'python')
    Default-Env 'UV_PROJECT_ENVIRONMENT' (Join-Path $Root 'venv')
    Default-Env 'UV_CACHE_DIR'           (Join-Path $Root 'cache\uv')
    Default-Env 'HF_HOME'                (Join-Path $Root 'models\hf')
    # HF_HUB_CACHE is the current name; the deprecated HUGGINGFACE_HUB_CACHE
    # alone was not enough - diffusers still wrote into %USERPROFILE%\.cache.
    Default-Env 'HF_HUB_CACHE'           (Join-Path $Root 'models\hf\hub')
    Default-Env 'DIFFUSERS_CACHE'        (Join-Path $Root 'models\hf\hub')
    Default-Env 'TORCH_HOME'             (Join-Path $Root 'models\torch')
    # MEASURED: without UV_PYTHON_BIN_DIR, `uv python install` still drops a shim
    # into %USERPROFILE%\.local\bin, outside the contained directory and outliving
    # an uninstall. The agent sets this too; this is the by-hand fallback.
    Default-Env 'UV_PYTHON_BIN_DIR'      (Join-Path $Root 'bin')
    Default-Env 'UV_TOOL_DIR'            (Join-Path $Root 'tools')
    Default-Env 'UV_TOOL_BIN_DIR'        (Join-Path $Root 'bin')
    $env:UV_NO_CONFIG = '1'
    $env:UV_PYTHON_DOWNLOADS = 'automatic'
    Write-Host ("weights will go to HF_HOME = " + $env:HF_HOME)

    # ---------------------------------------------------------------- uv ---
    $uv = Join-Path $Root 'uv.exe'
    if (-not (Test-Path $uv)) {
        Set-Status 'downloading uv (17 MB)'
        $zip = Join-Path $Root 'uv.zip'
        Invoke-WebRequest -UseBasicParsing `
            -Uri 'https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-pc-windows-msvc.zip' `
            -OutFile $zip
        Expand-Archive -Path $zip -DestinationPath $Root -Force
        Remove-Item -Force $zip
    }
    if (-not (Test-Path $uv)) { throw "uv.exe missing after download" }
    Set-Status ("uv " + (& $uv --version))

    # ------------------------------------------------------ interpreter ---
    # No system Python is consulted, and none is required. uv fetches a
    # standalone CPython build into $Root\python.
    Set-Status 'fetching CPython 3.12 (24 MB)'
    & $uv python install 3.12
    if ($LASTEXITCODE -ne 0) { throw "uv python install failed" }

    # ---------------------------------------------------------- packages ---
    # --frozen means: use uv.lock exactly, do not re-resolve. Offline it still
    # starts from whatever is already in .venv. When nothing has changed this is
    # a sub-second no-op, which is what makes "git pull && uv sync" a viable
    # update path instead of reshipping a 4 GB binary.
    Set-Status 'installing torch and CUDA (2.5 GB) - this is the long one'
    & $uv sync --project $Project --frozen --no-dev
    if ($LASTEXITCODE -ne 0) { throw "uv sync failed" }

    $py = Join-Path $env:UV_PROJECT_ENVIRONMENT 'Scripts\python.exe'
    if (-not (Test-Path $py)) { throw "venv python missing at $py" }

    # ------------------------------------------------------------- gate ---
    # The single most important check in this file. Installing the CPU-only wheel
    # is the most common way this setup fails and it fails SILENTLY: everything
    # imports, everything runs, and the worker is slower than the NAS it was
    # supposed to relieve. Fail here, loudly, rather than at benchmark time.
    Set-Status 'checking CUDA'
    $probe = @'
import sys, torch
print("torch          ", torch.__version__)
print("cuda available ", torch.cuda.is_available())
print("cuda version   ", torch.version.cuda)
if not torch.cuda.is_available():
    sys.exit("FATAL: torch cannot see the GPU. This is almost always the CPU-only "
             "wheel from PyPI rather than the cu126 wheel from download.pytorch.org. "
             "Check that uv.lock pins torch==2.6.0+cu126.")
if "+cu" not in torch.__version__:
    sys.exit("FATAL: %s is not a CUDA build." % torch.__version__)
print("device         ", torch.cuda.get_device_name(0))
free, total = torch.cuda.mem_get_info()
print("vram free/total %.0f / %.0f MiB" % (free/2**20, total/2**20))
'@
    $probe | & $py -
    if ($LASTEXITCODE -ne 0) { throw "CUDA check failed" }

    # ----------------------------------------------------------- models ---
    if (-not $SkipModels) {
        # ~3.0 GiB, into $Root\models via HF_HOME. Done here rather than on first
        # job so that the first job is not a twenty-minute download during
        # somebody's idle window.
        Set-Status 'downloading Chatterbox weights (3.0 GB)'
        $fetch = @'
import os
from chatterbox.mtl_tts import ChatterboxMultilingualTTS
print("HF_HOME =", os.environ.get("HF_HOME"))
ChatterboxMultilingualTTS.from_pretrained(device="cpu")
print("weights present")
'@
        $fetch | & $py -
        if ($LASTEXITCODE -ne 0) { throw "model download failed" }
    }

    # ---------------------------------------------------------- reclaim ---
    # THE WHEEL CACHE IS DEAD WEIGHT ONCE THE VENV EXISTS, and it is not small.
    # Measured on spring after a successful install:
    #
    #   venv    5.01 GB     cache   4.96 GB     models  2.99 GB     python 0.06 GB
    #
    # 4.96 GB of downloaded wheels, on somebody's games drive, that will never be
    # read again unless they reinstall. uv copies rather than hardlinks on this
    # filesystem, which is why both numbers are full size and why deleting one
    # does not damage the other - and that is asserted below rather than assumed,
    # because if uv ever DID hardlink here, this step would silently gut the
    # environment and the failure would appear at the first job instead.
    Set-Status 'reclaiming the wheel cache'
    & $uv cache clean
    & $py -c "import torch, chatterbox; print('post-clean import ok', torch.__version__)"
    if ($LASTEXITCODE -ne 0) {
        throw "the environment stopped importing after the cache was cleaned; " +
              "uv must be hardlinking into the venv on this filesystem. " +
              "Reinstall, and remove the cache clean above."
    }

    Clear-Status

    # THE READY MARKER, AND IT IS THE LAST THING THIS SCRIPT DOES. Six gigabytes is
    # twenty minutes of download; an install interrupted at fifteen must read as
    # "not installed" rather than "installed and broken". The first is fixed by
    # running this again, the second is a support question. `idlegpu service list`
    # reads exactly this file and nothing else.
    $torchVersion = (& $py -c "import torch; print(torch.__version__)")
    Set-Content -Path (Join-Path $Root '.installed') -Encoding UTF8 -Value @"
service   = chatterbox
python    = $py
torch     = $torchVersion
installed = $((Get-Date).ToUniversalTime().ToString('o'))
"@

    $size = (Get-ChildItem -Recurse -File $Root | Measure-Object -Property Length -Sum).Sum
    Write-Host ""
    Write-Host ("chatterbox is installed at {0} ({1:N2} GB)" -f $Root, ($size / 1GB))
    Write-Host "worker.ini needs, in [service.chatterbox]:"
    Write-Host "  Command   = $py"
}
catch {
    Set-Status ("FAILED: " + $_.Exception.Message)
    Write-Error $_
    exit 1
}
