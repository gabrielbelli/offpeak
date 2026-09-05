#!/usr/bin/env python3
"""Speech synthesis on the GPU. The one real service, and the proof the contract works.

THIS IS THE ONLY FILE IN THE REPOSITORY THAT IMPORTS TORCH, and that is the
point. Nothing in the agent, the listener, the scheduler or the queue knows what
audio is. The words "voice", "text" and "sample rate" appear here and nowhere in
src/. Adding image generation or password recovery costs one more file like this
one and one more INI section, and touches no C#.

WHY THIS IS A SEPARATE PROCESS AND NOT A THREAD IN THE AGENT. Killing a process
is the only thing that reliably returns the CUDA context, which is a few hundred
megabytes, plus the weights, which are a few gigabytes, to the driver.
torch.cuda.empty_cache() frees cached blocks and not the context. The agent runs
this inside a Windows job object with KILL_ON_JOB_CLOSE, so the whole tree dies
together, including anything torch spawned, and dies even if the agent itself is
killed, because the kernel closes the handle when the agent's process object is
torn down.

THE HARD LIMIT, DECLARED HONESTLY IN THE MANIFEST. A speech model's generate()
has no interruption point inside it. So the real floor on getting out of the
user's way is one segment, and the manifest says so in `unit_seconds` rather than
claiming an interruptibility this cannot deliver. What this process CAN do is
refuse to start another segment and throw away the one in flight, which is
exactly what it does: audio only becomes real when the whole array is delivered,
so there is no partial write to corrupt.

WHAT CROSSES THE WIRE, AND WHAT DELIBERATELY DOES NOT. A client sends already
segmented text and gets back raw float32 samples per segment, at the model's own
rate, plus a sidecar of timings. It does NOT send a file path, because a path on
the client's machine means nothing here; a reference clip is uploaded once to
POST /v1/assets and referenced afterwards by its sha256. It does NOT ask for
encoded audio, because encoding is cheap, is the client's business, and pinning
byte-exact encoder output to whatever ffmpeg happens to be on a stranger's
Windows box is a promise nobody should make.
"""

import argparse
import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "lib"))

import idlegpu_service as svc                                   # noqa: E402

# 24000 is the multilingual Chatterbox model's own output rate, read from the
# model at load time below rather than assumed. This constant is only the value
# published in the manifest before the model has been loaded once.
DEFAULT_SAMPLE_RATE = 24000


def manifest(sample_rate, device, unit_seconds, vram_mib):
    return {
        "id": "chatterbox",
        "description": "Chatterbox multilingual text to speech, one segment per unit of work",
        "labels": ["speech", "tts", "chatterbox", "audio"],
        "control": "queue",
        # Between segments and no finer, because generate() cannot be interrupted
        # from outside. Saying "checkpoint" here would be a lie that costs
        # somebody their game.
        "interruptible": "between-units",
        "unit_seconds": unit_seconds,
        "vram_mib": vram_mib,
        "outputs": "audio/pcm-f32@%d" % sample_rate,
        "checkpointable": False,
        "device": device,
        "params_schema": {
            "segments": "list of strings, ALREADY SEGMENTED by the client; each one is one unit of work",
            "language": "language id understood by the model, default en",
            "reference_sha256": "optional, a voice reference clip previously uploaded to POST /v1/assets",
            "exaggeration": "number, default 0.5",
            "cfg_weight": "number, default 0.5",
            "temperature": "number, default 0.8",
        },
        "artefacts": {
            "<job>.<n>.f32": "raw little-endian float32 PCM for segment n, at the rate in `outputs`",
            "<job>.timings.json": "per segment compute time, sample count and realtime factor",
        },
    }


class Runtime:
    """Loads the model once, then answers segments until told to stop."""

    def __init__(self, device="cuda"):
        self.device = device
        self.model = None
        self.sample_rate = DEFAULT_SAMPLE_RATE
        self.load_seconds = None
        self.vram_mib = 0

    def load(self):
        t0 = time.monotonic()
        import torch
        from chatterbox.mtl_tts import ChatterboxMultilingualTTS

        if self.device == "cuda" and not torch.cuda.is_available():
            # Loud, not silent. Installing the CPU-only wheel is the most common
            # way this setup fails and it fails invisibly: everything imports,
            # everything runs, and the result is slower than whatever machine the
            # work was moved off.
            raise SystemExit(
                "FATAL: torch cannot see the GPU (%s). This is almost always the "
                "CPU-only wheel from PyPI instead of a CUDA wheel from "
                "download.pytorch.org. Re-run provision.ps1." % torch.__version__)

        svc.log("loading model", torch=torch.__version__, cuda=torch.version.cuda,
                device=torch.cuda.get_device_name(0) if self.device == "cuda" else "cpu")
        self.model = ChatterboxMultilingualTTS.from_pretrained(device=self.device)
        self.load_seconds = time.monotonic() - t0
        self.sample_rate = int(getattr(self.model, "sr", DEFAULT_SAMPLE_RATE))

        if self.device == "cuda":
            free, total = torch.cuda.mem_get_info()
            self.vram_mib = int((total - free) / 2 ** 20)
        # COLD START. Process spawn to model ready. This decides whether
        # abandon-and-reload is viable at all: if it is sixty seconds and real
        # idle windows are two minutes, a controller that yields correctly never
        # finishes anything.
        svc.log("model ready", load_seconds=round(self.load_seconds, 3),
                vram_used_mib=self.vram_mib, sample_rate=self.sample_rate)

    def speak(self, text, params):
        import numpy as np
        t0 = time.monotonic()
        wav = self.model.generate(
            text,
            language_id=params.get("language", "en"),
            audio_prompt_path=params.get("_reference_path") or None,
            exaggeration=params.get("exaggeration", 0.5),
            cfg_weight=params.get("cfg_weight", 0.5),
            temperature=params.get("temperature", 0.8),
        )
        compute = time.monotonic() - t0
        audio = wav.squeeze().detach().cpu().numpy().astype("<f4")
        return audio, compute


class FakeRuntime:
    """The same lifecycle and the same yield behaviour, with no torch and no GPU.

    WHY THIS EARNS ITS PLACE. It lets the whole path - the YIELD line, the
    end-of-stdin dead man's switch, the job object kill, the abandon bookkeeping
    and the latency measurement - be exercised on the real machine BEFORE anybody
    downloads several gigabytes, and re-run afterwards whenever that path changes.
    """

    def __init__(self, segment_seconds=3.0):
        self.device = "fake"
        self.sample_rate = DEFAULT_SAMPLE_RATE
        self.load_seconds = None
        self.vram_mib = 0
        self.segment_seconds = segment_seconds

    def load(self):
        t0 = time.monotonic()
        time.sleep(1.0)                      # stand-in for weights coming off disk
        self.load_seconds = time.monotonic() - t0
        svc.log("model ready", load_seconds=round(self.load_seconds, 3), fake=True)

    def speak(self, text, params):
        import array
        import math
        t0 = time.monotonic()
        # Deliberately uninterruptible, exactly like the real generate(). Nothing
        # in here checks the yield flag, because the real one cannot either.
        while time.monotonic() - t0 < self.segment_seconds:
            sum(math.sqrt(i) for i in range(20000))
        compute = time.monotonic() - t0
        n = int(self.sample_rate * self.segment_seconds)
        return array.array("f", (0.0 for _ in range(n))), compute


def build_handler(rt, service_ref):
    def handle(job):
        params = job.params
        segments = params.get("segments")
        if not isinstance(segments, list) or not segments:
            raise ValueError("params.segments must be a non-empty list of strings")

        # The reference clip, by digest. Never by path: the client's filesystem
        # does not exist here, and a digest is also a name that carries nobody's
        # private voice labels into a public repository.
        ref = params.get("reference_sha256")
        params = dict(params)
        params["_reference_path"] = str(job.asset_path(ref)) if ref else None

        timings = []
        total_samples = 0
        for i, text in enumerate(segments):
            # BETWEEN segments. The only place it can happen, and the manifest
            # says how long the gap between two of these can be.
            job.check()
            audio, compute = rt.speak(str(text), params)
            data = audio.tobytes() if hasattr(audio, "tobytes") else bytes(audio)
            n = len(data) // 4
            job.artefact("%d.f32" % i, data)
            secs = n / float(rt.sample_rate)
            timings.append({
                "segment": i,
                "compute_seconds": round(compute, 3),
                "audio_seconds": round(secs, 3),
                "realtime_factor": round(secs / compute, 3) if compute > 0 else None,
                "samples": n,
                "chars": len(str(text)),
            })
            total_samples += n
            job.progress(segment=i + 1, of=len(segments),
                         realtime_factor=timings[-1]["realtime_factor"])

            # unit_seconds is the number that decides whether this service can
            # live on somebody's gaming PC, because it is the floor on how long
            # after the polite request the machine can still be busy. Shipping a
            # guess for it is exactly the kind of unchecked promise this project
            # exists to avoid, so it is corrected upwards the first time reality
            # exceeds it, and republished for every client to see.
            service = service_ref[0]
            if service is not None and compute > service.manifest.get("unit_seconds", 0):
                service.update_manifest(unit_seconds=round(compute, 2),
                                        unit_seconds_source="measured on this machine")

        job.artefact("timings.json", json.dumps(
            {"sample_rate": rt.sample_rate, "segments": timings}, indent=2))
        return {
            "sample_rate": rt.sample_rate,
            "segments": len(segments),
            "samples": total_samples,
            "audio_seconds": round(total_samples / float(rt.sample_rate), 3),
        }
    return handle


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--queue", required=True, help="the service's queue directory")
    ap.add_argument("--device", default="cuda")
    ap.add_argument("--once", action="store_true", help="drain the queue and exit")
    ap.add_argument("--no-stdin-watch", action="store_true",
                    help="for running by hand, where stdin is a terminal")
    ap.add_argument("--fake-model", action="store_true",
                    help="exercise the whole path with no torch and no GPU")
    ap.add_argument("--segment-seconds", type=float, default=3.0,
                    help="how long a fake segment takes (default 3)")
    ap.add_argument("--idle-exit-seconds", type=float, default=60.0,
                    help="exit after this long with nothing to do, so another service can run")
    args = ap.parse_args()

    rt = FakeRuntime(args.segment_seconds) if args.fake_model else Runtime(args.device)
    # Loading before publishing means the manifest can carry MEASURED numbers -
    # the model's own sample rate and the VRAM it actually took - instead of
    # guesses. A capability advertisement that was never checked against the
    # machine is how you end up promising a card you do not have.
    rt.load()
    # A deliberately conservative starting value, corrected upwards by
    # measurement the first time a segment takes longer. It is not a claim about
    # any particular card.
    unit = args.segment_seconds if args.fake_model else 8.0
    m = manifest(rt.sample_rate, rt.device, unit, rt.vram_mib)
    m["unit_seconds_source"] = "unverified default until a segment has been timed"
    ref = [None]
    service = svc.Service(args.queue, m, build_handler(rt, ref),
                          idle_exit_seconds=args.idle_exit_seconds)
    ref[0] = service
    service.run(once=args.once, watch_stdin_thread=not args.no_stdin_watch)


if __name__ == "__main__":
    main()
