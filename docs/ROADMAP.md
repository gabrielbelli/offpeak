# Roadmap

Things deliberately not built, with the reason and what would have to change.

Nothing here is a promise. It is a record of decisions, so that the next person
to have one of these ideas can find out in two minutes whether it was already
looked at and what stopped it.

---

## Remote CUDA: attaching this GPU to a machine that has none

**Status: investigated, not built, and the blocker is not ours to remove.**

### What it would buy

The install on the gaming PC would be nearly empty. Instead of six gigabytes of
torch and speech weights on somebody's games drive, the GPU would be published
over the network and the *client* machine would hold the models and the Python
environment. The runner would shrink to the policy engine, the listener and a
GPU-over-IP daemon. `offpeak service install chatterbox` would download nothing.

That is a genuinely attractive shape for this project, because the single
biggest imposition it makes on a volunteered machine is disk.

### What blocks it

**LUPINE** ([lupinemachines/lupine](https://github.com/lupinemachines/lupine),
Apache 2.0) is the mature open-source option. It is a GPU-over-IP bridge that
intercepts the CUDA driver API, cuBLAS, cuDNN and NVML, so unmodified CUDA code
on a CPU-only machine drives a remote GPU. Default port 14833.

**Its server side is Linux only, and that was confirmed from its own
documentation rather than assumed.** The distinction that matters, and that is
easy to get backwards when skimming:

| side | what runs there | platforms |
|---|---|---|
| **client** | the CPU-only machine with no GPU, running the workload | Linux x86_64/aarch64, macOS universal2, **Windows amd64/arm64** |
| **server** | the machine that physically has the GPU | **Linux only**, distributed as Ubuntu-based container images |

The README describes the server image as "based on Ubuntu", ships it only as
Docker tags of the form `cuda-<version>-ubuntu<version>`, and documents
Linux-specific server behaviour (SIGTERM for a graceful checkpoint). No Windows
server image is published and no Windows server is mentioned.

So the Windows client support, which is real and which is what a search turns up
first, is exactly the half that does not help. Our GPU is in Windows.

**Getting a Linux server onto this machine costs more than the feature is
worth**, and it costs it in the one currency this project cannot spend:

- WSL2 needs an administrator, a reboot, and the Virtual Machine Platform and
  Hyper-V features enabled.
- The machine runs kernel-mode anti-cheat (Riot Vanguard). Turning on
  hypervisor features under it is the single most disruptive change that could
  be made to a gaming PC, and at best it is a support conversation with the
  game vendor.
- It is the largest possible violation of the containment rule this project is
  built around: one directory, no admin, no reboot, no driver, uninstall by
  deleting a folder.

The alternatives are worse:

- **rCUDA** is stuck on CUDA 9.0 and Linux. Not viable.
- **NVIDIA PAIR** routes whole inference requests rather than remoting CUDA,
  and speaks only Ollama and LM Studio. It is a different product.

### Would it need protocol changes here? No.

**A remote CUDA endpoint is expressible as an ordinary service in the contract
that exists today, and costs nothing to leave room for.** Concretely, it would be
one `[service.cuda-share]` section and one controller:

```ini
[service.cuda-share]
Enabled           = false
Description       = publish this GPU to a remote machine over the network
Priority          = 1
Labels            = gpu,remote-cuda,passthrough
YieldGraceSeconds = 4
SizeHint          = about 200 MB
Provision         = cuda-share\provision.ps1
Command           = %INSTALL%\lupine-server.exe
```

The generic layer already does everything such a service needs:

- **Supervision and the kill backstop.** A GPU-over-IP daemon is a long-running
  process in a job object, which is precisely what `JobRunner` is.
- **Yielding.** The daemon gets `YIELD` on stdin like anything else. What it
  does with it is its business: refuse new attachments and drop existing ones.
  A larger `YieldGraceSeconds` covers a slower shutdown, the same way it would
  for a Hashcat checkpoint.
- **Advertising.** It publishes a manifest with `control = port`, which is
  already a value in the vocabulary, and clients learn from
  `GET /v1/services` that this runner offers a remote GPU and on which port.
- **Arbitration.** One GPU, one controller at a time, by priority. A remote
  attachment and a local speech job cannot collide, because the scheduler will
  not run both.

The one thing that would genuinely be new is a **session** rather than a job: a
remote attachment has no lease in `pending/` and no artefact in `done/`, it is
just a process that should run while somebody is connected. That fits
`control = port` and needs no new endpoint; the client submits one long-lived
job whose "artefact" is the connection.

So: **nothing about the protocol is in the way. The operating system is.**

### What would have to become true

Any one of these, and it is worth revisiting:

1. LUPINE publishes a Windows server. This is the one to watch; the client
   already runs on Windows, so the CUDA interception layer clearly builds there.
2. The GPU moves to a machine that already runs Linux, at which point this is a
   short afternoon.
3. The owner independently wants WSL2 for other reasons and has already
   accepted the anti-cheat consequences. Then it is opt-in, and the containment
   rule is not being broken on their behalf without asking.

Until then it stays here, at a cost of one document.

---

## Other things deliberately not built

**A central server.** No broker exists and none is required. Clients talk to a
runner directly over TLS with a pinned fingerprint. If several machines ever
need coordinating, that coordinator is *another client of the same HTTP API*,
not a layer underneath it, and nothing in the runner has to change to allow one.
Building a lease-and-expiry protocol against a registry nobody is running would
be inventing a distributed system to avoid typing a hostname.

**Capability self-testing.** `GET /v1/services` publishes what a controller
*declares* in its manifest. Nothing verifies the declaration. vast.ai runs a
real benchmark (ResNet18, ECC check, NCCL, bandwidth) before it will list a
machine, which is the honest version of this, and it is a later addition. Today
a controller that claims it can do something and cannot will fail its first job.

**Per-service parameter validation.** The manifest publishes a `params_schema`
and the runner passes the job body through opaquely. It validates that the body
is well-formed JSON and nothing more. Checking a body against a schema the
service published is a real improvement and is not free: it needs a JSON parser
in the agent, which is exactly what the directory protocol was shaped to avoid.

**Server-sent progress.** `GET /v1/services/{id}/jobs/{job}/events` answers 501
with a sentence telling you to poll instead. Polling is the floor and it works.

**Learning the idle baseline.** `--calibrate` is a manual twenty-minute
recording. Detecting a card's idle pstate, clock and power draw automatically on
first run is the right answer and is not done; the thresholds ship as
configuration with the machine that produced them named in the comment.

**Anything but Windows and NVIDIA.** `Signals.cs` is `nvidia-smi` and Windows
performance counters. This is stated in the first paragraph of the README
because it is a hard boundary, not a gap.

**Real HTTP.** Chunked request bodies are refused with 501, `Expect:
100-continue` with 417, and every response is `Connection: close`. A hand-rolled
server on a socket that might face a LAN should refuse what it does not do
rather than approximate it.
