# Adding a service

**One INI section and one controller script. No C# is recompiled, and no part of
the protocol changes.**

That claim is the point of the whole design, so this document is written to be
checkable rather than reassuring: it lists exactly what you write, exactly what
the agent does for you, and exactly what you must not touch. If you find
yourself wanting to change a file in `src/`, something has gone wrong and the
last section is about that.

The runner is not a speech worker. It has no word for audio, text, images or
hashes anywhere in the generic layer. It moves lease files in and artefact files
out, supervises a process, and takes the GPU back when somebody sits down at the
machine.

---

## What a service is

Two things:

1. **A section in `worker.ini`.** Identity, priority, labels, what to launch,
   how long it may take to yield, and how to install it.
2. **A controller: a program the agent launches.** Any program that can list a
   directory, rename a file, and read one line from standard input. There is no
   library to link and no socket to speak.

Everything else is the agent's job.

## What the agent does for you

| you do not write | because the agent already does it |
|---|---|
| an HTTP server | it has one, over TLS, with a pinned certificate |
| authentication | bearer token and address allowlist, checked before any lease is written |
| a queue | `pending/ working/ done/` created for you, with atomic claim by rename |
| idempotency | `Idempotency-Key` is honoured; a retry never starts a second run |
| process supervision | a Windows job object with `KILL_ON_JOB_CLOSE` takes the whole tree |
| detecting the owner | `Policy.cs`, and it is the entire reason this project exists |
| arbitration | one GPU, one controller, highest priority with queued work wins |
| artefact serving | anything in `done/` streams out of `/result` with a `Content-Length` |
| content-addressed inputs | `/v1/assets` by sha256, cached once, shared across services |
| containment | every cache variable pointed inside your service's own directory |
| disk accounting | `idlegpu service cost` measures your directory |

## What you write

### 1. The INI section

```ini
[service.sd]
Enabled           = false
Description       = Stable Diffusion XL, text to image
Priority          = 20
Labels            = image,sdxl,txt2img
YieldGraceSeconds = 4
SizeHint          = about 7 GB (torch cu126 plus an SDXL checkpoint)
Provision         = sd\provision.ps1
Command           = %RUNTIME%\sd\venv\Scripts\python.exe
Arguments         = "%SCRIPTS%\sd\controller.py" --queue "%QUEUE%"
```

`%RUNTIME%`, `%SCRIPTS%`, `%QUEUE%`, `%INSTALL%` and `%DATA%` are expanded by the
agent, so the section works unedited on anybody's machine. Absolute paths work
too.

**`Enabled = false` is not a placeholder.** A shipped section is a service this
build *knows about*. It downloads nothing and runs nothing until somebody types
`idlegpu service install sd`. That is what keeps a base install at 426 KB on a
machine whose owner only ever wanted to lend a GPU for speech.

**`YieldGraceSeconds` is a promise you are making about the owner's frame rate.**
Four seconds means: when the policy says yield, you have four seconds before the
job object kills you outright. Pick it from your worst-case uninterruptible span,
not your average one, and declare that span in the manifest as `unit_seconds` so
clients can reason about it.

### 2. The provisioning script

A PowerShell script called with `-Root <the service's own directory>`.
Everything it downloads goes under `$Root`. It writes `$Root\.installed` as its
**last** act.

```powershell
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$Root)
$ErrorActionPreference = 'Stop'

# HF_HOME, TORCH_HOME, PIP_CACHE_DIR, UV_CACHE_DIR, XDG_CACHE_HOME, TMP and TEMP
# are ALREADY SET for you, all pointing inside $Root. Do not override them: the
# agent sets the same values again for your controller at run time, and if the
# two disagree you download the weights twice and the second copy is outside the
# contained directory.
& $uv sync --project $PSScriptRoot --frozen --no-dev

# LAST. An install interrupted at 80 per cent must read as "not installed", which
# is fixed by running it again, rather than "installed and broken", which is a
# support question.
Set-Content -Path (Join-Path $Root '.installed') -Value "service = sd"
```

> `$PSScriptRoot`, not `$MyInvocation.MyCommand.Path`. Inside a `param()` default
> the latter is `null`, because `$MyInvocation` there describes the caller. This
> cost a failed 6 GB install to find.

### 3. The controller

Python controllers should use `services/lib/idlegpu_service.py`, which is the
directory protocol already written. `services/echo/controller.py` is 105 lines
including its docstring and is the template:

```python
MANIFEST = {
    "id": "sd",
    "description": "Stable Diffusion XL",
    "labels": ["image", "sdxl", "txt2img"],
    "control": "queue",              # queue | cli | port
    "interruptible": "between-units", # none | between-units | checkpoint
    "unit_seconds": 4.0,              # WORST CASE uninterruptible span
    "vram_mib": 6800,
    "outputs": "image/png",
    "checkpointable": False,
    "params_schema": {"prompt": "string", "steps": "integer"},
}

def handle(job):
    for i in range(job.params.get("n", 1)):
        job.check()                       # raises Yielded if the owner is back
        image = pipe(job.params["prompt"]).images[0]
        job.artefact("png", png_bytes(image))
    return {"images": i + 1}

svc.Service(args.queue, MANIFEST, handle).run()
```

`job.check()` between units and never inside one. That is the whole contract.

**In another language**, implement six rules and you are done:

1. Write your manifest to `<queue>/service.json`, atomically.
2. Glob `<queue>/pending/*.json`. Claim one by renaming it into `<queue>/working/`.
   The rename is the lock: it either succeeds or somebody else got there first.
3. Read one line from stdin on a background thread. `YIELD` sets a flag; **EOF
   also sets it**, which is a free dead man's switch for the agent dying.
4. Check the flag between units of work. On yield, rename the lease back into
   `pending/` and exit. Do not write a terminal record.
5. Write artefacts as `<queue>/done/<job_id>.<anything>`.
6. Finish by writing `<queue>/done/<job_id>.done.json` (or `.failed.json`, or
   `.cancelled.json`). The status is in the **file name**, so the agent never
   needs a JSON parser to answer "is this done".

## The manifest vocabulary

The agent serves this verbatim at `GET /v1/services` and never looks inside. The
fields exist so a client can reason about services it has never heard of.

| field | values | why a client cares |
|---|---|---|
| `control` | `queue`, `cli`, `port` | whether to poll for a result, hold a streaming connection, or connect to a port |
| `interruptible` | `none`, `between-units`, `checkpoint` | whether a yield loses work, and how much |
| `unit_seconds` | number | the worst-case uninterruptible span; the real floor on how fast this machine is handed back |
| `vram_mib` | number | roughly what it holds while running |
| `outputs` | free text, mime-ish | `audio/pcm-f32@24000`, `image/png`, `text`, `file` |
| `checkpointable` | boolean | whether a yield resumes or restarts |
| `params_schema` | object | published, not enforced; the runner passes bodies through opaquely |

## Two worked shapes

### A long job that must survive a yield: Hashcat

A two-hour run against a yield that fires in six seconds is survived by
**checkpoint and restore**, not by abandon and restart.

- Launch `hashcat.exe` with `--restore-timer 60` so a `.restore` file is always
  recent.
- Declare `interruptible: "checkpoint"`, `checkpointable: true`, and a
  `YieldGraceSeconds` larger than speech gets, because a running kernel does not
  stop instantly.
- On `YIELD`, send hashcat the checkpoint-and-quit keystrokes (`c` then `q`) and
  exit. Leave the lease in `pending/`.
- When the owner leaves and the 90-second cooldown clears, the scheduler
  relaunches the same lease and the controller adds `--restore`.
- If the grace still overruns, the job object kills it and the worst loss is the
  work since the last restore point. Bounded, not two hours.

This is `control: "cli"`. The CLI is first class for exactly this:

```
idlegpu submit hashcat -- -m 22000 hash.hc22000 rockyou.txt
```

Everything after `--` becomes `{"argv": [...]}`, which your controller turns
into a command line. The runner never learns what `-m 22000` means.

### A short job that returns bytes: Stable Diffusion

Seconds per image, so `between-units` with `unit_seconds` equal to one image is
enough. Write `done/<id>.png` and let `/result` stream it. Nothing else.

## Where the boundary is

**Never touch these when adding a service:**

`src/Policy.cs`, `src/Model.cs`, `src/Replay.cs`, `src/Agent.cs`,
`src/Listener.cs`, `src/Http.cs`, `src/Api.cs`, `src/Certs.cs`, `src/Cli.cs`,
`src/Jobs.cs`, the yield contract, the certificate, or the tests.

If you believe you need to, one of these is happening:

| symptom | what it actually means |
|---|---|
| "the agent needs to understand my job format" | it does not. Put it in the body; the agent splices it into the lease unparsed. |
| "I need a new endpoint" | you probably need a field in your manifest, or a key in your job body. |
| "my output does not fit in a JSON response" | write it as a file in `done/` and let `/result` stream it. That is what it is for. |
| "I need to know when the GPU is free" | you do not. The scheduler only launches you when it is. |
| "I need to stop cleanly mid-unit" | declare a longer `YieldGraceSeconds` and a truthful `unit_seconds`. Do not make the owner wait for a promise you did not declare. |
| "I need two controllers at once" | there is one GPU. Use `Priority`. |

The one thing that genuinely would need new code is a service that is not a
process the agent can launch, and that has not come up.

## Checking your work

```
idlegpu service list                 # is it known?
idlegpu service install sd           # does provisioning finish and leave a marker?
idlegpu service cost                 # what did it actually cost?
idlegpu services                     # did your manifest reach the API?
idlegpu submit sd --body-file job.json --wait
idlegpu mode Off                     # does it yield? this is the same code path a game takes
idlegpu status --json                # last_yield_ms, last_yield_was_kill
idlegpu mode Auto                    # does the job resume rather than fail?
idlegpu service remove sd            # is all the disk given back?
```

`last_yield_was_kill: true` means your controller ignored `YIELD` and the job
object had to take the GPU by force. The owner still got their machine back,
because that backstop cannot be opted out of, but any in-memory state you held
is gone. Fix the controller.
