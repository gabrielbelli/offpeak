#!/usr/bin/env python3
"""The controller side of the contract, in about two hundred lines.

USING THIS IS OPTIONAL. The protocol is directories and one line on stdin, so a
controller can be written in any language that can list a directory, rename a
file and read from standard input. This module exists because most people would
rather write the interesting part.

WHAT A CONTROLLER PROMISES
--------------------------

1. Publish a manifest. `service.json` in the queue directory says what this
   service can do. The agent serves it verbatim at GET /v1/services and has no
   other way of knowing. Written to a temp name and renamed, so a reader never
   sees half of it.

2. Claim work by rename. `pending/<job>.json` becomes `working/<job>.json`. If
   the process dies after that, the file is in working/ and the next start puts
   it back, which is what a lease expiry is when the queue is a directory.

3. Check the yield flag BETWEEN units of work, and never claim to check it
   during. This is the whole promise the runner makes to the person whose
   machine it is, and it has a hard floor: a unit of work usually cannot be
   interrupted from outside, so the honest thing is to declare how long one can
   be (`unit_seconds`) rather than to pretend otherwise.

4. Write artefacts first and the terminal record last. A client that sees
   `status: done` must find the bytes already there.

5. Exit when the queue has been empty for a while. There is one GPU and no
   preemption, so a controller that sits on it for ever starves every other
   service. Exiting hands it back; the agent starts this service again the moment
   a job arrives.

THE YIELD PATH, AND WHY IT IS TWO THINGS
----------------------------------------

A line reading YIELD on stdin means the user wants their GPU back. End of stdin
means the agent is gone, which is the same instruction arrived at differently:
the kernel closes the pipe when the agent's process object is torn down, so a
crashed agent cannot leave an orphan holding a CUDA context. Both set the same
event. The agent's job object is the third guarantee, and it is the one that
does not need this process to cooperate at all.
"""

import json
import os
import re
import sys
import threading
import time
from pathlib import Path

SAFE_NAME = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,199}$")

YIELD = threading.Event()
_LOG_LOCK = threading.Lock()


def log(msg, **kw):
    """One JSON object per line on stderr.

    The agent captures both of this process's output streams into
    logs/<service>.log inside the contained directory. Machine readable because
    the timing fields in here are the measurement, not decoration.
    """
    rec = {"t": round(time.time(), 3), "msg": msg}
    rec.update(kw)
    with _LOG_LOCK:
        sys.stderr.write(json.dumps(rec) + "\n")
        sys.stderr.flush()


def watch_stdin():
    """A YIELD line means stop. End of input means the agent is gone."""
    try:
        for line in sys.stdin:
            if line.strip().upper() == "YIELD":
                log("yield requested by the agent")
                YIELD.set()
                return
    except Exception as exc:                       # pragma: no cover - pipe teardown
        log("stdin watcher error", error=str(exc))
    log("stdin closed; the agent is gone")
    YIELD.set()


class Cancelled(Exception):
    """Raised by Job.check() when the client withdrew the job."""


class Yielded(Exception):
    """Raised by Job.check() when the machine's owner wants the GPU back."""


class Job:
    """One lease, and the only thing a handler is given."""

    def __init__(self, service, job_id, lease):
        self._service = service
        self.id = job_id
        self.lease = lease
        self.params = lease.get("params") or {}
        self.assets_dir = Path(lease.get("assets_dir") or "")
        self._artefacts = []

    # -- inputs ------------------------------------------------------------

    def asset_path(self, sha256):
        """Resolve a content addressed input the client uploaded first.

        The digest is the identity and any name attached to it is a label. That
        is what lets a client send a file once and reference it from a hundred
        jobs, and it is also why no filename a client chose ever reaches this
        machine's filesystem.
        """
        if not sha256 or not re.fullmatch(r"[0-9a-f]{64}", str(sha256)):
            raise ValueError("not a sha256 digest: %r" % (sha256,))
        p = self.assets_dir / sha256
        if not p.exists():
            raise FileNotFoundError(
                "asset %s is not held by this runner; upload it with POST /v1/assets first" % sha256)
        return p

    # -- the yield check ---------------------------------------------------

    def check(self):
        """Call this between units of work, and only between units of work."""
        if YIELD.is_set():
            raise Yielded()
        if self._service.cancel_flag(self.id):
            raise Cancelled()

    # -- outputs -----------------------------------------------------------

    def artefact(self, suffix, data):
        """Write one artefact and remember it for the terminal record.

        The name is always <job id>.<suffix>, never anything a client supplied.
        The agent will only serve a file whose name starts with the job id, so a
        controller cannot accidentally publish something that belongs to another
        job, or to no job at all.
        """
        if not SAFE_NAME.fullmatch(suffix):
            raise ValueError("unsafe artefact suffix: %r" % (suffix,))
        if suffix in ("done.json", "failed.json", "cancelled.json"):
            raise ValueError("that suffix is reserved for the terminal record")
        name = "%s.%s" % (self.id, suffix)
        path = self._service.done / name
        # Staged in working/ and renamed into done/, never written in place.
        # The agent lists done/<job>.* to answer "what came out of this job", so
        # a partly written file appearing there would be served to a client as a
        # truncated artefact.
        tmp = self._service.working / (name + ".part")
        if isinstance(data, (bytes, bytearray, memoryview)):
            tmp.write_bytes(bytes(data))
        else:
            tmp.write_text(str(data), encoding="utf-8")
        os.replace(tmp, path)
        self._artefacts.append(name)
        return path

    def progress(self, **fields):
        log("progress", job=self.id, **fields)


class Service:
    """The loop. Give it a manifest and a handler; it does the rest."""

    def __init__(self, queue_dir, manifest, handler,
                 idle_exit_seconds=30.0, poll_seconds=0.25):
        self.root = Path(queue_dir)
        self.pending = self.root / "pending"
        self.working = self.root / "working"
        self.done = self.root / "done"
        self.cancel = self.root / "cancel"
        self.manifest = dict(manifest)
        self.handler = handler
        self.idle_exit_seconds = idle_exit_seconds
        self.poll_seconds = poll_seconds

    # -- setup -------------------------------------------------------------

    def ensure_dirs(self):
        for d in (self.root, self.pending, self.working, self.done, self.cancel):
            d.mkdir(parents=True, exist_ok=True)

    def publish_manifest(self):
        """Fingerprint, then publish.

        The agent does not know what this service is until this file exists, and
        it never writes it. That is what makes adding a service a matter of
        writing a controller rather than of changing the runner: the generic
        layer learns the new capability from the new thing itself.
        """
        m = dict(self.manifest)
        m.setdefault("control", "queue")
        m.setdefault("interruptible", "between-units")
        m.setdefault("published_at", time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()))
        path = self.root / "service.json"
        tmp = self.root / "service.json.tmp"
        tmp.write_text(json.dumps(m, indent=2, sort_keys=True), encoding="utf-8")
        os.replace(tmp, path)

    def update_manifest(self, **fields):
        """Change what this service advertises, while it is running.

        This is how a declared capability becomes a MEASURED one. A controller
        can publish a conservative guess at startup and correct it once it has
        seen the machine do the work, and the agent picks the new value up on the
        next GET /v1/services because it re-reads the file every time rather than
        caching what it saw at launch.
        """
        self.manifest.update(fields)
        self.publish_manifest()

    def requeue_orphans(self):
        """Anything in working/ was interrupted. Put it back.

        This is the directory queue's version of a lease expiring, and it is why
        being killed mid-job costs a retry rather than a lost job.
        """
        n = 0
        for part in self.working.glob("*.part"):
            # A half written artefact from a run that was killed. It was staged
            # here rather than in done/ precisely so that it could be thrown away
            # without a client ever having seen it.
            try:
                part.unlink()
            except OSError:
                pass
        for leftover in sorted(self.working.glob("*.json")):
            try:
                os.replace(leftover, self.pending / leftover.name)
                n += 1
            except OSError:
                pass
        if n:
            log("requeued interrupted jobs", count=n)
        return n

    def cancel_flag(self, job_id):
        return (self.cancel / job_id).exists()

    def clear_cancel(self, job_id):
        try:
            (self.cancel / job_id).unlink()
        except OSError:
            pass

    # -- terminal records --------------------------------------------------

    def _finish(self, job_id, status, artefacts, extra=None):
        rec = {"job_id": job_id, "status": status, "artefacts": list(artefacts),
               "finished_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}
        if extra:
            rec.update(extra)
        # Artefacts are already on disk by the time this lands, and this lands by
        # rename. A client that sees "done" therefore always finds the bytes.
        path = self.done / ("%s.%s.json" % (job_id, status))
        tmp = self.working / ("%s.%s.json.part" % (job_id, status))
        tmp.write_text(json.dumps(rec, indent=2), encoding="utf-8")
        os.replace(tmp, path)

    # -- the loop ----------------------------------------------------------

    def run(self, once=False, watch_stdin_thread=True):
        self.ensure_dirs()
        self.publish_manifest()
        if watch_stdin_thread:
            threading.Thread(target=watch_stdin, daemon=True).start()
        self.requeue_orphans()

        served = failed = abandoned = 0
        idle_since = time.monotonic()

        while not YIELD.is_set():
            leases = sorted(self.pending.glob("*.json"))
            if not leases:
                if once:
                    break
                if self.idle_exit_seconds and \
                        time.monotonic() - idle_since > self.idle_exit_seconds:
                    # THE FAIRNESS MECHANISM, and it lives here rather than in the
                    # agent. One GPU means one controller and there is no
                    # preemption, so a controller that never exits starves every
                    # other service on the machine. Exiting when there is nothing
                    # to do costs a restart the next time work arrives and buys a
                    # scheduler that needs no preemption machinery at all.
                    log("idle; exiting so another service can have the GPU",
                        idle_seconds=round(self.idle_exit_seconds, 1))
                    break
                time.sleep(self.poll_seconds)
                continue

            idle_since = time.monotonic()
            lease_path = leases[0]
            job_id = lease_path.stem
            claimed = self.working / lease_path.name
            try:
                os.replace(lease_path, claimed)
            except OSError:
                continue                       # somebody else took it; fine

            try:
                lease = json.loads(claimed.read_text(encoding="utf-8"))
            except Exception as exc:
                log("unreadable lease", job=job_id, error=str(exc))
                claimed.unlink(missing_ok=True)
                self._finish(job_id, "failed", [], {"error": "the lease could not be parsed"})
                failed += 1
                continue

            job = Job(self, job_id, lease)

            # The check that matters, in one of the two places it can happen.
            if YIELD.is_set():
                os.replace(claimed, self.pending / lease_path.name)
                abandoned += 1
                break
            if self.cancel_flag(job_id):
                claimed.unlink(missing_ok=True)
                self.clear_cancel(job_id)
                self._finish(job_id, "cancelled", [], {"cancelled_before_start": True})
                continue

            t0 = time.monotonic()
            try:
                extra = self.handler(job) or {}
            except Yielded:
                # Computed work is thrown away and the lease goes back. Delivering
                # it would mean more disk and more bookkeeping at exactly the
                # moment somebody wants their frames.
                os.replace(claimed, self.pending / lease_path.name)
                abandoned += 1
                log("abandoned mid-job on yield", job=job_id,
                    seconds=round(time.monotonic() - t0, 3))
                break
            except Cancelled:
                claimed.unlink(missing_ok=True)
                self.clear_cancel(job_id)
                self._finish(job_id, "cancelled", job._artefacts)
                continue
            except Exception as exc:
                log("job failed", job=job_id, error=str(exc), kind=type(exc).__name__)
                claimed.unlink(missing_ok=True)
                self._finish(job_id, "failed", job._artefacts, {"error": str(exc)})
                failed += 1
                continue

            if YIELD.is_set():
                # Finished, but the user came back while we were inside the unit.
                # Hand it back rather than spend more of their frame time on it.
                os.replace(claimed, self.pending / lease_path.name)
                abandoned += 1
                log("discarded a finished job on yield", job=job_id)
                break

            extra.setdefault("compute_seconds", round(time.monotonic() - t0, 3))
            self._finish(job_id, "done", job._artefacts, extra)
            claimed.unlink(missing_ok=True)
            served += 1
            log("job done", job=job_id, **extra)

        self.requeue_orphans()
        log("controller exiting", served=served, failed=failed,
            abandoned=abandoned, yielded=YIELD.is_set())
        return served
