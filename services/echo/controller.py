#!/usr/bin/env python3
"""A service that needs nothing: no GPU, no torch, no network, no install.

WHY THIS SHIPS. Three reasons, and none of them is that echoing text is useful.

1. It is the proof that the contract is generic. This file and one INI section
   are the entire cost of adding a service. No C# was recompiled to make the
   runner able to do this, and the runner still does not know what "echo" means:
   it learned the labels, the output type and the interruptibility from the
   manifest this file publishes.

2. It makes the whole yield path testable on any machine, before anybody
   downloads several gigabytes of model weights. Submit a job with
   {"seconds": 10}, start a full screen window, and watch the agent take the GPU
   back and the job go straight back to queued. The expensive part of this system
   is not the part most likely to be wrong.

3. It burns CPU rather than sleeping, so "the controller was busy and had to be
   killed" is a real state rather than a timer that was always going to fire.
   Pass {"stubborn": true} to make it ignore the yield flag entirely, which is
   how the job object backstop gets exercised.

Run it by hand:

    python controller.py --queue C:\\path\\to\\queues\\echo --no-stdin-watch
"""

import argparse
import math
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "lib"))

import offpeak_service as svc                                   # noqa: E402

# The capability advertisement. The agent serves this verbatim at
# GET /v1/services and never inspects it, so the vocabulary is for clients:
#
#   control        queue | cli | port     how work reaches this service
#   interruptible  none | between-units | checkpoint
#   unit_seconds   the WORST CASE uninterruptible span. This is the number that
#                  decides whether a service can live on somebody's gaming PC at
#                  all, because it is the floor on how fast the machine can be
#                  handed back after the polite request is made.
#   vram_mib       roughly what it holds while running
#   outputs        a mime-ish string; the runner has no opinion about it
MANIFEST = {
    "id": "echo",
    "description": "burns CPU for a requested number of seconds and writes the text back",
    "labels": ["test", "cpu", "no-gpu"],
    "control": "queue",
    "interruptible": "between-units",
    "unit_seconds": 1.0,
    "vram_mib": 0,
    "outputs": "text",
    "checkpointable": False,
    "params_schema": {
        "text": "string, echoed back as the artefact",
        "seconds": "number, how long to burn CPU (default 3)",
        "units": "integer, how many chunks to split that into (default 3)",
        "stubborn": "boolean, ignore the yield flag so the job object has to kill this",
    },
}


def handle(job):
    text = str(job.params.get("text", ""))
    seconds = float(job.params.get("seconds", 3.0))
    units = max(1, int(job.params.get("units", 3)))
    stubborn = bool(job.params.get("stubborn", False))

    per_unit = seconds / units
    for i in range(units):
        # BETWEEN units, never inside one. That is the whole promise, and the
        # manifest above declares what "between" costs in seconds.
        if not stubborn:
            job.check()
        t0 = time.monotonic()
        while time.monotonic() - t0 < per_unit:
            sum(math.sqrt(n) for n in range(20000))
        job.progress(unit=i + 1, of=units)

    job.artefact("txt", text + "\n")
    return {"units": units, "chars": len(text)}


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--queue", required=True, help="the service's queue directory")
    ap.add_argument("--once", action="store_true", help="drain the queue and exit")
    ap.add_argument("--no-stdin-watch", action="store_true",
                    help="for running by hand, where stdin is a terminal")
    ap.add_argument("--idle-exit-seconds", type=float, default=30.0,
                    help="exit after this long with nothing to do, so another service can run")
    args = ap.parse_args()

    service = svc.Service(args.queue, MANIFEST, handle,
                          idle_exit_seconds=args.idle_exit_seconds)
    service.run(once=args.once, watch_stdin_thread=not args.no_stdin_watch)


if __name__ == "__main__":
    main()
