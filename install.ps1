# Install the agent for the logged-in user. No administrator, no service.
#
# THE ONE THING THIS FILE EXISTS TO GET RIGHT: the agent must run in the
# interactive desktop session, not session 0.
#
# Measured on spring (probe p3): a process started over SSH lands in session 0
# while the console user is in session 1. From session 0, GetForegroundWindow()
# returns 0 and GetLastInputInfo() reports session 0's own idle time, which was
# 620953 ms while the user was in fact at the machine. A Windows service is in
# the same position. An agent installed that way is blind to the person it exists
# to yield to, and Policy.cs refuses to run at all when it detects this - which is
# correct, and also means a service install would never work rather than working
# badly.
#
# So: autostart in the user's own session. Two ways to do that without an
# administrator, and this script uses the SECOND:
#
#   HKCU\...\Run              one registry value. Works, but a registry write is
#                             invisible, and "uninstall is delete the folder"
#                             stops being true the moment there is a key nobody
#                             can see.
#   the Startup folder        a .lnk in shell:startup. Same session, same timing,
#                             no registry, and the user can SEE it, disable it in
#                             Task Manager's Startup tab, and delete it with the
#                             file manager they already know.
#
# The shortcut wins on every axis that matters to somebody being asked to let a
# program run on their gaming PC. -UseRunKey is there for the one case the
# shortcut cannot cover: a locked-down profile with a redirected or policy-managed
# Startup folder.
#
# WHAT THIS INSTALLS IS SMALL, AND THAT IS THE POINT. The agent, the tray, the
# listener, the CLI and the policy are one executable of about 120 KB. It
# downloads nothing. Services are opted into afterwards, one at a time:
#
#   idlegpu service list
#   idlegpu service install echo          about 25 MB, needs no GPU
#   idlegpu service install chatterbox    about 6 GB, speech on the GPU
#
#   powershell -ExecutionPolicy Bypass -File install.ps1

[CmdletBinding()]
param(
    [string]$Dest = (Join-Path $env:LOCALAPPDATA 'idlegpu'),
    [switch]$UseRunKey,
    [switch]$NoAutostart
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

# Two binaries out of one set of sources: idlegpu.exe is the console build you
# type at, idlegpuw.exe is the Windows-subsystem build autostart points at so no
# console flashes on the desktop at every logon. build.ps1 explains the measured
# defect behind that. python.exe and pythonw.exe are the same pair.
$srcCli  = Join-Path $here 'dist\idlegpu.exe'
$srcTray = Join-Path $here 'dist\idlegpuw.exe'
if (-not (Test-Path $srcCli)) { throw "build it first: powershell -ExecutionPolicy Bypass -File build.ps1" }

New-Item -ItemType Directory -Force -Path $Dest | Out-Null
$exe = Join-Path $Dest 'idlegpu.exe'
$trayExe = Join-Path $Dest 'idlegpuw.exe'
Copy-Item $srcCli $exe -Force
if (Test-Path $srcTray) { Copy-Item $srcTray $trayExe -Force } else { $trayExe = $exe }

# Copy a directory tree, file by file, actually overwriting what is there.
#
# NOT Copy-Item -Recurse -Force -Exclude, WHICH DOES NOT WORK. Measured while
# reinstalling over an existing install: files in destination subdirectories that
# already existed were NOT refreshed, and __pycache__ was copied despite being in
# -Exclude. Combining -Recurse with -Exclude is a long standing PowerShell wart:
# the exclusion is applied to the recursion as well as to the files, and the copy
# quietly does much less than it appears to.
#
# The symptom is the worst kind. Everything reports success, and then a
# provisioning script you edited five minutes ago is not the one that runs, and a
# 6 GB install fails against a lock file you already fixed. Explicit is cheaper
# than clever here; this is fifteen lines and it is honest.
function Copy-Tree([string]$from, [string]$to, [string[]]$skipDirs) {
    if (-not (Test-Path $from)) { return }
    New-Item -ItemType Directory -Force -Path $to | Out-Null
    foreach ($f in Get-ChildItem -Recurse -File $from) {
        $rel = $f.FullName.Substring($from.Length).TrimStart('\')
        $parts = $rel -split '\\'
        if ($parts | Where-Object { $skipDirs -contains $_ }) { continue }
        $target = Join-Path $to $rel
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item $f.FullName $target -Force
    }
}

$skip = @('__pycache__', '.venv', 'venv', 'python', 'models', 'cache', 'bin', 'tools')

# The controller scripts and the shared library travel with the agent, so that
# `idlegpu service install <id>` can find a provisioning script without a git
# checkout. These are kilobytes. The gigabytes a service downloads go into
# runtime\<id>\ underneath and are never copied from here.
Copy-Tree (Join-Path $here 'services') (Join-Path $Dest 'services') $skip

# Small helpers that are useful for testing the yield path and are not part of
# any service.
Copy-Tree (Join-Path $here 'tools') (Join-Path $Dest 'tools') $skip

# Never overwrite a worker.ini somebody has edited. Their thresholds, their
# allowlist, their notes.
$ini = Join-Path $Dest 'worker.ini'
$example = Join-Path $here 'worker.ini.example'
if (-not (Test-Path $ini) -and (Test-Path $example)) { Copy-Item $example $ini }
if (Test-Path $example) { Copy-Item $example (Join-Path $Dest 'worker.ini.example') -Force }

if (-not $NoAutostart) {
    if ($UseRunKey) {
        Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' `
            -Name 'idlegpu' -Value ('"' + $trayExe + '"')
        $autostart = "the HKCU Run value 'idlegpu'"
        $undo = "Remove-ItemProperty HKCU:\Software\Microsoft\Windows\CurrentVersion\Run idlegpu"
    } else {
        $startup = [Environment]::GetFolderPath('Startup')
        $lnk = Join-Path $startup 'idlegpu.lnk'
        $shell = New-Object -ComObject WScript.Shell
        $s = $shell.CreateShortcut($lnk)
        $s.TargetPath = $trayExe
        $s.WorkingDirectory = $Dest
        $s.Description = 'idlegpu: lend this GPU while nobody is using it'
        $s.Save()
        $autostart = $lnk
        $undo = "Remove-Item '$lnk'"
    }
} else {
    $autostart = '(none; you asked for -NoAutostart)'
    $undo = '(nothing to undo)'
}

Write-Host ""
Write-Host ("installed idlegpu.exe ({0:N0} bytes) and idlegpuw.exe to {1}" -f (Get-Item $exe).Length, $Dest)
Write-Host "autostart: $autostart"
Write-Host ""
Write-Host "NOTHING ELSE HAS BEEN DOWNLOADED. See what this build can do, and what each"
Write-Host "one would cost, with:"
Write-Host "  & '$exe' service list"
Write-Host ""
Write-Host "Start it now, without waiting for a logon:"
Write-Host "  & '$trayExe'"
Write-Host ""
Write-Host "START IN Off MODE IF YOU DO NOT TRUST IT YET. Set StartMode = Off in"
Write-Host "$ini and the agent watches, logs every verdict and never takes the GPU, so"
Write-Host "you can check a week of its opinions against a week of your own gaming."
Write-Host ""
Write-Host "TO UNINSTALL, in full:"
Write-Host "  $undo"
Write-Host "  Remove-Item -Recurse -Force '$Dest'"
Write-Host "and, if you ever set Bind to something other than 127.0.0.1 and allowed the"
Write-Host "firewall prompt, remove that inbound rule too. See the README; those are the"
Write-Host "only three things this program creates outside its own directory, plus one"
Write-Host "transient key file Windows insists on and the agent sweeps for itself."
