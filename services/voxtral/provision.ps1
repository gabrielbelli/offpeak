# Put a working Voxtral-4B-TTS int4 runtime on a machine with no Python.
#
# A SECOND TORCH, ON PURPOSE, AND IT IS THE EXPENSIVE FACT ABOUT THIS SERVICE.
# chatterbox-tts 0.1.7 hard-pins torch==2.6.0; the int4 path here needs 2.14.0
# for torchao's TILE_PACKED_TO_4D packing with the HQQ parameter search. One
# virtual environment cannot hold both, and src/Install.cs:325 derives
# UV_PROJECT_ENVIRONMENT from InstallDir with no per-service override - so a
# shared tree is a shared venv and a shared venv is impossible here. This gets
# its own InstallDir and its own everything:
#
#   %LOCALAPPDATA%\idlegpu\runtime\voxtral\
#     uv.exe          a single ~17 MB binary with no prerequisites of its own
#     python\         CPython 3.12.14, fetched by uv. Duplicated from the
#                     chatterbox tree at about 60 MiB against 12 GiB, which is
#                     the correct trade and is said out loud rather than
#                     engineered around
#     venv\           the locked environment, torch cu126 and 34 other packages
#     voxtral-int4\   the third-party wrapper, at a PINNED commit
#     models\original\  7.49 GiB of weights, straight from Hugging Face
#     cache\          uv's wheel cache, reclaimed at the end
#
# Uninstall is: delete that directory. `idlegpu service remove voxtral` does it.
#
# MEASURED COST. The finished tree is about 12.2 GiB. During the install it
# passes about 16.7 GiB, because uv copies wheels rather than hardlinking on
# this filesystem, and the cache is reclaimed before this script declares
# itself finished. Nothing here is shared with any other service.
#
# FOUR DEPARTURES FROM services\chatterbox\provision.ps1, each one measured:
#
#   1. huggingface-cli NO LONGER EXISTS. hub 1.x removed
#      huggingface_hub.commands, so the shell-out dies with ModuleNotFoundError.
#      The download is snapshot_download() from Python instead.
#   2. local_dir= BYPASSES HF_HOME. The containment
#      Install.ContainedEnvironment gives every other service for free does not
#      apply to it, so the destination is built from $Root explicitly or 7.5 GiB
#      lands somewhere `service remove` never reaches.
#   3. THE WRAPPER IS A PINNED TARBALL, NOT A CLONE. An unpinned `--depth 1`
#      clone is a build that works today and fails on somebody else's machine
#      next month, and the lock beside this file was resolved against ONE
#      commit. git is not a prerequisite anywhere else in this project either.
#   4. THERE IS NO -SkipModels. chatterbox has one and it still writes the ready
#      marker, which makes "installed but no weights" reachable. It is worse
#      here: a half-finished 7.5 GiB local_dir download leaves a partial
#      consolidated.safetensors that load_file() dies on, so this script checks
#      the file's SIZE rather than its existence.
#
#   powershell -ExecutionPolicy Bypass -File provision.ps1 -Root <dir>

[CmdletBinding()]
param(
    # Mandatory, and passed in by `idlegpu service install voxtral`. There is no
    # default on purpose: a provisioning script that guesses where to put twelve
    # gigabytes is one that will put them somewhere the uninstall does not reach.
    [Parameter(Mandatory = $true)]
    [string]$Root,
    # The project definition and lock. Defaults to the directory this script is
    # in, which is how it works both from a git checkout and from an installed
    # copy.
    #
    # $PSScriptRoot, not $MyInvocation.MyCommand.Path. Inside a param() default
    # the latter is NULL, because $MyInvocation there describes the CALLER - so
    # the default silently became empty and a whole install died with "Cannot
    # bind argument to parameter 'Path' because it is null". Resolved in the
    # body instead, where both are populated.
    [string]$Project = '',
    # THE WRAPPER, PINNED TO A COMMIT. This is the revision every number this
    # service publishes was measured against, and the revision the lock beside
    # this file was resolved for.
    [string]$Commit = '93d3e21bb7c5ccd812600adf9c7bbd8bb69c2ad5',
    # The Hugging Face repository. Ungated, 7.49 GiB.
    [string]$Weights = 'mistralai/Voxtral-4B-TTS-2603'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # a progress bar over SSH is noise

# THE SIX FILES THIS CONTROLLER IMPORTS, BY CONTENT.
#
# NOT THE ARCHIVE'S OWN DIGEST, and that is deliberate. GitHub does not promise
# a byte-stable zip for a given commit - it has changed its compression before -
# so pinning the archive would mean an install that fails one morning for a
# reason nobody can act on. The COMMIT is already a content address for the
# tree; these digests are the same guarantee applied to the six files that are
# actually imported, and they are checked after extraction where line endings
# and archive format cannot get in the way.
$Expected = @{
    'audio_postprocess.py' = 'd33a83546a936240dd8fcd206ec948ad62647d648f6abd919dc30af5fc2f38c5'
    'generate.py'          = 'de2840d9795cbb4a94a8de503f29496c74156e53247650b930ae1ad9a794d335'
    'generate_fast.py'     = 'ca6826bb180f4074ed298efe7ea4a658261153f80742f52190c188499b0674ee'
    'load_model.py'        = '93b223e9ffa22b24202c29cfc8f63686cf1f5ab72e6ecb45b30e5606a992f88c'
    'model.py'             = '633effd41f8f3e3be35acb5b965fe294c0ad72a559af79c22efd2753736ed2d5'
    'torchao_inference.py' = '33326c289d7cd9100284cb74ffab7947630ea0640cf94978478ca43fc8b0d616'
}

# THE WEIGHTS FILE, BY SIZE, BECAUSE EXISTENCE IS NOT THE FAILURE MODE.
# snapshot_download into a local_dir writes as it goes, so a download killed at
# 80 per cent leaves a consolidated.safetensors that exists, opens, and dies
# inside load_file() 63 seconds into the first job. Measured: 8,004,752,248
# bytes. Hashing 7.5 GiB to say the same thing would add minutes to every
# install for a failure this catches.
$WeightsFile = 'consolidated.safetensors'
$WeightsBytes = 8004752248L

$statusFile = Join-Path $Root 'provision.status'

function Set-Status([string]$text) {
    # The tray polls this file and shows it in its own colour. First run pulls
    # about 12 GiB; a grey icon for half an hour reads as broken and gets killed
    # at 6 GB, which is the worst possible moment to stop.
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

    # Everything uv does stays inside $Root.
    #
    # DEFERS TO THE ENVIRONMENT WHEN THERE IS ONE. `idlegpu service install`
    # sets all of these before launching this script, and sets the SAME values
    # again for the controller at run time (Install.ContainedEnvironment). If
    # this script overrode them, provisioning would put things in one place and
    # the controller would look in another. Setting them here is the fallback
    # for running this script by hand.
    function Default-Env([string]$name, [string]$value) {
        if (-not [Environment]::GetEnvironmentVariable($name)) {
            [Environment]::SetEnvironmentVariable($name, $value)
        }
    }
    Default-Env 'UV_PYTHON_INSTALL_DIR'  (Join-Path $Root 'python')
    Default-Env 'UV_PROJECT_ENVIRONMENT' (Join-Path $Root 'venv')
    Default-Env 'UV_CACHE_DIR'           (Join-Path $Root 'cache\uv')
    Default-Env 'HF_HOME'                (Join-Path $Root 'models\hf')
    Default-Env 'HF_HUB_CACHE'           (Join-Path $Root 'models\hf\hub')
    Default-Env 'TORCH_HOME'             (Join-Path $Root 'models\torch')
    # MEASURED LEAK: without UV_PYTHON_BIN_DIR, `uv python install` still drops
    # a shim into %USERPROFILE%\.local\bin, outside the contained directory and
    # outliving an uninstall. The agent sets this too; this is the by-hand
    # fallback.
    Default-Env 'UV_PYTHON_BIN_DIR'      (Join-Path $Root 'bin')
    Default-Env 'UV_TOOL_DIR'            (Join-Path $Root 'tools')
    Default-Env 'UV_TOOL_BIN_DIR'        (Join-Path $Root 'bin')
    $env:UV_NO_CONFIG = '1'
    $env:UV_PYTHON_DOWNLOADS = 'automatic'

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
    # No system Python is consulted and none is required. This is the same
    # CPython 3.12 build the chatterbox tree has, fetched again because the two
    # trees cannot share one - see the header.
    Set-Status 'fetching CPython 3.12 (24 MB)'
    & $uv python install 3.12
    if ($LASTEXITCODE -ne 0) { throw "uv python install failed" }

    # ---------------------------------------------------------- packages ---
    # --frozen means: use uv.lock exactly, do not re-resolve. Every package in
    # the lock has a wheel, and no-build = true in pyproject.toml means a
    # resolution that ever needed a compiler fails here rather than on somebody's
    # gaming PC at 4 GB in.
    Set-Status 'installing torch 2.14 cu126 and friends (4.6 GB) - this is the long one'
    & $uv sync --project $Project --frozen --no-dev
    if ($LASTEXITCODE -ne 0) { throw "uv sync failed" }

    $py = Join-Path $env:UV_PROJECT_ENVIRONMENT 'Scripts\python.exe'
    if (-not (Test-Path $py)) { throw "venv python missing at $py" }

    # ------------------------------------------------------------- gate ---
    # THE SINGLE MOST IMPORTANT CHECK IN THIS FILE, and it matters more here
    # than it does for chatterbox. Installing the CPU-only wheel is the most
    # common way this setup fails and it usually fails silently - but this
    # checkpoint has NO CPU PATH AT ALL to fail silently onto: torchao's int4
    # kernel calls torch.cuda.get_device_capability() before any device
    # dispatch. So a CPU-only wheel here is not a slow worker, it is a service
    # that fails every job it is ever given. Fail now, loudly, while somebody
    # is watching.
    Set-Status 'checking CUDA'
    $probe = @'
import sys, torch
print("torch          ", torch.__version__)
print("cuda available ", torch.cuda.is_available())
print("cuda version   ", torch.version.cuda)
if not torch.cuda.is_available():
    sys.exit("FATAL: torch cannot see the GPU. This is almost always the CPU-only "
             "wheel from PyPI rather than the cu126 wheel from download.pytorch.org. "
             "Check that uv.lock pins torch==2.14.0+cu126. There is no CPU fallback "
             "for this checkpoint.")
if "+cu" not in torch.__version__:
    sys.exit("FATAL: %s is not a CUDA build." % torch.__version__)
import torchao
print("torchao        ", torchao.__version__)
print("device         ", torch.cuda.get_device_name(0))
free, total = torch.cuda.mem_get_info()
print("vram free/total %.0f / %.0f MiB" % (free/2**20, total/2**20))
if total/2**20 < 8000:
    print("WARNING: the load peaks at about 8265 MiB and this card is smaller "
          "than that. Every job will be refused by the VRAM preflight.")
'@
    $probe | & $py -
    if ($LASTEXITCODE -ne 0) { throw "CUDA check failed" }

    # ---------------------------------------------------------- wrapper ---
    # A PINNED COMMIT, FETCHED AS AN ARCHIVE. voxtral-int4 has no packaging of
    # any kind - it is a directory of scripts - so there is nothing to pip
    # install and the controller puts this directory on sys.path instead.
    $wrapper = Join-Path $Root 'voxtral-int4'
    if (-not (Test-Path (Join-Path $wrapper 'src\torchao_inference.py'))) {
        Set-Status "fetching the voxtral-int4 wrapper at $($Commit.Substring(0,7)) (2 MB)"
        $zip = Join-Path $Root 'wrapper.zip'
        $stage = Join-Path $Root 'wrapper-stage'
        Invoke-WebRequest -UseBasicParsing `
            -Uri "https://github.com/TheMHD1/voxtral-int4/archive/$Commit.zip" `
            -OutFile $zip
        if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
        Expand-Archive -Path $zip -DestinationPath $stage -Force
        Remove-Item -Force $zip
        $inner = Get-ChildItem $stage -Directory | Select-Object -First 1
        if (-not $inner) { throw "the wrapper archive was empty" }
        if (Test-Path $wrapper) { Remove-Item -Recurse -Force $wrapper }
        Move-Item -Path $inner.FullName -Destination $wrapper
        Remove-Item -Recurse -Force $stage
    }

    # BY CONTENT, AFTER EXTRACTION. See the note on $Expected: the commit is the
    # pin and these are the six files that are actually imported. A wrapper that
    # is not the one the lock was resolved against, and not the one the audio
    # was measured on, must not be installed silently.
    Set-Status 'verifying the wrapper'
    foreach ($name in $Expected.Keys) {
        $path = Join-Path $wrapper "src\$name"
        if (-not (Test-Path $path)) { throw "the wrapper is missing src\$name" }
        $got = (Get-FileHash -Algorithm SHA256 -Path $path).Hash.ToLowerInvariant()
        if ($got -ne $Expected[$name]) {
            throw ("src\$name is not the file this service was built against " +
                   "(sha256 $got, expected $($Expected[$name])). The pinned " +
                   "commit is $Commit; do not install a different one without " +
                   "re-measuring, because the flow_steps and cfg_alpha defaults " +
                   "in worker.ini were chosen by ear against this code.")
        }
    }

    # ----------------------------------------------------------- models ---
    # STRAIGHT INTO $Root\models\original, WHICH IS NOT HF_HOME. local_dir
    # bypasses the hub cache entirely, so the containment every other cache
    # variable gives us for free does not apply and the path is built here.
    # The layout matches what the wrapper's own scripts expect, and the
    # controller passes it explicitly anyway.
    #
    # DONE HERE RATHER THAN ON THE FIRST JOB so that the first job is not a
    # half-hour download inside somebody's idle window.
    $models = Join-Path $Root 'models\original'
    $weightsPath = Join-Path $models $WeightsFile
    $have = (Test-Path $weightsPath) -and ((Get-Item $weightsPath).Length -eq $WeightsBytes)
    if (-not $have) {
        Set-Status "downloading Voxtral weights, 7.49 GB - this takes a while"
        $fetch = @"
import os
from huggingface_hub import snapshot_download
# NOT huggingface-cli: hub 1.x removed huggingface_hub.commands and the
# shell-out dies with ModuleNotFoundError. Measured.
target = r"$models"
os.makedirs(target, exist_ok=True)
p = snapshot_download("$Weights", local_dir=target)
print("weights in", p)
"@
        $fetch | & $py -
        if ($LASTEXITCODE -ne 0) { throw "weights download failed" }
    }

    # THE SIZE, NOT THE EXISTENCE, AND IT IS THE LAST GATE BEFORE THE MARKER.
    if (-not (Test-Path $weightsPath)) { throw "$WeightsFile is missing after the download" }
    $size = (Get-Item $weightsPath).Length
    if ($size -ne $WeightsBytes) {
        throw ("$WeightsFile is $size bytes and should be $WeightsBytes. This is " +
               "an interrupted download, not a corrupt one: it will open and then " +
               "die inside load_file() 63 seconds into the first job. Run this " +
               "script again; it resumes.")
    }
    $voices = @(Get-ChildItem (Join-Path $models 'voice_embedding') -Filter *.pt -ErrorAction SilentlyContinue)
    if ($voices.Count -lt 1) {
        throw ("no voice embeddings were downloaded. This checkpoint has no " +
               "speaker encoder, so with no .pt files there is no voice it can " +
               "speak in and every job would be refused.")
    }
    Write-Host ("voices: " + $voices.Count)

    # ---------------------------------------------------------- reclaim ---
    # THE WHEEL CACHE IS DEAD WEIGHT ONCE THE VENV EXISTS, and it is 4-5 GB of
    # it on somebody's games drive. uv copies rather than hardlinks on this
    # filesystem, which is why deleting one does not damage the other - and that
    # is ASSERTED below rather than assumed, because if uv ever did hardlink
    # here this step would silently gut the environment and the failure would
    # appear at the first job instead.
    Set-Status 'reclaiming the wheel cache'
    & $uv cache clean
    & $py -c "import torch, torchao, scipy, tiktoken, soundfile; print('post-clean import ok', torch.__version__)"
    if ($LASTEXITCODE -ne 0) {
        throw ("the environment stopped importing after the cache was cleaned; " +
               "uv must be hardlinking into the venv on this filesystem. " +
               "Reinstall, and remove the cache clean above.")
    }

    Clear-Status

    # THE READY MARKER, AND IT IS THE LAST THING THIS SCRIPT DOES. Twelve
    # gigabytes is a long download; an install interrupted at eight must read as
    # "not installed" rather than "installed and broken". The first is fixed by
    # running this again, the second is a support question. `idlegpu service
    # list` reads exactly this file and nothing else.
    #
    # AND IT IS NOT THE ONLY THING THE MARKER STANDS BETWEEN. This controller
    # publishes its manifest BEFORE it loads anything, precisely so that an
    # out-of-memory is one failed job rather than a relaunch loop - but a
    # missing wrapper or missing weights would still be a failure on every job
    # for ever. Writing this file last is what keeps that state unreachable.
    $torchVersion = (& $py -c "import torch; print(torch.__version__)")
    Set-Content -Path (Join-Path $Root '.installed') -Encoding UTF8 -Value @"
service   = voxtral
wrapper   = $Commit
weights   = $Weights
python    = $py
torch     = $torchVersion
installed = $((Get-Date).ToUniversalTime().ToString('o'))
"@

    $total = (Get-ChildItem -Recurse -File $Root | Measure-Object -Property Length -Sum).Sum
    Write-Host ""
    # THE WHOLE TREE, AND IT IS ALL THIS SERVICE'S. Nothing here is shared with
    # chatterbox, so unlike that script's figure this one is both the total and
    # the incremental cost, and removing this service gives all of it back.
    Write-Host ("voxtral is installed; {0} holds {1:N2} GB, none of it shared" -f `
                $Root, ($total / 1GB))
    Write-Host "worker.ini needs, in [service.voxtral]:"
    Write-Host "  Command   = $py"
}
catch {
    Set-Status ("FAILED: " + $_.Exception.Message)
    Write-Error $_
    exit 1
}
