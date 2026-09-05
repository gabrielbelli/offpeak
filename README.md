# idlegpu

**Lend a gaming PC's idle GPU to whatever you like, and give it straight back the
instant its owner wants it.**

One 127 KB executable watches the machine. When nobody is using the GPU it runs
work you have asked it to run. When somebody starts a game it stops, in well
under a second, and does not come back for a minute and a half.

> **Windows and NVIDIA only.** The detection is `nvidia-smi` plus Windows
> performance counters, and it depends on details of the WDDM display driver
> model. There is no Linux or AMD path and none is planned. This is a boundary,
> not a gap.

```
idlegpu service install echo        # 21 MB, no GPU needed, proves the whole path
idlegpu submit echo --body-file job.json --wait
```

---

## Contents

- [What this actually is](#what-this-actually-is)
- [The hard part, and why the obvious answer fails](#the-hard-part-and-why-the-obvious-answer-fails)
- [The policy: a ladder, not a threshold](#the-policy-a-ladder-not-a-threshold)
- [Nothing is installed until you ask](#nothing-is-installed-until-you-ask)
- [What it costs on disk](#what-it-costs-on-disk)
- [Install](#install)
- [The API](#the-api)
- [TLS, and why there is no certificate authority](#tls-and-why-there-is-no-certificate-authority)
- [Exposing it to a LAN, and the one admin click](#exposing-it-to-a-lan-and-the-one-admin-click)
- [Adding a service](#adding-a-service)
- [How yielding works, measured](#how-yielding-works-measured)
- [Containment, and the four things outside the folder](#containment-and-the-four-things-outside-the-folder)
- [Uninstall](#uninstall)
- [Anti-cheat](#anti-cheat)
- [Tests](#tests)
- [Layout](#layout)
- [What is real and what is not](#what-is-real-and-what-is-not)

---

## What this actually is

Three pieces that are useful separately and only interesting together:

1. **A policy that can tell whether somebody is using a GPU.** This is the part
   with no off-the-shelf equivalent. Every distributed computing project has
   something like it and every one of them is a list of executable names. This
   one is a ladder of signals with a measured idle baseline behind each
   threshold, and 172 tests over recorded samples.
2. **A generic work runner.** A service is a process the agent supervises inside
   a Windows job object plus a queue that is a directory. The runner never parses
   a job or opens an artefact. It has no word for audio, images or hashes.
3. **A TLS listener and a CLI**, so you can talk to the machine directly. There
   is no central server, and none is required.

One runner hosts many services. Speech is one. Image generation is another. A
long password-recovery run is another. See
[docs/ADDING-A-SERVICE.md](docs/ADDING-A-SERVICE.md).

**The one promise that outranks everything: the machine belongs to somebody who
plays games on it, and they have absolute priority.** Every design decision below
that conflicts with that decision loses.

---

## The hard part, and why the obvious answer fails

The obvious answer is "run when `utilisation.gpu` is below some threshold". It
fails in three separate ways, each measured rather than assumed. This baseline is
150 samples at 1 Hz on an RTX 3070 (driver 610.47, Windows 11 Pro 10.0.26200),
desktop up and untouched, and it is checked into
`tests/fixtures/_recorded_idle_spring.csv` so you can look at it:

| signal | reading while idle |
|---|---|
| `utilisation.gpu` | **5-6%**, never lower, in all 150 samples |
| `clocks.mem` | **810 MHz**, zero variance |
| `pstate` | **P5**, 150 of 150 |
| `power.draw` | 33.68-35.13 W |
| `memory.used` | 416 MiB, zero variance |

**Wrong low.** A threshold under 5% can never be satisfied on that machine. A
threshold over 6% is inside the noise. The naive test is not imprecise here, it
is *unreachable*.

**Wrong high.** A browser decoding video raises utilisation without touching the
shaders. Yielding to that makes the runner useless on any desktop that plays
anything.

**Wrong late, and this is the expensive one.** A game paused at a menu draws
nothing at all. It still owns its VRAM and resumes in one frame.
`utilisation.gpu` calls that idle and hands the card away.

And the obvious fix for the third case **is not available**:
`nvidia-smi --query-compute-apps` returns `used_memory` as **`[N/A]` for every
process**, because of the WDDM driver model. `pmon` shows `-` per process too.
Per-process GPU memory simply is not there.

What *is* there is the Windows performance counters, which is where the paused
game is caught: `\GPU Process Memory(*)\Dedicated Usage` agreed with
`nvidia-smi`'s total to within 1.5%, and `\GPU Engine(*)` separates `3d` from
`videodecode`, which is what tells a game apart from a video.

**Every threshold in this document came off one machine and is written into
`worker.ini`, not into the source.** If your card idles differently they are
wrong for you, and changing them does not need a compiler. Run
`idlegpu --calibrate baseline.csv 20` while you are not using the machine, then
again while you play, and set them from what you see.

---

## The policy: a ladder, not a threshold

### Tier 0 — blind. Fail closed.

`nvidia-smi` is dead or stale, or the agent is not in the console session. The
state is **`Blocked`**, deliberately a different word from `Busy`: "I cannot see
you" is not "a game is running", and one of those needs fixing while the other
does not.

The policy *starts* in `Blocked`. An agent that has seen no samples knows
nothing, and "knows nothing" must never mean "help yourself to the GPU".

> This is also why the agent starts from your Startup folder and **not as a
> Windows service**. Measured: over SSH the agent lands in session 0 while the
> console user is in session 1, and from there `GetForegroundWindow` returns 0
> and `GetLastInputInfo` reported 620953 ms of idleness while somebody was
> sitting at the machine. A service is in exactly that position permanently. It
> would be blind to the person it exists to yield to.

### Tier 1 — vetoes. A game **exists**. Instant, no confirmation.

These survive a pause, an alt-tab and a minimise, which `utilisation.gpu` does
not.

| signal | source | why |
|---|---|---|
| `RunningAppID != 0` | `HKCU\Software\Valve\Steam` | Steam writes it **before the first frame** |
| an anti-cheat service running | service control manager | starts with the game, before the window |
| a named game process | `GameProcessNames` | survives pause and minimise |
| 512 MiB held by a non-allowlisted process | `\GPU Process Memory` | the paused-game backstop |
| a full-screen foreground window | `GetWindowRect` vs `GetMonitorInfoW` | not proof of a game, but proof somebody is doing one thing with their whole screen |

> **`vgk` is useless and `vgc` is the signal.** Riot Vanguard installs both.
> Measured on the test machine: `vgk` (the kernel driver) Running/System
> permanently, `vgc` (the user-mode service) Stopped/Manual until a game starts.
> A check for "Vanguard is running" is always true and therefore says nothing.
> The list is `AntiCheatServices` in `worker.ini`, because the next one to matter
> will not be called `vgc`.

Every controller this runner started is exempt from the VRAM veto, **by pid and
never by name**. Counting our own memory as evidence of a user makes the policy
oscillate: allocate, see our own VRAM, yield, free it, see nothing, allocate,
for ever.

### Tier 2 — votes. Something is **drawing**. Three consecutive seconds.

Memory clock above 1500 MHz · pstate outside P5/P8/P12 · power above 70 W ·
utilisation above 25% · 3D engine above 20%.

**Video decode deliberately does not yield.** **Keyboard and mouse idleness
deliberately does not gate anything** — this is about the GPU. `GetLastInputInfo`
is recorded into `state.json` for the trace and is never consulted by the
decision.

### The asymmetry is the safety property

```ini
BusyConfirmSamples   = 3     # about 3 s for a tier-2 vote; tier 1 is instant
ClearCooldownSeconds = 90    # busy -> available
YieldGraceSeconds    = 2
```

Going busy is instant. Coming back takes **90 seconds of everything staying
clear**. Ninety seconds is longer than a level load, longer than alt-tabbing to a
browser mid-match, and longer than the gap between two rounds.

A false busy costs one job. A false idle costs somebody their game.

---

## Nothing is installed until you ask

**The base install downloads nothing.** It is the agent, the tray, the listener,
the CLI and the policy, and that is all. A machine whose owner never wanted
speech never sees a byte of torch.

A service is opted into explicitly, and only then does it fetch anything:

```
idlegpu service list                 # what this build knows about, and what each costs
idlegpu service install chatterbox   # NOW it downloads
idlegpu service cost                 # what each one is actually using, measured
idlegpu service disable chatterbox   # stop running it, keep it on disk
idlegpu service remove chatterbox    # delete it and reclaim the disk
```

### Three states, never conflated

This distinction is the reason the API exists in the shape it does.

| state | meaning | whose problem |
|---|---|---|
| **known** | there is a section for it in `worker.ini`. Nothing downloaded, nothing running. | the owner's, and it takes one command |
| **installed** | provisioning finished and left its marker. Real disk committed. | — |
| **ready** | installed *and* enabled. The scheduler will run it. | — |

and, separately and orthogonally, because it is a property of the **machine** and
not of any service:

| | |
|---|---|
| **gpu_available** | is the GPU free right now | nobody's, and it clears on its own |

A client asking "can you speak for me" must be able to tell *"no, speech is not
installed on this runner"* from *"no, somebody is playing a game"*. The first is
a five-minute fix by the machine's owner. The second is nobody's to fix and will
resolve itself. A client that cannot tell them apart retries for ever against a
runner that was never going to say yes.

`GET /v1/services` reports all four separately and never merges them.

---

## What it costs on disk

**Measured on the test machine, not estimated.**

| | files | size |
|---|---|---|
| **base install** | **13** | **426 KB** |
| `idlegpu.exe` (console build) | 1 | 127 KB |
| `idlegpuw.exe` (tray build) | 1 | 127 KB |
| everything else (config, controller scripts, `uv.lock`) | 11 | 172 KB |

Per service, only if you install it:

| service | measured | what it is |
|---|---|---|
| `echo` | **21.5 MiB** | an embeddable CPython 3.12.10 and nothing else |
| `chatterbox` | **8.17 GiB** | 5.01 GB of torch cu126 and its dependencies, 2.99 GB of model weights, 0.06 GB of CPython |

> The chatterbox install lands at 13.1 GB and then reclaims 5.0 GB of downloaded
> wheels before it declares itself finished, because a wheel cache on somebody's
> games drive is 5 GB that will never be read again. The provisioning script
> re-imports torch afterwards and fails loudly rather than quietly if that
> assumption was ever wrong on a filesystem where uv hardlinks instead of copies.

`idlegpu service cost` walks the directories and prints the real numbers for your
machine. It measures rather than quoting this table, because a figure in a README
ages the moment a dependency does.

> Two binaries, not one, and the reason is measured. A Windows-subsystem
> executable is not waited for by a shell. Over SSH, `idlegpu service list` built
> as a single `winexe` printed nothing, set no exit code, and dumped its output
> into the middle of the *next* command; redirecting to a file produced zero
> bytes. So `idlegpu.exe` is the console build you type at and `idlegpuw.exe` is
> the windowed build autostart points at, exactly as `python.exe` and
> `pythonw.exe` are.

---

## Install

Needs Windows, an NVIDIA GPU, and .NET Framework 4.8 — which is **already part of
Windows**. There is no SDK to install, no NuGet restore, and no network access
required to build: the C# compiler ships inside the operating system.

```powershell
git clone <this repo>
cd idlegpu
powershell -ExecutionPolicy Bypass -File build.ps1
powershell -ExecutionPolicy Bypass -File install.ps1
```

That copies 426 KB into `%LOCALAPPDATA%\idlegpu` and puts a shortcut in your
Startup folder. It downloads nothing.

### Start in `Off` mode for a week

This is the recommended first step and it costs you nothing.

```ini
StartMode = Off
```

In `Off` the agent watches, evaluates the policy and logs every verdict, and
**never takes the GPU**. Play for a week, then read `worker.log`. If it never
said `available` while you were playing, its opinions are worth trusting. If it
did, you have found a threshold to change before it could cost you a match.

### Then install a service

```powershell
idlegpu service install echo        # 21.5 MiB, needs no GPU
idlegpu service list
```

`echo` exists so you can exercise the entire path — submit, schedule, run, yield,
artefact — before spending six gigabytes finding out whether speech works. The
expensive part of this system is not the part most likely to be wrong.

---

## The API

`https://127.0.0.1:47600` by default. Real HTTP/1.1, so `curl` works.

| | |
|---|---|
| `GET /healthz` | loopback, no auth. Is the agent up, is a controller alive |
| `GET /v1/status` | the published snapshot: mode, state, seconds until available, the GPU sample |
| `GET /v1/services` | every known service, its three states, its labels and the manifest its controller published |
| `POST /v1/services/{id}/jobs` | submit. Body is service-defined JSON. `Idempotency-Key` honoured |
| `GET /v1/services/{id}/jobs/{job}` | queued / running / done / failed / cancelled |
| `GET /v1/services/{id}/jobs/{job}/result` | stream the artefact bytes |
| `DELETE /v1/services/{id}/jobs/{job}` | withdraw it, or ask the controller to stop |
| `HEAD /v1/assets/{sha256}` | do you already hold this blob |
| `POST /v1/assets` | upload a blob, get its sha256 back |
| `POST /v1/mode` | `Auto` \| `AlwaysOn` \| `Off` |

`GET /v1/services/{id}/jobs/{job}/events` answers **501 with a sentence telling
you to poll**, rather than 404. A 404 sends you looking for a typo.

### Idempotency is not optional

A yield is the **normal** case, not an error. When the owner comes back mid-job
the lease goes back to `pending` and the job reports `queued` again, and a client
will retry. `Idempotency-Key` is what makes that a retry rather than a second
run — which, for a speech service, is the difference between one sentence and the
same sentence spoken twice.

### The CLI

First class, because some services are command lines by nature.

```
idlegpu status
idlegpu services
idlegpu submit hashcat -- -m 22000 hash.hc22000 rockyou.txt
idlegpu watch hashcat <job>
idlegpu result hashcat <job> -o cracked.txt
idlegpu mode Off
idlegpu fingerprint
```

Everything after `--` becomes `{"argv": [...]}` and is handed to the controller
verbatim. The runner never learns what `-m 22000` means.

Connect to another machine with `--host`, `--port`, `--fingerprint` and
`--key-file`.

> **PowerShell strips double quotes** when it passes an argument to a native
> program, so `--body '{"a":1}'` arrives as `{a:1}`. The client checks the JSON
> before it sends and says so; use `--body-file` for anything non-trivial.

---

## TLS, and why there is no certificate authority

**Everything is TLS 1.2 or 1.3, including on loopback, and verification is never
disabled.** There is no `verify=False`, no blanket-trust callback, and no flag to
add one.

A standalone project cannot assume you own a certificate authority, so **the
trust root is a pinned fingerprint**. On first run the agent mints a self-signed
certificate into its own directory (touching no certificate store) and prints its
SHA-256. Clients compare `GetCertHashString()` against that value and refuse
anything else.

```powershell
idlegpu fingerprint
# 2ddc244cfcd75ef2a86f5d4434d7c20aeaae4fcaa416dfe8d409cd23440151e9
```

Being unable to verify is a reason to fix configuration. It is never a reason to
switch verification off.

Three implementation details that are load-bearing rather than incidental:

- **`TcpListener` + `SslStream`, never `HttpListener`.** Measured under a
  genuinely non-elevated token: `HttpListener` binds `localhost` and *nothing
  else* without administrator rights, and its `https` prefixes reset every
  connection because binding a certificate to a port is `netsh http add sslcert`,
  which is administrator, machine-wide, and outlives an uninstall. A raw
  `TcpListener` binds `0.0.0.0` with no reservation and no admin. So the HTTP
  framing here is hand-written, and it refuses what it does not implement:
  chunked bodies get 501, `Expect: 100-continue` gets 417, and every response is
  `Connection: close`.
- **`AuthenticateAsServer(cert, false, Tls12 | Tls13, false)`, never the
  one-argument overload.** That overload inherits
  `ServicePointManager.SecurityProtocol`, and this program is compiled by a bare
  `csc.exe` with no project file. Measured: an assembly with no
  `TargetFrameworkAttribute` gets a default of **`Ssl3, Tls`**, and the same
  assembly with the attribute gets `SystemDefault`. So `src/AssemblyInfo.cs`
  carries the attribute *and* every call site names its protocols, because either
  alone is one edit away from silently re-enabling SSL 3.0.
- **`SslProtocols.None` throws on .NET Framework 4.8**, so it is never passed.

---

## Exposing it to a LAN, and the one admin click

**Loopback by default.** That is a decision, not timidity, and this is the
paragraph to read before changing it.

A non-elevated process can bind `0.0.0.0` with no reservation — measured, it
gets `LISTENING`. What it **cannot** do is open the Windows Firewall.
`New-NetFirewallRule` returns "Cannot connect to CIM server. Access denied" and
`netsh advfirewall` returns "The requested operation requires elevation".
Measured from another machine on the same LAN, every connection to that listening
socket failed silently, with no rule created and no prompt shown.

Worse, from Microsoft's own documentation: **if a user without administrator
rights is prompted, a BLOCK rule is created whatever they click**, and if
notifications are off there is no prompt at all. That failure is permanent and
silent, and no amount of reading this agent's log will explain it.

So:

- **Loopback needs no firewall rule and works over SSH immediately.** This is the
  path to prefer:
  ```bash
  ssh -N -L 47600:127.0.0.1:47600 you@thatmachine
  ```
- **A LAN bind needs one administrator click, once.** Set `Bind = 0.0.0.0`, and
  the agent then **refuses to start** unless you have also set an `ApiKeyFile`
  and an `AllowedCidrs` allowlist. An operator who exposes a socket and forgets
  the token has made a mistake nothing else will make them notice, because it
  will appear to work.

Authentication is two independent checks, both before any lease is written: the
pinned certificate, and a bearer token compared in constant time. Loopback may
run tokenless, the way BOINC's GUI RPC does, because reaching `127.0.0.1` already
means code execution on the machine.

---

## Adding a service

**One INI section and one controller script. No C# is recompiled and no part of
the protocol changes.** Full guide, with worked Hashcat and Stable Diffusion
shapes: [docs/ADDING-A-SERVICE.md](docs/ADDING-A-SERVICE.md).

The whole inter-process protocol is filenames:

```
<QueueDir>/pending/<job>.json      the agent wrote a lease. Work to do.
<QueueDir>/working/<job>.json      the controller claimed it, by rename.
<QueueDir>/done/<job>.done.json    the controller finished it.
<QueueDir>/done/<job>.<anything>   an artefact. Bytes. The agent never opens it.
<QueueDir>/service.json            the manifest, written by the controller.
```

There is no socket between the agent and a controller, no JSON parser in the
agent, and no library to link. A controller is any program that can list a
directory, rename a file and read one line from standard input. The status is in
the **file name** so that a directory listing answers "is this done".

`YIELD` arrives on standard input. **EOF also means yield**, which gives a dead
man's switch for free: if the agent dies for any reason the kernel closes the
pipe and the controller exits on its own rather than becoming an orphan holding a
CUDA context.

---

## How yielding works, measured

**t = 0.** The policy returns a veto. State flips to `Busy`. Three things happen
and none of them blocks the sampler:

1. The lease goes back to `pending` before the controller has even noticed.
2. `YIELD` is written to the controller's standard input.
3. `Stop()` is queued to the thread pool, so the 1 Hz sampler keeps sampling.

**t = 759 ms** (measured, `echo` service, cooperative): the controller was gone
and the driver had the context back.

**t = grace expiry** if it did not go: `CloseHandle` on the job object takes the
whole tree down together, including any torch child, and takes it down even if
the agent itself is killed, because the kernel closes the handle when the agent's
process object is torn down. `KILL_ON_JOB_CLOSE` cannot be opted out of by a
badly behaved controller.

Measured with Chatterbox actually resident: the controller was gone **2.5 s**
after the policy decided, and VRAM went from **4013 MiB back to 388 MiB**, which
is the idle baseline. Then the job reports **`queued`, not `failed`**, and
resumes when the cooldown clears — measured, from `running` to `queued` to
`running` again, with the segments already delivered still on disk. That distinction matters more than it looks: a yield is the normal case,
so a client that sees `failed` every time somebody launches a game will conclude
the runner is broken.

Two defects were fixed before the listener landed, because a listener raises the
job rate and makes both bite on essentially every yield:

- **`Stop()` had no re-entrancy guard.** The loop ticks every 1000 ms and the
  grace is 2 s, so `Running` was still true on the next tick and a *second*
  `Stop()` was queued. `Yields` counted one yield twice and `LastYieldMs` was
  overwritten by the second call's stopwatch — corrupting the exact measurement
  this project exists to produce.
- **A job-object handle leaked on every cooperative yield.** `CloseHandle` was
  only reached on the kill path, so a controller that exited politely left one
  orphaned kernel object behind each time.

The status document reports `yields`, `last_yield_ms` and `last_yield_was_kill`
per service, because "it yielded in 2.1 s" means something very different if
2.0 s of that was the grace timer.

---

## Containment, and the four things outside the folder

**Everything lives in one directory**, `%LOCALAPPDATA%\idlegpu`: the executables,
the config, the log, the TLS key, the queues, the asset store, and everything any
service ever downloads — its Python, its site-packages, its model weights, its
caches.

`HF_HOME`, `TORCH_HOME`, `TRANSFORMERS_CACHE`, `PIP_CACHE_DIR`, `UV_CACHE_DIR`,
`UV_PYTHON_INSTALL_DIR`, `UV_PYTHON_BIN_DIR`, `XDG_CACHE_HOME`, `TMP` and `TEMP`
are all pointed inside it, set on the child process and **never on the machine or
the user**. No PATH change, no system-wide environment variable, no MSI, no
service, no admin prompt, no driver.

> Each of those variables is there because something was *observed* writing
> outside the directory without it. The last one found: `uv python install` still
> wrote a 45 KB shim to `%USERPROFILE%\.local\bin\python3.12.exe`, outside the
> contained directory and surviving an uninstall, until `UV_PYTHON_BIN_DIR` was
> set. That was found by listing the profile after an install, not by reading
> documentation, which is the only way this kind of thing is ever found.

Four things exist outside that directory, and they are named here rather than
discovered later:

1. **The autostart entry.** A shortcut in your Startup folder — visible in
   Explorer, disableable in Task Manager's Startup tab. `install.ps1 -UseRunKey`
   uses an `HKCU\...\Run` value instead, for a profile with a redirected Startup
   folder.
2. **A firewall rule, only if you ever enabled a LAN bind** and allowed the
   prompt.
3. **One transient CNG key file** in `%APPDATA%\Microsoft\Crypto\Keys`, about
   1.8 KB, while the TLS certificate is loaded. This one has **no clean fix** and
   is documented rather than hidden: Schannel cannot use an ephemeral key
   (`X509KeyStorageFlags.EphemeralKeySet` loads fine and then fails the handshake
   with "No credentials are available in the security package"), and CNG ignores
   a redirected `APPDATA`. So the certificate is loaded `UserKeySet` **without**
   `PersistKeySet` and disposed deterministically, which removes the file; a hard
   kill can leave one behind, and the agent sweeps stale ones on start.
4. **Windows' own temporary files** from the download, inside the service's
   `cache\tmp`, removed with it.

---

## Uninstall

```powershell
Remove-Item "$([Environment]::GetFolderPath('Startup'))\idlegpu.lnk"
Remove-Item -Recurse -Force "$env:LOCALAPPDATA\idlegpu"
```

That is everything, including every gigabyte any service downloaded. Add
`Remove-ItemProperty HKCU:\Software\Microsoft\Windows\CurrentVersion\Run idlegpu`
if you installed with `-UseRunKey`, and remove the inbound firewall rule if you
ever created one.

To reclaim a service's disk without uninstalling:

```powershell
idlegpu service remove chatterbox
```

---

## Anti-cheat

Riot Vanguard is kernel-mode and resident on the machine this was developed
against, so this is not a hypothetical.

Everything here is passive and ordinary: `Shell_NotifyIcon`, registry **reads**,
`ServiceController.Status`, performance counters, and spawning `nvidia-smi`.

**No `SetWindowsHookEx`, no `ReadProcessMemory`, no injection, no driver, no
input synthesis, no overlay, no elevation.** The parts that could have looked
like automation — anything reaching into another process — are exactly the parts
designed out by reading operating system counters instead.

The residual risk is social rather than technical: an unsigned executable from an
unknown publisher, running at login, spawning a GPU process on a machine with
kernel-mode anti-cheat. **The cheapest mitigation is running in `Off` mode for a
week first**, which is the recommended first step anyway.

---

## Tests

```powershell
powershell -ExecutionPolicy Bypass -File tests\run-tests.ps1
```

**172 assertions, about a second, and no network, no GPU, no NVIDIA driver, no
game and no console session.** They link `Model`, `Config`, `Policy`, `Replay`,
`Jobs`, `Http` and `Install` and nothing else — no `Signals.cs`, so no user32, no
PDH, no registry; no `Listener.cs`, so no socket is opened and no certificate is
minted. The HTTP tests feed a `MemoryStream` to the same parser the listener
feeds a TLS stream to.

**Every test is named after the mistake it prevents**, because a test named after
the function it calls tells you nothing when it goes red at two in the morning:

```
two controllers of ours are both exempt
a paused game is caught by VRAM alone
Valorant is caught by the vgc service
the cooldown is ninety seconds, not eighty-nine
a job id cannot escape the queue directory
a chunked body is refused, not misread
a malformed body is refused at submit
every cache variable points inside the contained directory
a service section is known, not installed
```

Fixtures are twelve generated CSVs plus **two real recordings** from the test
machine. `tests/gen_fixtures.py` is where the line between measured and synthetic
is drawn, in its docstring.

---

## Layout

```
src/            the agent. 3,500 lines of C# 5, built by the csc.exe inside Windows
  Model.cs        pure data, no platform dependency, so the policy is testable
  Policy.cs       the ladder. Takes its clock from the snapshot, never from now
  Signals.cs      nvidia-smi, PDH counters, session, launchers
  Agent.cs        three threads, and the job object that supervises a controller
  Listener.cs     TcpListener + SslStream
  Http.cs         hand-written HTTP/1.1 framing, and what it refuses
  Api.cs          the routes and the two auth checks
  Jobs.cs         the directory queue and the content-addressed asset store
  Install.cs      opting in: the three states, disk cost, reclaiming
  Cli.cs          the client
services/       one directory per service. NONE is installed by default
  lib/            the directory protocol, written once, for Python controllers
  echo/           needs no GPU. Install this first
  chatterbox/     speech. About 6.3 GB
tests/          172 assertions, no network
tools/          loadgen and a fake job, for exercising the yield path
probe/          seven read-only probes that produced every number in this README
docs/           ADDING-A-SERVICE.md, ROADMAP.md
```

---

## What is real and what is not

### Measured end to end on the machine

| | |
|---|---|
| base install | **426 KB**, 13 files, nothing downloaded |
| `echo` install | 21.5 MiB, and it exercises submit / schedule / run / yield / artefact |
| `chatterbox` install | 8.17 GiB after the wheel cache is reclaimed |
| a real synthesis | 2 segments of speech on an RTX 3070, through the TLS API |
| cooperative yield (`echo`) | **759 ms** from the policy deciding to the process being gone |
| forced yield (`chatterbox`) | **2.5 s**, VRAM back from **4013 MiB to 388 MiB** |
| after the yield | the job reads **`queued`**, not `failed`, and resumes when the owner leaves |
| tests | **172 assertions**, about a second, no network |

**The forced yield is the designed path for speech, not a failure.** A speech
model's `generate()` has no interruption point inside it, so a controller in the
middle of a segment cannot answer `YIELD`; the job object takes it, which is why
the job object exists. What the controller declares in its manifest is
`unit_seconds`, corrected to **24.3 s measured on this machine**, so a client
knows the worst case is losing one segment rather than the whole job.

### And the number that is less exciting than you would hope

Chatterbox on this RTX 3070 measured **0.56 to 0.72x realtime** in steady state.
That is faster than the 0.275x the NAS's CPU manages, by roughly two and a half
times, and it is still slower than realtime. The first segment of a cold job
costs 24.3 s because the model loads inside it.

This is written down rather than rounded up because the whole project is an
argument about measuring things instead of assuming them, and "borrow a gaming
GPU and speech gets 20x faster" would have been an assumption. A 2.5x
improvement on the only component in the stack slower than realtime is worth
having. It is not a different category of thing.

**Real, and measured on hardware:** the whole policy and its 172 tests; the tray
and its three modes; the TLS listener, the pinned certificate and the HTTP API;
the directory queue, idempotency, artefacts and the asset store; the scheduler;
the opt-in install path; a real GPU synthesis; the yield, and the resume after
it.

**Deliberately not built**, with reasons, in
[docs/ROADMAP.md](docs/ROADMAP.md): a central server, capability self-testing,
parameter-schema enforcement, server-sent progress, an automatically learnt idle
baseline, and remote CUDA — which was investigated, and whose blocker is that
LUPINE's server side is Linux-only while this GPU is in Windows.

**Known limits, stated plainly:**

- One GPU means one controller at a time, and there is no fairness beyond static
  `Priority`. A long job starves a queued one until it finishes or the owner
  takes the machine back.
- Per-process VRAM is `[N/A]` under WDDM, so a wedged foreign CUDA process is
  visible in aggregate but cannot be attributed. Fine for yielding, blind for
  diagnosis.
- `GameProcessNames` and `VramAllowlist` ship as one person's software and **will
  be wrong for you**. Epic, Game Pass, GOG and Battle.net set no Steam
  `RunningAppID`, so for anything launched from those the VRAM veto is the only
  backstop. `idlegpu status --json` shows what it is objecting to under
  `foreign_vram`, which is how you find out what to add.
- A controller that ignores `YIELD` forces the job-object kill. The GPU is always
  reclaimed; any un-checkpointed state that controller held is lost.
