#!/usr/bin/env python3
"""Speech synthesis on the GPU. The one real service, and the proof the contract works.

TWO CHECKPOINTS, ONE FILE, SELECTED WITH --engine. `multilingual` is Chatterbox
in 23 languages with expression controls; `turbo` is Chatterbox Turbo, English
only with no expression controls at all, and 2.36x quicker on an RTX 3070. What
differs between them is a row in ENGINES below and nothing else: no branch in the
queue loop, in the yield check, in the thread retune or in speak(). See the
comment on ENGINES for why the missing controls are refused by name rather than
accepted and dropped.

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
import os
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "lib"))

import idlegpu_service as svc                                   # noqa: E402

# 24000 is what both checkpoints produce - they share S3GEN_SR - read from the
# model at load time below rather than assumed. This constant is only the value
# published in the manifest before the model has been loaded once.
DEFAULT_SAMPLE_RATE = 24000


def _rss_mib():
    """Resident set of this process, in MiB, with no third party dependency.

    GetProcessMemoryInfo through ctypes on Windows, /proc/self/statm elsewhere.
    psutil would be one line and one more package in a contained runtime that
    currently needs none.
    """
    try:
        if os.name == "nt":
            import ctypes
            from ctypes import wintypes

            class PMC(ctypes.Structure):
                _fields_ = [("cb", wintypes.DWORD),
                            ("PageFaultCount", wintypes.DWORD),
                            ("PeakWorkingSetSize", ctypes.c_size_t),
                            ("WorkingSetSize", ctypes.c_size_t),
                            ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
                            ("QuotaPagedPoolUsage", ctypes.c_size_t),
                            ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
                            ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
                            ("PagefileUsage", ctypes.c_size_t),
                            ("PeakPagefileUsage", ctypes.c_size_t)]

            pmc = PMC()
            pmc.cb = ctypes.sizeof(PMC)
            # THE CURRENT-PROCESS PSEUDO-HANDLE, WRITTEN OUT RATHER THAN CALLED
            # FOR. GetCurrentProcess returns (HANDLE)-1, and ctypes defaults every
            # return value to a 32 bit int; taking it back as a Python integer
            # gives 0xFFFFFFFFFFFFFFFF, which then fails to convert on the way
            # into the next call with "int too long". Measured: memory_mib was
            # published as 0 either way, silently, which would have told the
            # runner's admission check that a 6.5 GiB job needs nothing.
            proc = ctypes.windll.psapi.GetProcessMemoryInfo
            proc.argtypes = [ctypes.c_void_p, ctypes.POINTER(PMC), wintypes.DWORD]
            proc.restype = wintypes.BOOL
            if proc(ctypes.c_void_p(-1), ctypes.byref(pmc), pmc.cb):
                return int(pmc.WorkingSetSize / 2 ** 20)
            return 0
        with open("/proc/self/statm", "r") as fh:
            return int(int(fh.read().split()[1]) * os.sysconf("SC_PAGESIZE") / 2 ** 20)
    except Exception:
        return 0


# WHICH CHECKPOINT, AND WHAT IT CANNOT DO.
#
# ONE CONTROLLER, TWO ENGINES, AND NO SECOND FILE. tts_turbo.py ships inside the
# same chatterbox-tts wheel as mtl_tts.py, so a fork would be two copies of the
# queue plumbing, the yield check, the thread retune and the timing sidecar, kept
# in step by hand, to change an import and a keyword argument list.
#
# WHAT IS PER ENGINE IS DATA, NOT BRANCHES. Everything that differs between the
# two lives in this table: the class to import, the parameters generate() will
# actually honour, and how long a segment takes. speak() reads the table; nothing
# below asks "am I turbo".
#
# CONTROLS IS THE IMPORTANT COLUMN, and it is not a preference. Turbo's
# generate() ACCEPTS exaggeration and cfg_weight and then discards them with a
# logged warning, because the checkpoint sets hp.emotion_adv = False so the
# conditioning layer is never built and inference_turbo has no
# classifier-free-guidance path at all. It has no language_id parameter of any
# kind, so passing one is a TypeError rather than a fallback to English. A
# parameter that is accepted and dropped is a client believing something false
# about the audio it just received, so this file refuses by name what it cannot
# honour and passes nothing it was not asked for.
ENGINES = {
    "multilingual": {
        "id_suffix": "",
        "import": ("chatterbox.mtl_tts", "ChatterboxMultilingualTTS"),
        "description": "Chatterbox multilingual text to speech, one segment per unit of work",
        "labels": ["speech", "tts", "chatterbox", "audio"],
        # Everything generate() honours, and the default this controller uses when
        # the client does not say. `language` maps to the language_id argument.
        "controls": {
            "language": ("language_id", "en"),
            "exaggeration": ("exaggeration", 0.5),
            "cfg_weight": ("cfg_weight", 0.5),
            "temperature": ("temperature", 0.8),
        },
        # A deliberately conservative starting value per device, corrected upwards
        # the first time a real segment takes longer. Understating it is the
        # dangerous direction: unit_seconds is the floor on how long after a polite
        # request the machine can still be busy.
        "unit_seconds": {"cuda": 8.0, "cpu": 60.0},
        "min_reference_seconds": 0.0,
        # STRICT IS OFF HERE, AND THAT IS NOT A DOUBLE STANDARD - see the note on
        # turbo below. This service has been deployed for months against a client
        # that sends `sample_rate`, which this schema has never declared. Turning
        # strictness on for it today would fail every job in production to close a
        # hole that has never been exploited. The schema is corrected below so that
        # flipping this is one word whenever somebody wants to.
        "strict_params": False,
    },
    "turbo": {
        "id_suffix": "-turbo",
        "import": ("chatterbox.tts_turbo", "ChatterboxTurboTTS"),
        "description": "Chatterbox Turbo text to speech, English only, one segment per unit of work",
        "labels": ["speech", "tts", "chatterbox", "turbo", "audio"],
        # NO language, NO exaggeration, NO cfg_weight. Not omitted for brevity:
        # generate() cannot honour any of the three, and the whole point of this
        # table is that what it cannot honour it never receives.
        "controls": {
            "temperature": ("temperature", 0.8),
        },
        # 2.36x baseline measured on an RTX 3070, so a segment on the card takes
        # well under baseline's 8.0 - but this seeds a CEILING that is only ever
        # corrected UPWARDS, so it is set from the measured ratio rounded up rather
        # than from optimism. The CPU figure stays at baseline's 60.0 because turbo
        # on a processor has never been measured at all, and inventing a smaller
        # number for it would understate the floor to every client until the first
        # segment corrected it.
        "unit_seconds": {"cuda": 4.0, "cpu": 60.0},
        # Turbo asserts this itself inside prepare_conditionals, so a shorter clip
        # fails the job. Published so a client can refuse before submitting.
        "min_reference_seconds": 5.0,
        # ON, AND IT IS THE ONLY DEFENCE THAT SURVIVES A VERSION SKEW. This runner
        # is a separate program on somebody's desktop and can be pointed at by an
        # older server that still sends exaggeration, cfg_weight and language to
        # every speech service it knows. Refusing at the far end of the wire is
        # what stops that server being told "fine" and handed audio that ignored
        # three of the four things it asked for.
        "strict_params": True,
    },
}


def service_id(engine):
    """This service's id, from the agent, never a literal.

    THE DEFECT THIS PREVENTS, and it is the single most likely copy-paste in the
    whole exercise. Nothing reconciles the id in worker.ini with the id inside
    the manifest a controller publishes: the agent splices the manifest in
    verbatim under a key it labels with the section id. A controller copied to
    make a second engine, with "chatterbox" left in its manifest, publishes
        {"id":"chatterbox-turbo", ..., "manifest":{"id":"chatterbox"}}
    and every layer is satisfied. Anything routing on the manifest id - which is
    the id the CONTROLLER believes it is - then hands back the other engine, and
    the only symptom is audio in the wrong voice at the wrong speed.

    IDLEGPU_SERVICE_ID is set by the agent for every service it launches, so the
    id can only be wrong if worker.ini is wrong, which is one place instead of
    two. The fallback is for running this by hand outside the agent.
    """
    from_agent = os.getenv("IDLEGPU_SERVICE_ID", "").strip()
    if from_agent:
        return from_agent
    return "chatterbox" + ENGINES[engine]["id_suffix"]


def params_schema(engine):
    """Exactly the keys this engine honours, and nothing else.

    This is what `strict_params` is checked against, so a key described here is a
    key that reaches generate() and a key missing from here is one the job is
    refused for.
    """
    spec = ENGINES[engine]
    schema = {
        "segments": "list of strings, ALREADY SEGMENTED by the client; each one is one unit of work",
        "reference_sha256": "optional, a voice reference clip previously uploaded to POST /v1/assets",
        # DECLARED, NOT IGNORED. The client sends the rate it is going to assemble
        # the artefacts at; this controller checks it against the model's own rate
        # and fails the job when they disagree, rather than returning samples at a
        # rate nobody asked for. `outputs` in the manifest is the authority.
        "sample_rate": "optional, must match the rate in `outputs`; the job is refused if it does not",
    }
    # The prose for a control that is not a number, keyed by the wire name. Every
    # other control describes itself from its default.
    described = {"language": "language id understood by the model, default %s"}
    for name, (_arg, default) in sorted(spec["controls"].items()):
        schema[name] = described.get(name, "number, default %s") % (default,)
    return schema


def manifest(engine, sample_rate, device, unit_seconds, vram_mib, rss_mib=0):
    spec = ENGINES[engine]
    return {
        "id": service_id(engine),
        "engine": engine,
        "description": spec["description"],
        "labels": list(spec["labels"]),
        "control": "queue",
        # Between segments and no finer, because generate() cannot be interrupted
        # from outside. Saying "checkpoint" here would be a lie that costs
        # somebody their game.
        "interruptible": "between-units",
        "unit_seconds": unit_seconds,
        "vram_mib": vram_mib,
        # System memory this actually took, measured after the model loaded. The
        # runner's admission check reads it to decide whether starting this on a
        # machine somebody is using would push it into paging.
        "memory_mib": rss_mib,
        "outputs": "audio/pcm-f32@%d" % sample_rate,
        "checkpointable": False,
        "device": device,
        # THE HONEST SURFACE, PUBLISHED RATHER THAN DOCUMENTED. A client reading
        # this can tell before submitting that this engine has no language and no
        # expression controls, instead of finding out by receiving English.
        "params_schema": params_schema(engine),
        "min_reference_seconds": spec["min_reference_seconds"],
        "refuses_undeclared_params": spec["strict_params"],
        "artefacts": {
            "<job>.<n>.f32": "raw little-endian float32 PCM for segment n, at the rate in `outputs`",
            "<job>.timings.json": "per segment compute time, sample count and realtime factor",
        },
    }


class Runtime:
    """Loads the model once, then answers segments until told to stop."""

    def __init__(self, device="cuda", engine="multilingual"):
        # "auto" is the honest default for a runner that now sells both. It uses
        # the card when there is one and the CPU when there is not, and either way
        # the manifest says which, so a client can see what it is being given.
        # Naming a device that is not there used to be a SystemExit; it is now a
        # decision the machine makes and reports.
        self.device = device
        self.engine = engine
        self.spec = ENGINES[engine]
        self.resolved = device
        self.model = None
        self.sample_rate = DEFAULT_SAMPLE_RATE
        self.load_seconds = None
        self.vram_mib = 0
        self.rss_mib = 0
        # What torch has actually been told, so a THREADS line that changes
        # nothing does not churn the thread pool between every segment.
        self.threads = 0

    def load(self):
        t0 = time.monotonic()
        import importlib
        import torch

        module_name, class_name = self.spec["import"]
        model_class = getattr(importlib.import_module(module_name), class_name)

        if self.device == "auto":
            self.resolved = "cuda" if torch.cuda.is_available() else "cpu"
            svc.log("device chosen", device=self.resolved,
                    cuda_available=torch.cuda.is_available())
        else:
            self.resolved = self.device

        if self.resolved == "cuda" and not torch.cuda.is_available():
            # STILL LOUD WHEN THE GPU WAS ASKED FOR BY NAME. Installing the
            # CPU-only wheel is the most common way this setup fails and it fails
            # invisibly: everything imports, everything runs, and the result is
            # slower than whatever machine the work was moved off. Asking for
            # "cuda" and silently getting the CPU would be that failure with a
            # friendlier face. Ask for "auto" or "cpu" to run on the CPU on
            # purpose.
            raise SystemExit(
                "FATAL: torch cannot see the GPU (%s). This is almost always the "
                "CPU-only wheel from PyPI instead of a CUDA wheel from "
                "download.pytorch.org. Re-run provision.ps1, or set device = auto "
                "in worker.ini to fall back to the CPU." % torch.__version__)

        if self.resolved == "cpu":
            # DO NOT LET TORCH TAKE THE WHOLE MACHINE FROM INSIDE.
            #
            # torch defaults its intra-op thread pool to one thread per core, so on
            # a sixteen thread desktop a single generate() will happily use all
            # sixteen. The agent's job object cap is a HARD CAP: once the job has
            # spent its share of the scheduling interval no thread in it runs until
            # the next one, so sixteen threads under a ten per cent cap do not go
            # faster than four, they just spend more of their time descheduled and
            # thrash more cache on the way.
            #
            # IDLEGPU_CPU_THREADS is set by the agent from the cap in force. When
            # it is absent this leaves torch's own default alone, because a
            # controller run by hand on a spare machine should use it.
            n = os.getenv("IDLEGPU_CPU_THREADS", "").strip()
            if n.isdigit() and int(n) > 0:
                torch.set_num_threads(int(n))
                self.threads = int(n)
                svc.log("cpu threads", threads=int(n))

        if self.resolved == "cuda":
            # MATCH THE CPU'S NUMERICS, because the point of moving the work is
            # to make it faster and not to make it different.
            #
            # On Ampere and later, torch leaves torch.backends.cudnn.allow_tf32
            # TRUE by default. TF32 keeps float32's range and throws away most
            # of its mantissa: 10 explicit bits against 23. It is a good trade
            # for training and a questionable one for a convolutional vocoder,
            # which is what turns this model's tokens back into a waveform, and
            # the machine running the CPU copy has no such mode and never took
            # that trade.
            #
            # So the two backends were not running the same arithmetic, and the
            # one on the GPU was the lower-precision one. Both flags are set
            # explicitly rather than left to a default that has changed between
            # torch releases and differs between matmul and cudnn.
            #
            # THIS COSTS SPEED and that is the intended direction: quality first,
            # and TF32 is available to anyone who wants it back.
            allow = os.getenv("IDLEGPU_ALLOW_TF32", "0") not in ("0", "false", "no")
            torch.backends.cudnn.allow_tf32 = allow
            torch.backends.cuda.matmul.allow_tf32 = allow
            svc.log("precision", tf32=allow)

        svc.log("loading model", torch=torch.__version__, cuda=torch.version.cuda,
                device=torch.cuda.get_device_name(0) if self.resolved == "cuda" else "cpu")
        self.model = model_class.from_pretrained(device=self.resolved)
        self._assert_runtime(model_class)
        self.load_seconds = time.monotonic() - t0
        self.sample_rate = int(getattr(self.model, "sr", DEFAULT_SAMPLE_RATE))

        if self.resolved == "cuda":
            free, total = torch.cuda.mem_get_info()
            self.vram_mib = int((total - free) / 2 ** 20)
        else:
            # System memory, not VRAM, and it is the number the runner's admission
            # check needs: measured resident size decides whether starting this on
            # a machine somebody is using pushes it into paging, and paging is felt
            # in a way CPU contention is not.
            self.rss_mib = _rss_mib()
        # COLD START. Process spawn to model ready. This decides whether
        # abandon-and-reload is viable at all: if it is sixty seconds and real
        # idle windows are two minutes, a controller that yields correctly never
        # finishes anything.
        svc.log("model ready", load_seconds=round(self.load_seconds, 3),
                vram_used_mib=self.vram_mib, sample_rate=self.sample_rate)

    def _assert_runtime(self, model_class):
        """WHAT THE INSTALLED PACKAGE ACTUALLY DOES, checked against what this
        file claims about it, once, at load.

        THE DEFECT THIS PREVENTS is the table above going quietly out of date. A
        chatterbox-tts point release that gives turbo a language_id parameter, or
        a rebuilt checkpoint where hp.emotion_adv is True, would not break
        anything visibly: this controller would go on refusing a control the
        model had grown, or - far worse in the other direction - go on PASSING a
        control the model had lost, which generate() would accept and discard.
        Failing the whole process here costs one loud restart. Not checking costs
        one job at a time, silently, for as long as nobody listens closely.
        """
        import inspect

        try:
            takes = set(inspect.signature(model_class.generate).parameters)
        except (TypeError, ValueError):        # a C extension or a decorator
            svc.log("could not inspect generate(); the capability table is unchecked",
                    engine=self.engine)
            return

        declared = set(arg for _name, (arg, _default) in self.spec["controls"].items())
        missing = sorted(declared - takes)
        if missing:
            raise SystemExit(
                "FATAL: this build of %s.generate() does not take %s, but the engine "
                "table for '%s' says it does. Passing it would raise TypeError on "
                "every job. The installed chatterbox-tts is not the one this "
                "controller was written against."
                % (model_class.__name__, ", ".join(missing), self.engine))

        # THE OTHER DIRECTION, AND IT IS A WARNING RATHER THAN FATAL. A parameter
        # generate() grew that this table does not offer is a capability going
        # unsold, not a wrong answer, so it must not stop a machine that is
        # working. It is said out loud because the alternative is finding out a
        # year later.
        interesting = {"language_id", "exaggeration", "cfg_weight"}
        gained = sorted((interesting & takes) - declared)
        if gained:
            svc.log("this build of the model takes parameters this engine does not offer; "
                    "the engine table may be out of date",
                    engine=self.engine, parameters=gained)

        # hp.emotion_adv is the flag that decides whether the emotion conditioning
        # layer is built at all, and it is what makes exaggeration structurally
        # absent from turbo rather than merely unused.
        hp = getattr(getattr(self.model, "t3", None), "hp", None)
        emotion = getattr(hp, "emotion_adv", None)
        if emotion is not None:
            offers = "exaggeration" in self.spec["controls"]
            if bool(emotion) != offers:
                svc.log("the checkpoint disagrees with the engine table about expression",
                        engine=self.engine, emotion_adv=bool(emotion),
                        offers_exaggeration=offers)

    def _retune(self):
        """Take the agent's latest thread count, BETWEEN units of work.

        The cap the agent applies is a kernel hard cap and it binds in about
        200 ms whatever this process is doing; that is the promise and it does
        not depend on this method existing. This is the optimisation beside it:
        sixteen threads inside a tenth of a machine finish no sooner than two and
        evict far more of the owner's cache, so when the cap moves the thread
        count should move with it.

        NEVER DURING A generate(). torch's intra-op pool cannot be resized in the
        middle of a parallel region, so this is called at the top of a segment,
        exactly where the yield flag is checked and for the same reason.
        """
        if self.resolved != "cpu":
            return
        want = svc.wanted_threads()
        if want <= 0 or want == self.threads:
            return
        try:
            import torch
            torch.set_num_threads(want)
            self.threads = want
            svc.log("cpu threads changed", threads=want)
        except Exception as exc:
            svc.log("could not change the thread count", error=str(exc))

    def speak(self, text, params):
        self._retune()

        # BUILT FROM THE TABLE, SO A KEY THIS ENGINE CANNOT HONOUR CANNOT BE
        # TYPED HERE. Turbo receives no language_id, no exaggeration and no
        # cfg_weight - not None for them, ABSENT - because generate() would
        # accept two of the three and throw them away, and raise TypeError on the
        # third. There is no `if engine == "turbo"` anywhere in this method, and
        # a third engine is a row in ENGINES rather than a branch.
        kwargs = {"audio_prompt_path": params.get("_reference_path") or None}
        for name, (arg, default) in self.spec["controls"].items():
            value = params.get(name)
            kwargs[arg] = default if value is None else value

        t0 = time.monotonic()
        wav = self.model.generate(text, **kwargs)
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

    def __init__(self, segment_seconds=3.0, engine="multilingual"):
        self.device = "fake"
        self.engine = engine
        self.spec = ENGINES[engine]
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


def check_params(rt, params):
    """Refuse, by name, anything this engine cannot honour.

    THE LAST LINE OF DEFENCE, AND THE ONLY ONE THAT SURVIVES A VERSION SKEW.
    The server that submits these jobs runs somewhere else, is upgraded on its
    own schedule and can be older than this file. An older server sends
    exaggeration, cfg_weight and language to every speech service it knows
    about, because until turbo existed every speech service honoured all three.

    If this controller quietly ignored them, that server would be told the job
    succeeded and would hand its caller English audio with none of the delivery
    it asked for, with no error anywhere in the stack. Refusing here turns a
    silent wrong answer into a failed job with a sentence in it, and a failed
    job is something somebody fixes.

    Named rather than counted: "unsupported parameter 'exaggeration'" says which
    field and which engine, so the fix is obvious from the message alone.
    """
    schema = params_schema(rt.engine)
    if rt.spec["strict_params"]:
        for name in sorted(params):
            # A leading underscore is this controller's own working state - the
            # resolved reference path - and never came off the wire.
            if name.startswith("_") or name in schema:
                continue
            raise ValueError(
                "unsupported parameter %r: %s has no such control. It honours: %s."
                % (name, service_id(rt.engine), ", ".join(sorted(schema))))

    # DECLARED MEANS CHECKED. sample_rate is in every engine's schema, so a
    # client that names a rate this model does not produce is told so rather than
    # handed samples at a different rate and left to assemble them wrongly.
    want = params.get("sample_rate")
    if want is not None and int(want) != int(rt.sample_rate):
        raise ValueError(
            "sample_rate %s was asked for and this model produces %d; the rate is a "
            "property of the checkpoint and cannot be changed here. See `outputs` in "
            "the manifest." % (want, rt.sample_rate))


def build_handler(rt, service_ref):
    def handle(job):
        params = job.params
        # BEFORE ANY WORK. A job that is going to be refused must be refused
        # while its client is still holding the submit, not after two minutes on
        # a card somebody wanted back.
        check_params(rt, params)
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
    ap.add_argument("--device", default="auto",
                    help="cuda, cpu, or auto (default): use the card if there is one")
    ap.add_argument("--engine", default="multilingual", choices=sorted(ENGINES),
                    help="which checkpoint: multilingual (23 languages, expression "
                         "controls) or turbo (English only, no expression, 2.36x "
                         "quicker on a 3070)")
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

    rt = (FakeRuntime(args.segment_seconds, args.engine) if args.fake_model
          else Runtime(args.device, args.engine))
    # Loading before publishing means the manifest can carry MEASURED numbers -
    # the model's own sample rate and the VRAM it actually took - instead of
    # guesses. A capability advertisement that was never checked against the
    # machine is how you end up promising a card you do not have.
    rt.load()
    # A deliberately conservative starting value, corrected upwards by
    # measurement the first time a segment takes longer. It is not a claim about
    # any particular card.
    # A CPU run is far slower per segment than a CUDA one, and unit_seconds is
    # the floor on how long after a polite request the machine can still be busy.
    # Starting a CPU run with the GPU's number would understate that floor to
    # every client until the first segment corrected it upwards.
    resolved = getattr(rt, "resolved", rt.device)
    if args.fake_model:
        unit = args.segment_seconds
    else:
        # PER ENGINE AND PER DEVICE, from the table. A turbo run seeded with
        # baseline's number overstates the floor to every client until the first
        # segment; a CPU run seeded with the card's number understates it, which
        # is the direction that costs somebody their game.
        seeds = ENGINES[args.engine]["unit_seconds"]
        unit = seeds["cpu"] if resolved == "cpu" else seeds["cuda"]
    m = manifest(args.engine, rt.sample_rate, resolved, unit, rt.vram_mib,
                 getattr(rt, "rss_mib", 0))
    m["unit_seconds_source"] = "unverified default until a segment has been timed"
    ref = [None]
    service = svc.Service(args.queue, m, build_handler(rt, ref),
                          idle_exit_seconds=args.idle_exit_seconds)
    ref[0] = service
    service.run(once=args.once, watch_stdin_thread=not args.no_stdin_watch)


if __name__ == "__main__":
    main()
