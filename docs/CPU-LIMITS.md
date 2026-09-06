# Selling the processor as well as the card

What Windows can actually do about "use my machine, but never get in my way",
measured on one desktop rather than read off a documentation page. Every number
below came from a probe in `probe/`, run on:

    spring, AMD Ryzen 7 5700X3D, 8 cores / 16 threads, single CCD, 96 MiB L3
    31.9 GiB of memory, Windows 11 build 26200, .NET Framework 4.8

Numbers from one machine are documented defaults, not laws. `idlegpu --limits`
re-measures the important ones on yours in about fifty seconds, using the same
code path the tray uses.

## The short version

Use **all three together**, on the job object the agent already creates:

| Mechanism | What it is for | Cost |
|---|---|---|
| `JOB_OBJECT_CPU_RATE_CONTROL_INFORMATION`, hard cap | how much of the machine | none measurable |
| `IDLE_PRIORITY_CLASS` through the job | never preempt the owner | none |
| `JOB_OBJECT_LIMIT_WORKINGSET` maximum | keep the job's memory off the owner's back | needs a privilege |
| `GlobalMemoryStatusEx` before starting | do not start what will hurt | 1 microsecond |

Rejected: **affinity masks**, which are the horizontal answer and pin work to
cores rather than limiting how much of them it takes. Rejected: **commit caps**,
`JOB_OBJECT_LIMIT_JOB_MEMORY` and `JOB_OBJECT_LIMIT_PROCESS_MEMORY`, which kill
the job rather than squeezing it. Rejected as a primary mechanism:
**weight-based rate control**, which does nothing at all unless the thing you are
competing with is also in a rate-controlled job, and a game is not.

## The promise

**The CPU is given back within one sampling tick plus about 200 ms.** Under one
and a half seconds from the owner touching the keyboard to the job being
throttled.

That is a stronger promise than the GPU's six seconds, and it is stronger for a
structural reason rather than a lucky one: giving the CPU back does not require
killing anything. The cap moves on a live job object, so there is no model to
unload and no model to load again afterwards. A job that has been throttled to
five per cent for two hours resumes at full speed the instant the machine is free
again, having lost nothing.

## 1. CPU rate control

`JOBOBJECT_CPU_RATE_CONTROL_INFORMATION` via `SetInformationJobObject`, class
`JobObjectCpuRateControlInformation` (15). Windows 8 and later.

**`CpuRate` is a share of the WHOLE MACHINE, not of one core.** The documentation
says "the portion of processor cycles that the threads in a job object can use
during each scheduling interval, as the number of cycles per 10,000 cycles" and
does not say of what. Measured, with sixteen spinning threads on sixteen logical
processors (`probe/p8_cpu.cs`):

| cap requested | measured share of the machine | which is |
|---|---|---|
| none | 96.2% | 15.4 cores |
| 50% | 50.2% | 8.03 cores |
| 25% | 22.4% | 3.59 cores |
| 10% | 9.3% | 1.48 cores |
| 5% | 4.4% | 0.70 cores |

So it is the whole machine, and it is vertical: nothing about it says which cores
the work runs on, and the scheduler keeps its cache-aware placement.

**When the cap binds the job is descheduled, not slowed.** From the
documentation: "After the job reaches its CPU cycle limit for the current
scheduling interval, no threads associated with the job will run until the next
interval." The interval is short enough that this is invisible, which is the
difference between this and BOINC's throttle (see prior art below).

**It can be changed on a live job, which the tray requirement needs.** Measured:

    at 80%                                        81.5% of the machine
    set to 5% on the running job                  call returned in 0 ms
                                                  under 10% within the first
                                                  200 ms sampling window
    settled                                        5.6%
    raised back to 90%                            over 50% again after 828 ms
    cleared entirely, ControlFlags = 0            97.4%

**No administrator rights and no privilege.** Measured with
`SeIncreaseBasePriorityPrivilege` removed from the token: the cap still applied.

**Weight-based mode is useless here.** `JOB_OBJECT_CPU_RATE_CONTROL_WEIGHT_BASED`
with `Weight = 1`, the lowest of nine, on an otherwise idle machine: the job got
95.9 per cent. At `Weight = 9`: 95.6 per cent. Weight arbitrates between jobs
that are BOTH under rate control. A game is not in a job object at all, so weight
would have arbitrated against nothing.

**`MIN_MAX_RATE` works and is mutually exclusive with the hard cap.** Min 10 per
cent, max 20 per cent measured 16.6 per cent. Setting `MIN_MAX_RATE` and
`HARD_CAP` together failed with error 87, `ERROR_INVALID_PARAMETER`, as
documented. A reservation is not what this needs: the owner's work should get
everything, so there is nothing to reserve.

**Do not set `CpuRate` to 0.** `SetInformationJobObject` returns `INVALID_ARGS`.
This is why zero in the settings means "do not run", not "capped to nothing".

## 2. Priority is necessary and not sufficient

This is the part worth being sceptical about, and the first measurement was
misleading.

`probe/p9_felt.cs` put a **single-threaded** foreground workload doing
register-bound arithmetic against sixteen spinning threads. Idle priority looked
perfect: p99 unit latency 4.87 ms against a 4.81 ms floor, full throughput. If
that had been the only measurement, the conclusion would have been "priority
alone is enough" and it would have been wrong.

`probe/p10_felt_hard.cs` is the honest version. Eight foreground threads at
normal priority, each doing frame-shaped work over a 48 MiB array; sixteen
background threads streaming 128 MiB each, which is more than the whole 96 MiB
L3, so they evict what the foreground had cached.

| condition | median ms | p95 | p99 | max | units done |
|---|---|---|---|---|---|
| nothing else running | 4.32 | 6.81 | 7.82 | 15.26 | 7155 |
| 16 threads at NORMAL | 7.90 | 31.20 | 40.94 | 48.53 | 1929 |
| 16 threads at IDLE | 10.38 | 12.68 | 13.28 | 36.36 | 4281 |
| IDLE + 50% cap | 10.38 | 12.51 | 13.18 | 26.18 | 4391 |
| IDLE + 25% cap | 5.93 | 11.64 | 12.53 | 15.71 | 5701 |
| IDLE + 10% cap | 4.89 | 10.56 | 11.89 | 14.80 | 6452 |
| IDLE + 5% cap | 4.74 | 8.55 | 10.61 | 12.79 | 6725 |

Three things fall out of that table and all three shaped the defaults.

**Idle priority alone still cost 40 per cent of the foreground throughput** and
nearly tripled the median. Priority governs who gets a runnable slot; it says
nothing about memory bandwidth, and a background thread has already evicted the
foreground's cache lines by the time the scheduler preempts it. "Felt" is the
word the requirement used and this is felt.

**A 50 per cent cap on sixteen threads is no better than no cap at all**, because
eight streaming threads saturate the memory controller on their own. Halving a
number that was already past the knee buys nothing.

**The cap starts paying at 25 and is close to invisible at 10**, which is where
light use is set.

Idle priority is still worth having: without it, the same load costs the
foreground 73 per cent of its throughput and takes p99 to 40.94 ms. Both, not
either.

Priority is set through `JOB_OBJECT_LIMIT_PRIORITY_CLASS` rather than on the
process, so it covers children the controller has already spawned. It works
without `SeIncreaseBasePriorityPrivilege` because lowering a priority never
needed it, despite what the documentation for that flag implies.

## 3. Memory

Three mechanisms, and only two of them are usable.

**Commit caps kill the job.** `JOB_OBJECT_LIMIT_JOB_MEMORY` at 256 MiB against a
child allocating 512 MiB: the child died with exit code `0xC0000005`,
`STATUS_ACCESS_VIOLATION`, after reporting 128 MiB committed. Not a catchable
allocation failure, not a slowdown, and inside torch it would be a crash in the
middle of a segment. A limiter that kills the job is a different product from one
that squeezes it, so this is not used.

**A working set maximum trims the job, and it works.**
`JOB_OBJECT_LIMIT_WORKINGSET` with a 32 MiB minimum and a 192 MiB maximum,
against the same child committing 512 MiB:

    peak WORKING SET   191 MiB      held under the limit
    peak PRIVATE bytes 529 MiB      it still committed everything it asked for
    exit code          0            it finished

The job pages instead of the machine. That is the memory analogue of taking spare
cycles, and it is what light use uses.

**It needs a privilege, and it is not the one the documentation warns about.**
The docs attach `SE_INC_BASE_PRIORITY_NAME` to `JOB_OBJECT_LIMIT_PRIORITY_CLASS`.
Measured with that privilege removed from the token:

    CPU rate control, 10% hard cap : ok=True  err=0
    job limit IDLE_PRIORITY_CLASS  : ok=True  err=0
    job limit WORKINGSET           : ok=False err=1314  ERROR_PRIVILEGE_NOT_HELD
    Process.PriorityClass = Idle   : ok=True

The agent runs as SYSTEM from its boot task and has it. Somebody running the tray
by hand as an ordinary user does not, and gets the CPU cap and the priority
without the memory cap rather than nothing at all. The tray says so rather than
showing a number that is not in force.

**An admission check is the third mechanism and it is not optional.** A working
set cap contains a job that has already started; it cannot undo the moment where
a 4.7 GiB model loads onto a machine with 2 GiB spare and the owner's browser
goes to disk. Paging is felt in a way CPU contention is not: a throttled job
makes things slower, a machine that is swapping makes things stop.

`GlobalMemoryStatusEx` measured at **0.001 ms per call** over a thousand calls, so
it is free to poll on the fast loop. The honest field is `ullAvailPhys`, physical
memory the system can hand out without taking it off somebody. `ullAvailPageFile`
is the wrong number: a machine with a large page file has gigabytes of commit
headroom while already paging hard.

## 4. EcoQoS

`SetProcessInformation` with `ProcessPowerThrottling` and
`PROCESS_POWER_THROTTLING_EXECUTION_SPEED`. The call is **accepted** on this
desktop Ryzen and appears to do nothing useful: CPU per cent was unchanged, and
Microsoft's own rollout covered Intel mobile and Ryzen mobile parts with
efficiency cores rather than desktop parts without them.

It is not used. There is no efficiency class to move work to on this chip, and a
mechanism that cannot be measured cannot be promised.

## 5. Telling our own load apart from everybody else's

The GPU has a defect here that the CPU does not have to inherit.

`nvidia-smi` reports a whole card with no owner attached, so the moment a
controller puts a model on it the memory clock rises and the agent reads its own
load as somebody else at the machine. Measured before it was patched: four
restarts in a row on a locked desktop with nobody near it. The fix was to
**suppress** the GPU's load votes while a job runs, which throws away a real
signal to avoid a false one.

A job object accounts for every process assigned to it, children included, and
`QueryInformationJobObject` with `JobObjectBasicAccountingInformation` returns
that as exact CPU time. So:

    foreign CPU = machine CPU  -  sum of our job objects' CPU

is arithmetic rather than a guess, and the CPU signal keeps working **while a job
is running**, which is exactly when it matters, because that is when the owner
walks up to a machine that is already busy.

Machine CPU comes from `GetSystemTimes` rather than PDH, for two reasons that
both matter for something meant to be published. PDH counter paths are
**localised**: `\Processor Information(_Total)\% Processor Time` does not exist
on a German or Portuguese Windows. And one PDH counter sample was measured at
98 ms on this machine, which is why the GPU's counters live on the slow loop;
`GetSystemTimes` is a single syscall and belongs on the fast one, where the yield
decision is.

`GetSystemTimes` reports kernel time **inclusive of idle time**, which is the
trap in that API. Busy is `(kernel + user - idle) / (kernel + user)`.

## 6. Prior art

**BOINC** throttles by suspending and resuming the science application on a duty
cycle: "Use at most 75 per cent" means compute for three seconds, wait for one,
repeat, at ten-second granularity. Users complain about exactly what you would
expect. They expect a percentage to be a cap on cycles and it is not; a task
throttled to 50 per cent still takes the same CPU minutes and twice the wall
clock; and in 7.22.2 the busy-detection suspend was reported as firing "nearly
every minute". The lesson taken here: a duty cycle a person can perceive is worse
than a lower cap they cannot, and the kernel's own scheduling interval is far
finer than anything a user-mode supervisor can do with suspend and resume.

**Folding@home** pauses on idle detection and the forums are full of clients
stuck at "paused: waiting for idle" or folding when the user thought it would
not, including a report that browser GPU acceleration keeps the client believing
the card is busy. The lesson taken here: the detector has to be able to SAY what
it saw. That is what the Why submenu and the published verdict are for, and it is
why this agent keeps evaluating and logging even in Off mode.

Both projects also confirm the shape of the problem: everybody's answer is idle
priority plus a duty cycle, because that is all that is portable. On Windows
specifically there is a kernel-enforced rate cap that is strictly better, and it
has been there since Windows 8.

## 7. What is not used, and why

**Affinity masks.** `SetProcessAffinityMask`, `JOB_OBJECT_LIMIT_AFFINITY` and
cpusets are the horizontal answer: they say which cores, not how much. They also
fight the scheduler's cache-aware placement, which on a single-CCD X3D part with
one large shared L3 is worth more than the isolation is.

**Anything that looks like a cheat tool.** No injection, no hooking, no driver,
no elevation beyond what a documented API needs. Every mechanism above is a
documented Win32 call against a handle the agent owns, applied to a process the
agent started. Riot Vanguard is installed on the machine this was measured on.

## Sources

- [JOBOBJECT_CPU_RATE_CONTROL_INFORMATION](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_cpu_rate_control_information)
- [JOBOBJECT_BASIC_LIMIT_INFORMATION](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_limit_information)
- [JOBOBJECT_EXTENDED_LIMIT_INFORMATION](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_extended_limit_information)
- [SetInformationJobObject](https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-setinformationjobobject)
- [POWER_THROTTLING_PROCESS_STATE](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntddk/ns-ntddk-_power_throttling_process_state)
- [Introducing EcoQoS](https://devblogs.microsoft.com/performance-diagnostics/introducing-ecoqos/)
- [BOINC preferences: use at most N% CPU time](https://boinc.berkeley.edu/wiki/Preferences)
- [BOINC: suspending computation, CPU is busy](https://boinc.mundayweb.com/wiki/index.php?title=Suspending_computation_-_CPU_usage_is_too_high_%2F_CPU_is_busy)
- [Folding@home forum: GPU paused, waiting for idle](https://foldingforum.org/viewtopic.php?t=32555)
