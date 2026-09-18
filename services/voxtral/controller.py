#!/usr/bin/env python3
"""Voxtral-4B-TTS int4 on the GPU. Twenty fixed voices, nine languages, no cloning.

WHY THIS IS A SECOND FILE AND NOT A THIRD ROW IN chatterbox/controller.py. The
two Chatterbox engines share a file because they share a wheel, a virtual
environment and a torch. This shares none of them: chatterbox-tts 0.1.7 pins
torch==2.6.0 and the int4 path here needs 2.14.0, so the two cannot live in one
environment, and `src/Install.cs:325` derives UV_PROJECT_ENVIRONMENT from
InstallDir with no per-service override - one tree is one venv. A row in
chatterbox's ENGINES table would be a row that could never be loaded.

IT IS NOT A CLONING ENGINE, AND EVERYTHING BELOW TURNS ON THAT. Twenty speaker
embeddings ship inside the checkpoint as .pt tensors; there is no speaker encoder
anywhere in it and no reference-audio parameter in any of the wrapper's nine
source files. So there is no `reference_sha256` in the schema, `voice` is
REQUIRED rather than defaulted, and a job that sends a clip digest is refused by
name rather than being spoken in whichever voice the runner felt like.

WHY THE FRAME LOOP IS VENDORED OUT OF generate_speech_fast, in four parts, and
the first two are each sufficient on their own:

 1. THE YIELD CHECK. Upstream's loop is `for frame_idx in range(max_frames)`
    with no way in, and at these settings it runs at about 1.35 frames a second.
    Called as shipped, one utterance is a single uninterruptible unit of three
    and a half minutes: the owner sits down, YieldGraceSeconds expires, the job
    object kills the process, and every second of that GPU time is thrown away -
    every time. Owning the loop makes the unit ONE FRAME, about 740 ms.
 2. THE AUDIO THE OWNER ACTUALLY CHOSE. postprocess_audio applies a 6th-order
    Butterworth at 10 kHz, which the owner heard immediately and called a pilot
    mic; it also resamples 24000 -> 48000 while three of the repository's four
    callers then write the result at 24000, which is the half-speed bug in the
    box. Neither is something to monkey-patch into somebody else's module from
    a distance: the patch would be invisible to anyone reading either file.
 3. FAILURES STOP BEING SWALLOWED. generate_fast.py:189 catches RuntimeError
    INSIDE the loop and breaks, returning partial audio as a success, and :216
    returns one sample of silence, also as a success. A CUDA out-of-memory in
    the middle of a job is a RuntimeError. Here it propagates and fails the job.
 4. FRAME COUNTS ARE RECORDED. `frames` and `frame_rate` in the timings sidecar
    are what lets a caller assert that the audio it got came off a 12.5 Hz grid,
    which nothing else on this stack produces.

THE SAMPLE RATE IS 24000 AND IT IS A WIRE CONTRACT, not a preference. The codec
produces 24 kHz natively: 240 samples per patch, three stride-2 upsamples, so
1920 samples per frame and 12.5 frames a second. The 48 kHz that appears
everywhere upstream exists ONLY because postprocess_audio resamples on its way
past. This controller does not resample, so there is nothing to get wrong, and
the geometry is asserted against the decoded array on every segment.

RUN WITH -X utf8, AND IT IS LOAD-BEARING. See _assert_utf8_mode.
"""

import argparse
import json
import os
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "lib"))

import idlegpu_service as svc                                   # noqa: E402

# The id this controller answers to when it is run BY HAND, outside the agent.
# Under the agent it comes from IDLEGPU_SERVICE_ID and this is never read; see
# service_id() for the copy-paste defect that arrangement exists to prevent.
DEFAULT_SERVICE_ID = "voxtral"

# WHAT THE CODEC PRODUCES, DERIVED ONCE AND THEN CHECKED AGAINST THE MODEL.
#
# CodecDecoder projects each frame to `patch_size` = 240 samples and passes it
# through three stride-2 transposed convolutions, so one frame is 1920 samples
# and 24000 / 1920 is 12.5 frames a second. These three numbers are stated here
# because the sidecar publishes them and a client asserts on them; _check_codec_
# geometry() re-derives them from the loaded checkpoint's own config and fails
# the load if a future checkpoint disagrees. A frame rate that is published and
# wrong is worse than one that is absent.
NATIVE_SAMPLE_RATE = 24000
SAMPLES_PER_FRAME = 1920
FRAME_RATE = NATIVE_SAMPLE_RATE / float(SAMPLES_PER_FRAME)      # 12.5

# HOW OFTEN A RUNNING JOB SAYS SOMETHING. Every frame would be about 1.35 lines
# a second for three and a half minutes; every 25 frames is one line per two
# seconds of audio, which is roughly one every twenty seconds of compute.
PROGRESS_FRAMES = 25

# WHAT A CALLER MAY SET PER JOB, WITH THE BOUNDS IT IS LEGAL WITHIN.
#
# THE RULE THAT DECIDES WHAT IS IN HERE: a knob is caller-settable if and only
# if changing it costs nothing but the current job. These two are read inside
# the per-frame solver and cost exactly one job to change. group_size is
# consumed once inside a 63-second load; max_frames sizes the rotary table and
# is the ceiling the model is allowed to run to; fade_ms and low_pass_hz shape
# the artefact rather than the generation and must be identical across every
# segment of a listening comparison. All four of those are install-time flags,
# published in `settings`, and refused BY NAME if they arrive in a job body.
#
# THE BOUNDS MUST MATCH voice_common.engines.CONTROL_RANGES ON THE SERVER, and
# they are duplicated here rather than imported because this program runs on
# somebody else's machine and cannot import that package. The far end of a wire
# is the only place a version skew can be caught: an older server that has
# never heard of these bounds is exactly what this check is for.
CONTROLS = {
    "flow_steps": {
        "kind": int,
        "min": 1,
        "max": 64,
        "prose": ("integer 1-64, default %s. Steps in the flow-matching solver "
                  "and THE MAIN QUALITY KNOB; the cost is linear in it. The "
                  "upstream repository defaults to 8."),
    },
    "cfg_alpha": {
        "kind": float,
        "min": 1.0,
        "max": 3.0,
        "prose": ("number 1.0-3.0, default %s. Classifier-free guidance "
                  "strength; 1.2 is full CFG and 1.0 is documented upstream as "
                  "faster but garbled, which is why the floor is 1.0 and not 0."),
    },
}

# SETTINGS THAT ARE FIXED WHEN THE PROCESS STARTS, AND REFUSED BY NAME WHEN A
# JOB ASKS FOR THEM. The house rule cuts both ways: a field this controller
# cannot honour FOR THIS JOB must not be swallowed just because it is honourable
# at load. Each entry is the sentence a caller gets back.
LOAD_TIME_SETTINGS = {
    "group_size": "the int4 quantisation group size, consumed once inside a "
                  "63-second model load",
    "max_frames": "the per-segment frame ceiling, which sizes the rotary table "
                  "when the model loads",
    "fade_ms": "artefact policy, and it must be identical across every segment "
               "of a listening comparison",
    "low_pass_hz": "artefact policy, and it must be identical across every "
                   "segment of a listening comparison",
    "peak": "artefact policy, and it must be identical across every segment of "
            "a listening comparison",
}

# A CONSERVATIVE STARTING VALUE PER FACT, MEASURED ON AN RTX 3070 8 GB AND
# CORRECTED BY THIS MACHINE THE FIRST TIME IT DOES THE WORK. None of these is a
# claim about anybody else's card; they exist so that the first client to read
# the manifest gets a number rather than a null, and every one of them is
# republished from measurement as soon as there is a measurement to publish.
SEEDS = {
    # Process start to model ready. 63 s, of which 28.8 s is the quantisation.
    # It decides whether abandon-and-reload is viable at all, which is why
    # worker.ini gives this service a ten-minute idle window and not the
    # default one.
    "cold_load_seconds": 63.0,
    # 8265 MiB PEAK ON AN 8192 MiB CARD, and it survived once with 455 MiB of
    # desktop resident. load_model_int4 puts the whole BF16 model on the device
    # before it quantises anything, so the peak is the BF16 size and not the
    # int4 one. This is why --min-free-vram-mib exists.
    "load_peak_vram_mib": 8265,
    # One frame at flow_steps=32, cfg_alpha=1.2: 0.104x realtime against a
    # 12.5 Hz grid is about 740 ms. unit_seconds is only ever corrected
    # UPWARDS, so this is rounded up.
    "unit_seconds": 1.0,
}


def _add_wrapper_to_path(wrapper_dir):
    """Make the third-party wrapper importable, and put it LAST.

    The modules in there are called model, generate, load_model and serve, which
    are four of the most collidable names anybody has ever chosen. Inserting the
    directory at the front of sys.path would let any of them shadow a package
    this environment installed - torch's own `serve` would be the amusing one -
    and the failure would be an AttributeError somewhere unrelated. Appending
    means they are found only because nothing else provides them, which is true
    today and is checked by the fact that the load works at all.

    Vendored source rather than a pip install because there is no distribution:
    voxtral-int4 is a repository of scripts with no packaging of any kind, so
    provision.ps1 unpacks a PINNED commit and this points at it.
    """
    if wrapper_dir and wrapper_dir not in sys.path:
        sys.path.append(wrapper_dir)


def _assert_utf8_mode():
    """PYTHONUTF8, ASSERTED, BECAUSE THE FAILURE IS OTHERWISE FOUR FRAMES DEEP.

    The wrapper's generate.py opens tekken.json with a bare `open(path)`. On
    Windows that means cp1252, and tekken.json is 14.9 MB of base64 and Unicode,
    so the tokeniser dies inside json.load with a UnicodeDecodeError that names
    a byte offset and nothing else. Measured on the test machine: without
    -X utf8 the tokeniser cannot be constructed at all; with it, it encodes.

    worker.ini passes "-X" "utf8" in Arguments because there is no per-service
    environment key and adding one would mean recompiling the agent to add a
    service - the one thing this design promises never to need. So the flag can
    be lost by an edit to one line of INI, and this turns that into a sentence
    at start rather than a stack trace inside tiktoken at the first job.
    """
    if not sys.flags.utf8_mode:
        raise SystemExit(
            "FATAL: this controller must run in UTF-8 mode. The Voxtral wrapper "
            "opens tekken.json with no encoding, so Windows uses cp1252 and the "
            "tokeniser dies on a 14.9 MB file of base64 and Unicode. Launch it "
            'with -X utf8 (worker.ini: Arguments = "-X" "utf8" "...controller.py" '
            "...), or set PYTHONUTF8=1.")


def service_id():
    """This service's id, from the agent, never a literal.

    THE DEFECT THIS PREVENTS. Nothing reconciles the id in worker.ini with the
    id inside the manifest a controller publishes: the agent splices the
    manifest in verbatim under a key it labels with the section id. A controller
    copied to make another service, with the first one's id left in, publishes
        {"id":"voxtral", ..., "manifest":{"id":"chatterbox"}}
    and every layer is satisfied, while anything routing on the manifest id
    hands back the other engine - and the only symptom is audio in the wrong
    voice at the wrong speed. The fallback is for running this by hand.
    """
    from_agent = os.getenv("IDLEGPU_SERVICE_ID", "").strip()
    return from_agent or DEFAULT_SERVICE_ID


def voice_names(voice_dir):
    """The speakers this checkpoint actually carries, off the disk.

    READ, NOT LISTED IN THIS FILE, and that is the whole point. The twenty names
    are a property of the weights somebody downloaded, so a hard-coded list here
    would be a second copy of a fact that a reinstall or a checkpoint revision
    can change underneath it - and the first symptom would be a voice offered in
    the picker that fails on submission. Listing a directory needs no model, no
    CUDA and no 63-second load, so this is answerable while the manifest is
    being published.
    """
    try:
        return sorted(p.stem for p in Path(voice_dir).glob("*.pt"))
    except OSError:
        return []


def params_schema(voices, defaults):
    """Exactly the keys this service honours, and nothing else.

    `strict_params` is checked against this, so a key described here is a key
    that reaches the model and a key missing from here is one the job is refused
    for. `voice` has no default on purpose: this checkpoint has twenty speakers
    and no way to derive one from the text, so a runner-chosen default would be
    audio in a voice nobody asked for, delivered as a success.
    """
    listed = ", ".join(voices) if voices else "(none found on this machine)"
    schema = {
        "segments": "list of strings, ALREADY SEGMENTED by the client; each one "
                    "is one utterance and one unit of delivery",
        "voice": "REQUIRED, one of this checkpoint's fixed speaker embeddings: "
                 "%s. There is no speaker encoder in these weights, so a "
                 "reference clip cannot become a voice here." % listed,
        "sample_rate": "optional, must match the rate in `outputs`; the job is "
                       "refused if it does not",
    }
    for name, spec in sorted(CONTROLS.items()):
        schema[name] = spec["prose"] % (defaults.get(name),)
    return schema


def manifest(voices, settings, defaults, sample_rate, device, unit_seconds,
             vram_mib, seeds=None, unavailable=None):
    seeds = dict(SEEDS if seeds is None else seeds)
    m = {
        "id": service_id(),
        "description": "Voxtral-4B-TTS int4: twenty fixed preset voices, nine "
                       "languages, one utterance per unit of delivery",
        "labels": ["speech", "tts", "audio", "preset-voice"],
        "control": "queue",
        # Between FRAMES, which is why the loop is vendored. Saying "checkpoint"
        # would be a lie that costs somebody their game; saying "none" would be
        # true of the shipped call and is exactly what this file exists not to
        # ship.
        "interruptible": "between-units",
        "unit_seconds": unit_seconds,
        "vram_mib": vram_mib,
        "outputs": "audio/pcm-f32@%d" % sample_rate,
        "checkpointable": False,
        "device": device,
        # THE HONEST SURFACE, PUBLISHED RATHER THAN DOCUMENTED. A client reading
        # this can tell before submitting that this engine has no expression
        # controls, no language parameter and no reference clip, instead of
        # finding out by receiving something else.
        "params_schema": params_schema(voices, defaults),
        # NOT A CLONING ENGINE, SAID AS A FACT OF ITS OWN. min_reference_seconds
        # is a floor on a clip, and 0.0 already means "no minimum" for an engine
        # that does take one - it cannot also mean "there is no speaker encoder
        # here at all". A client that reads only the floor would offer a clip
        # upload for a checkpoint that has nowhere to put it.
        "reference_audio": False,
        "min_reference_seconds": 0,
        "voices": list(voices),
        # THE 24000/48000 TRAP, CLOSED AT BOTH ENDS. Published here, checked
        # against the model at load, checked against the caller's declared rate
        # in check_params, and asserted against the decoded array on every
        # segment. Audio that has been through the wrapper's postprocess is
        # 48 kHz; this never has been.
        "native_sample_rate": sample_rate,
        "frame_rate": FRAME_RATE,
        "cold_load_seconds": seeds["cold_load_seconds"],
        "load_peak_vram_mib": seeds["load_peak_vram_mib"],
        # ON, AND IT IS THE ONLY DEFENCE THAT SURVIVES A VERSION SKEW. This
        # runner is a separate program on somebody's desktop and can be pointed
        # at by an older server that still sends exaggeration, cfg_weight,
        # temperature and language to every speech service it knows about.
        # Refusing at the far end of the wire is what stops that server being
        # told "fine" and handed audio that ignored four of the things it asked
        # for.
        "refuses_undeclared_params": True,
        # WHAT THIS INSTALL IS ACTUALLY SERVING, so that "what is spring running"
        # is answerable with curl instead of an SSH session that lands in
        # session 0 and can start nothing. These are the flags in worker.ini,
        # echoed back.
        "settings": dict(settings),
        "artefacts": {
            "<job>.<n>.f32": "raw little-endian float32 PCM for segment n, at "
                             "the rate in `outputs`",
            "<job>.timings.json": "per segment compute time, frame count, frame "
                                  "rate, peak level and realtime factor",
        },
    }
    if unavailable:
        # SAID OUT LOUD RATHER THAN DISCOVERED PER JOB. A machine whose torch
        # cannot see the card will fail every job with this same sentence; a
        # client can read it here and stop submitting.
        m["unavailable"] = unavailable
    return m


class Runtime:
    """Loads the model on the first job, then answers utterances until told to stop.

    LAZY, AND IT IS A DELIBERATE DEPARTURE FROM chatterbox/controller.py, which
    loads before it publishes so that its manifest can carry measured numbers.
    The reason is the margin: this checkpoint peaks at 8265 MiB on an 8192 MiB
    card, having survived once with 455 MiB of desktop resident - 73 MiB of
    room. Loading before publishing means an out-of-memory kills the process
    BEFORE service.json exists, the lease stays in pending/, and the agent's
    scheduler - which reads no exit code, has no backoff and has no failure
    counter - relaunches it every 500 ms for ever while the client polls
    `queued`. Loud in logs\\, invisible everywhere else. Failing the JOB instead
    of the PROCESS turns a silent infinite loop into one terminal record with a
    sentence in it.
    """

    def __init__(self, model_dir, wrapper_dir, device="cuda", group_size=32,
                 max_frames=2000, min_free_vram_mib=8400, unavailable=None):
        self.model_dir = str(model_dir)
        self.wrapper_dir = str(wrapper_dir)
        self.voice_dir = str(Path(model_dir) / "voice_embedding")
        self.device = device
        self.group_size = group_size
        self.max_frames = max_frames
        self.min_free_vram_mib = min_free_vram_mib
        # WHY THIS MACHINE CANNOT RUN IT, OR None. Published in the manifest so a
        # client can stop submitting, and raised HERE so a job that was submitted
        # anyway fails with the same sentence rather than with whatever torch
        # says when it is asked for a device it cannot see.
        self.unavailable = unavailable
        self.model = None
        self.tokenizer = None
        self.sample_rate = NATIVE_SAMPLE_RATE
        self.frame_rate = FRAME_RATE
        self.samples_per_frame = SAMPLES_PER_FRAME
        self.load_seconds = None
        self.vram_mib = 0
        self.peak_vram_mib = 0

    # -- loading -----------------------------------------------------------

    def ensure_loaded(self):
        """Load once, and refuse the job rather than the process if it will not fit."""
        if self.model is not None:
            return
        if self.unavailable:
            raise RuntimeError(self.unavailable)
        # BEFORE A 63-SECOND LOAD THAT NOTHING CAN INTERRUPT. Everywhere else in
        # this file the yield check is between frames; the load is the one span
        # that cannot be broken up, so the honest thing is not to START one when
        # the owner is already back. Raising Yielded puts the lease back in
        # pending/ and the job runs when they leave.
        if svc.YIELD.is_set():
            raise svc.Yielded()
        import torch

        # BEFORE ANYTHING IS ALLOCATED. Nothing in the agent checks VRAM on our
        # behalf: MemoryAllows is a SYSTEM memory paging admission check, and
        # ForeignVramBusyMiB decides whether the OWNER is using the card, not
        # whether our job fits beside whatever is on it. This is the only place
        # the question is asked, and it is asked with both numbers in the answer
        # so the person reading the failed job can act on it.
        free, _total = torch.cuda.mem_get_info()
        free_mib = int(free / 2 ** 20)
        if free_mib < self.min_free_vram_mib:
            raise RuntimeError(
                "the card had %s MiB free and this checkpoint needs %s MiB "
                "during load: it puts the whole model on the device in BF16 "
                "before it quantises anything, so the peak is the BF16 size. "
                "Close whatever is holding VRAM and retry, or lower "
                "--min-free-vram-mib if you know better than this measurement."
                % (f"{free_mib:,}", f"{self.min_free_vram_mib:,}"))

        t0 = time.monotonic()
        _add_wrapper_to_path(self.wrapper_dir)
        from torchao_inference import load_model_int4
        from generate import TekkenTokenizer

        svc.log("loading model", torch=torch.__version__, cuda=torch.version.cuda,
                device=torch.cuda.get_device_name(0), group_size=self.group_size,
                expect_seconds=SEEDS["cold_load_seconds"])
        torch.cuda.reset_peak_memory_stats()
        try:
            model = load_model_int4(self.model_dir, device=self.device,
                                    group_size=self.group_size)
        except torch.cuda.OutOfMemoryError as exc:
            # THE PROCESS STAYS ALIVE. See the class docstring: dying here is an
            # invisible relaunch loop, and every one of those loops answers
            # `queued` to a client that will wait out its whole timeout.
            torch.cuda.empty_cache()
            raise RuntimeError(
                "the card ran out of memory loading this checkpoint (%s MiB "
                "were free beforehand and the load peaks at about %s MiB). "
                "Nothing has been lost but this job; close whatever is holding "
                "VRAM and retry."
                % (f"{free_mib:,}", f"{SEEDS['load_peak_vram_mib']:,}")) from exc

        model.eval()
        # EVERYTHING IS BUILT LOCALLY AND ASSIGNED LAST, and that ordering is
        # the whole of this comment. self.model is the "already loaded" flag, so
        # setting it before the tokeniser is built or the geometry is checked
        # would mean a failure here leaves a runtime that skips the load on the
        # NEXT job and then dies on a None tokeniser - one legible failure
        # turned into an endless illegible one.
        tokenizer = TekkenTokenizer(str(Path(self.model_dir) / "tekken.json"))
        self._check_codec_geometry(model)
        self.model = model
        self.tokenizer = tokenizer
        self.load_seconds = time.monotonic() - t0
        self.vram_mib = int(torch.cuda.memory_allocated() / 2 ** 20)
        self.peak_vram_mib = int(torch.cuda.max_memory_allocated() / 2 ** 20)
        svc.log("model ready", load_seconds=round(self.load_seconds, 1),
                vram_mib=self.vram_mib, peak_vram_mib=self.peak_vram_mib,
                sample_rate=self.sample_rate, frame_rate=self.frame_rate)

    def _check_codec_geometry(self, model):
        """WHAT THE CHECKPOINT ACTUALLY PRODUCES, checked against what this file
        publishes about it, once, at load.

        THE DEFECT THIS PREVENTS is a published frame rate going quietly out of
        date. `frame_rate` and `native_sample_rate` cross the wire, a client
        divides by both, and a checkpoint revision that changed the patch size
        or the number of upsampling stages would leave every duration, every
        realtime factor and every token count wrong - with the audio still
        playing, which is what makes it expensive to find. Re-derived from the
        model's own config rather than trusted.
        """
        config = model.config
        stages = len(model.codec.upsample_convs)
        samples_per_frame = int(config.patch_size) * (2 ** stages)
        rate = int(config.sample_rate)
        if samples_per_frame != SAMPLES_PER_FRAME or rate != NATIVE_SAMPLE_RATE:
            raise RuntimeError(
                "this checkpoint decodes %d samples per frame at %d Hz; this "
                "controller publishes %d at %d and every duration a client "
                "computes would be wrong. The weights are not the ones this "
                "file was written against."
                % (samples_per_frame, rate, SAMPLES_PER_FRAME, NATIVE_SAMPLE_RATE))
        self.sample_rate = rate
        self.samples_per_frame = samples_per_frame
        self.frame_rate = rate / float(samples_per_frame)

    def voice_path(self, name):
        p = Path(self.voice_dir) / ("%s.pt" % name)
        if not p.exists():
            # UPSTREAM SILENTLY CARRIES ON HERE. generate_speech_fast tests
            # `if voice_path.exists()` and, when it does not, generates with no
            # speaker embedding at all - which produces audio, in some voice,
            # reported as a success. check_params refuses unknown names before
            # any work starts; this is the same refusal at the only other place
            # the file could go missing.
            raise ValueError(
                "voice %r has no embedding in %s; this checkpoint's speakers "
                "are files and that one is not there." % (name, self.voice_dir))
        return p

    # -- generation --------------------------------------------------------

    def speak(self, text, voice, flow_steps, cfg_alpha, job):
        """One utterance, checking the yield flag between frames.

        Vendored from generate_speech_fast. The per-frame solver itself is
        IMPORTED rather than copied: `_decode_one_frame_fast` is the arithmetic
        the owner chose flow_steps=32 by listening to, and a second copy of it
        here would be a second thing to keep in step with a checkpoint nobody in
        this project wrote.
        """
        import torch

        model = self.model
        config = model.config
        _add_wrapper_to_path(self.wrapper_dir)
        from generate_fast import _decode_one_frame_fast

        with torch.no_grad():
            voice_embed = torch.load(self.voice_path(voice), weights_only=True)
            voice_embed = voice_embed.to(device=self.device, dtype=torch.bfloat16)
            n_voice = voice_embed.shape[0]

            prompt_ids = [config.bos_id, config.begin_audio_id]
            prompt_ids.extend([config.audio_id] * n_voice)
            prompt_ids.append(config.inst_end_id)
            prompt_ids.extend(self.tokenizer.encode(text))
            prompt_ids.append(config.inst_id)
            prompt_ids.append(config.begin_audio_id)

            prompt_embed = model.backbone.tok_embeddings(
                torch.tensor([prompt_ids], device=self.device))
            prompt_embed[0, 2:2 + n_voice] = voice_embed

            model.backbone.setup_freqs(
                max_len=self.max_frames + len(prompt_ids) + 100, device=self.device)
            hidden, caches = model.backbone(prompt_embed)
            pos = len(prompt_ids)

            # The first decode step is an AUDIO token, and it is not optional:
            # without it the backbone has been told a turn is beginning and
            # never that it is speaking.
            audio_tok = model.backbone.tok_embeddings(
                torch.tensor([[config.audio_id]], device=self.device))
            hidden, caches = model.backbone(audio_tok, caches=caches, pos=pos)
            pos += 1
            h = hidden[:, -1, :]

            t0 = time.monotonic()
            all_codes = []
            ended = False
            for index in range(self.max_frames):
                # BETWEEN FRAMES, AND THIS LINE IS WHY THIS LOOP IS IN THIS FILE
                # AT ALL. Everything else here is upstream's, transcribed.
                job.check()
                codes, is_end = _decode_one_frame_fast(
                    model.acoustic, h, config,
                    flow_steps=flow_steps, cfg_alpha=cfg_alpha)
                if is_end.any():
                    ended = True
                    break
                all_codes.append(codes)
                next_embed = model.embed_audio_codes(codes).unsqueeze(1)
                hidden, caches = model.backbone(next_embed, caches=caches, pos=pos)
                pos += 1
                h = hidden[:, -1, :]
                if (index + 1) % PROGRESS_FRAMES == 0:
                    job.progress(frames=index + 1, of=self.max_frames,
                                 audio_seconds=round((index + 1) / self.frame_rate, 1))

            # NO `except RuntimeError: break` ANYWHERE ABOVE, and that absence is
            # the point. Upstream catches it inside the loop and returns whatever
            # it had as a SUCCESS, so a card that ran out halfway through hands
            # the client a truncated sentence with no error attached to it.
            generated = len(all_codes)
            if generated == 0:
                raise RuntimeError(
                    "the model produced no audio frames for this segment; the "
                    "text may be empty or the tokeniser may have swallowed it.")
            if not ended and generated >= self.max_frames:
                raise RuntimeError(
                    "generation reached frame %d of %d with no end-of-audio "
                    "code, so this segment is cut off at %.1f s. The text is "
                    "longer than this install's --max-frames ceiling; send "
                    "shorter segments or raise it in worker.ini."
                    % (generated, self.max_frames, generated / self.frame_rate))

            # THEIR TRIM, KEPT EXACTLY AS IT IS. It removes a leading run of
            # IDENTICAL semantic codes, which is the conservative half of the
            # warm-up glitch in HF discussion #20. It does not catch the rest,
            # and the thing that catches the rest is the fade in _shape(), NOT a
            # bigger trim: dropping frames unconditionally on top of this was
            # tried on the owner's own machine and it ate the first word.
            from audio_postprocess import trim_warmup_frames
            kept = trim_warmup_frames(all_codes)
            frames = len(kept)

            # SYNCHRONISE BEFORE THE CODEC, so that a CUDA error queued up during
            # generation is raised HERE, where it is still this job's failure,
            # rather than inside a decode that upstream wraps in a bare except.
            torch.cuda.synchronize()
            audio = model.codec(torch.stack(kept, dim=1))[0].float().cpu().numpy()

        compute = time.monotonic() - t0

        # THE GEOMETRY, ASSERTED ON EVERY SEGMENT. The codec is a reshape of a
        # fixed-width projection, so this is exact rather than approximate, and
        # it is the one check that would catch a resample sneaking back in
        # between here and the wire - which is precisely the 24000/48000 bug
        # that ships audio playing at half speed.
        expected = frames * self.samples_per_frame
        if audio.size != expected:
            raise RuntimeError(
                "%d frames decoded to %d samples and %d were expected at %d "
                "samples a frame; something has resampled this audio and the "
                "rate on the wire would be a lie."
                % (frames, audio.size, expected, self.samples_per_frame))
        return audio, frames, generated, compute


class FakeRuntime:
    """The same lifecycle and the same yield behaviour, with no torch and no GPU.

    WHY THIS EARNS ITS PLACE, and it earns it harder here than it does for
    chatterbox: the whole path - the YIELD line, the end-of-stdin dead man's
    switch, the job object kill, the abandon bookkeeping and the latency
    measurement - can be exercised on the real machine before anybody downloads
    7.5 GB, and re-run afterwards whenever that path changes. It also makes the
    per-frame yield granularity observable, which is the entire claim this
    controller makes over calling generate_speech_fast as shipped.
    """

    def __init__(self, voice_dir, frame_seconds=0.74, frames=40):
        self.voice_dir = str(voice_dir)
        self.device = "fake"
        self.sample_rate = NATIVE_SAMPLE_RATE
        self.samples_per_frame = SAMPLES_PER_FRAME
        self.frame_rate = FRAME_RATE
        self.max_frames = frames
        self.load_seconds = None
        self.vram_mib = 0
        self.peak_vram_mib = 0
        self.frame_seconds = frame_seconds
        self.frames = frames

    def ensure_loaded(self):
        if self.load_seconds is not None:
            return
        t0 = time.monotonic()
        time.sleep(1.0)                    # stand-in for 8 GB coming off disk
        self.load_seconds = time.monotonic() - t0
        svc.log("model ready", load_seconds=round(self.load_seconds, 3), fake=True)

    def voice_path(self, name):
        return Path(self.voice_dir) / ("%s.pt" % name)

    def speak(self, text, voice, flow_steps, cfg_alpha, job):
        import array
        t0 = time.monotonic()
        for _ in range(self.frames):
            # Deliberately in the same place the real one checks, because the
            # granularity is the thing being exercised.
            job.check()
            end = time.monotonic() + self.frame_seconds
            while time.monotonic() < end:
                pass
        n = self.frames * self.samples_per_frame
        return (array.array("f", bytes(4 * n)), self.frames, self.frames,
                time.monotonic() - t0)


def _peak(audio):
    """The loudest sample, for the sidecar. Works on a numpy array and on the
    array.array the fake runtime produces, because the sidecar is part of the
    path the fake exists to exercise."""
    try:
        return round(float(abs(audio).max()), 5)
    except TypeError:
        return round(max((abs(x) for x in audio), default=0.0), 5)


def _shape(audio, sample_rate, fade_ms, low_pass_hz, peak):
    """What happens to the samples between the codec and the artefact.

    THREE THINGS THE UPSTREAM POST-PROCESSOR DOES, AND WHAT IS DONE WITH EACH:

      LOW-PASS: upstream applies a 6th-order Butterworth at 10 kHz to every
        file. The owner heard it in one pass and called it a pilot mic, so it is
        OFF by default here (--low-pass-hz 0) and is a flag rather than a
        deletion, because it is his ear and somebody else's codec noise.
      RESAMPLE 24000 -> 48000: NOT DONE, and this is the important one.
        Upstream resamples and then generate.py, generate_fast.py and
        benchmark_all.py all write the result back at 24000 - only serve.py gets
        it right - which is a file that plays at half speed with nothing
        reporting an error. This service's whole wire is 24 kHz, so there is
        nothing to resample and nothing to mislabel.
      PEAK NORMALISE: OFF by default, and that is a considered choice rather
        than an omission. A job here is many segments generated independently,
        and a per-segment gain makes the loudness STEP between sentences, which
        a listener blames on the model. The gain belongs to whoever holds the
        whole job; every segment's measured peak is published in the timings
        sidecar so that it can be applied there without re-reading the audio.
        --peak is for single-utterance work by hand.

    AND ONE THING UPSTREAM DOES NOT DO. The warm-up glitch in HF discussion #20
    is a noise burst in the first frames, and trim_warmup_frames only catches it
    when it is a run of IDENTICAL codes. Dropping frames unconditionally to
    catch the rest was tried on the owner's machine and it ATE THE FIRST WORD.
    A squared ramp over the first 120 ms buries the burst under an amplitude
    envelope while leaving every sample of speech present, which is the
    difference between fixing the noise and eating the word in front of it.
    """
    import numpy as np

    audio = np.asarray(audio, dtype=np.float32).copy()
    if audio.size < 2:
        return audio
    if low_pass_hz:
        from scipy.signal import butter, sosfilt
        sos = butter(6, low_pass_hz, btype="low", fs=sample_rate, output="sos")
        audio = sosfilt(sos, audio).astype(np.float32)
    n = min(int(fade_ms / 1000.0 * sample_rate), audio.size)
    if n > 1:
        audio[:n] *= np.linspace(0.0, 1.0, n, dtype=np.float32) ** 2
    if peak:
        seen = float(np.abs(audio).max())
        if seen > 1e-6:
            audio = audio * (peak / seen)
    # "<f4" AND NOT float32. The wire format is little-endian float32 and the
    # reader on the other end does np.frombuffer(raw, "<f4"); saying so here
    # costs nothing on x86 and is the difference between a contract and a
    # coincidence about which machines this ever ran on.
    return audio.astype("<f4")


def check_params(rt, params, voices, defaults, settings):
    """Refuse, by name, anything this service cannot honour for this job.

    THE LAST LINE OF DEFENCE, AND THE ONLY ONE THAT SURVIVES A VERSION SKEW.
    The server that submits these jobs runs somewhere else, is upgraded on its
    own schedule, and can be older than this file. An older one sends
    exaggeration, cfg_weight, temperature and language to every speech service
    it knows about, because until this one existed every speech service on the
    stack honoured at least three of the four. If they were quietly ignored here
    that server would be told the job succeeded and would hand its caller audio
    that ignored most of the request, with no error anywhere in the stack.

    Named rather than counted, and the sentence says where the field DOES live,
    because a refusal a caller cannot act on is a refusal that gets retried.
    """
    schema = params_schema(voices, defaults)
    for name in sorted(params):
        # A leading underscore is this controller's own working state and never
        # came off the wire.
        if name.startswith("_") or name in schema:
            continue
        if name in LOAD_TIME_SETTINGS:
            # THE HOUSE RULE, CUTTING THE OTHER WAY. These are real settings of
            # this service and they are published in `settings` - they simply
            # cannot be changed for one job. Swallowing one because it is
            # honourable at load would be the caller believing something false
            # about the audio it just received.
            raise ValueError(
                "%s is an install-time setting on this runner and not a job "
                "parameter: it is %s. It is %r here and it is in the manifest "
                "under `settings`; change it in worker.ini."
                % (name, LOAD_TIME_SETTINGS[name], settings.get(name)))
        raise ValueError(
            "unsupported parameter %r: %s has no such control. It honours: %s."
            % (name, service_id(), ", ".join(sorted(schema))))

    voice = params.get("voice")
    if not voice:
        raise ValueError(
            "voice is required by %s: this checkpoint carries its speakers as "
            "fixed embeddings and there is no way to derive one from the text, "
            "so a default chosen here would be audio in a voice nobody asked "
            "for. Send one of: %s." % (service_id(), ", ".join(voices)))
    if voice not in voices:
        raise ValueError(
            "voice %r is not one of %s's %d: this checkpoint has no speaker "
            "encoder of any kind, so a clip cannot become a voice here. It "
            "carries: %s." % (voice, service_id(), len(voices), ", ".join(voices)))

    for name, spec in sorted(CONTROLS.items()):
        value = params.get(name)
        if value is None:
            continue
        if spec["kind"] is int and isinstance(value, float) and value != int(value):
            raise ValueError(
                "%s must be a whole number and %r is not: it is a loop count in "
                "the solver and reaches range() as a TypeError." % (name, value))
        try:
            value = spec["kind"](value)
        except (TypeError, ValueError):
            raise ValueError(
                "%s must be a number and %r is not." % (name, value)) from None
        if not (spec["min"] <= value <= spec["max"]):
            raise ValueError(
                "%s must be between %s and %s and %r is not. These bounds are "
                "the ones the server's own catalogue declares; a value outside "
                "them has never been listened to."
                % (name, spec["min"], spec["max"], value))

    # DECLARED MEANS CHECKED. sample_rate is in the schema, so a client naming a
    # rate this codec does not produce is told so rather than handed samples at
    # another rate and left to assemble them wrongly - which is the 24000/48000
    # trap arriving from the caller's side instead of ours.
    want = params.get("sample_rate")
    if want is not None and int(want) != int(rt.sample_rate):
        raise ValueError(
            "sample_rate %s was asked for and this codec produces %d; the rate "
            "is a property of the checkpoint and nothing here resamples. See "
            "`outputs` in the manifest." % (want, rt.sample_rate))


def build_handler(rt, settings, defaults, service_ref):
    voices = voice_names(rt.voice_dir)
    # A COLD LOAD IS MEASURED ONCE. Republishing service.json after every job
    # would rewrite the file for a number that cannot have changed.
    published = {"load": False}

    def handle(job):
        params = job.params
        # BEFORE ANY WORK, AND BEFORE THE 63-SECOND LOAD. A job that is going to
        # be refused must be refused while its client is still holding the
        # submit, not after three and a half minutes on a card somebody wanted
        # back.
        check_params(rt, params, voices, defaults, settings)
        segments = params.get("segments")
        if not isinstance(segments, list) or not segments:
            raise ValueError("params.segments must be a non-empty list of strings")

        voice = params["voice"]
        # None IS THE ONLY VALUE THAT MEANS "THE CALLER DID NOT SAY", so it is
        # tested for rather than leaned on: `params.get(x) or default` would
        # quietly turn a zero into the deployment default instead of the
        # refusal check_params has already written for it.
        flow_steps = params.get("flow_steps")
        flow_steps = defaults["flow_steps"] if flow_steps is None else int(flow_steps)
        cfg_alpha = params.get("cfg_alpha")
        cfg_alpha = defaults["cfg_alpha"] if cfg_alpha is None else float(cfg_alpha)

        # AFTER the refusals and INSIDE the job, so that a card with no room
        # fails one job with a sentence rather than killing a process the
        # scheduler will relaunch for ever.
        rt.ensure_loaded()
        service = service_ref[0]
        if service is not None and rt.load_seconds and not published["load"]:
            published["load"] = True
            service.update_manifest(
                cold_load_seconds=round(rt.load_seconds, 1),
                cold_load_source="measured on this machine",
                vram_mib=rt.vram_mib,
                load_peak_vram_mib=rt.peak_vram_mib or SEEDS["load_peak_vram_mib"])

        timings = []
        total_samples = 0
        total_frames = 0
        for i, text in enumerate(segments):
            job.check()
            audio, frames, generated, compute = rt.speak(
                str(text), voice, flow_steps, cfg_alpha, job)
            shaped = _shape(audio, rt.sample_rate, settings["fade_ms"],
                            settings["low_pass_hz"], settings["peak"])
            data = shaped.tobytes() if hasattr(shaped, "tobytes") else bytes(shaped)
            n = len(data) // 4
            job.artefact("%d.f32" % i, data)
            secs = n / float(rt.sample_rate)
            timings.append({
                "segment": i,
                "compute_seconds": round(compute, 3),
                "audio_seconds": round(secs, 3),
                "realtime_factor": round(secs / compute, 4) if compute > 0 else None,
                "samples": n,
                # FRAMES AND THE GRID THEY CAME OFF. `frames` is what was
                # decoded, after the warm-up trim; `frames_generated` is what
                # the model produced. frames / frame_rate is audio_seconds
                # exactly, and nothing else on this stack runs at 12.5 Hz.
                "frames": frames,
                "frames_generated": generated,
                "frame_rate": rt.frame_rate,
                # PUBLISHED SO THE JOB-WIDE GAIN CAN BE APPLIED WHERE THE JOB
                # IS. See _shape(): normalising per segment makes the loudness
                # step between sentences, so the gain is not applied here and
                # the number needed to apply it elsewhere is.
                "peak": _peak(shaped),
                "chars": len(str(text)),
                "voice": voice,
                "flow_steps": flow_steps,
                "cfg_alpha": cfg_alpha,
            })
            total_samples += n
            total_frames += frames
            job.progress(segment=i + 1, of=len(segments),
                         realtime_factor=timings[-1]["realtime_factor"])

            # unit_seconds is the floor on how long after a polite request this
            # machine can still be busy, and here it is ONE FRAME rather than
            # one utterance, because the loop above checks between frames. It is
            # only ever corrected upwards, and it is per frame so that a long
            # utterance does not inflate a number that describes interruption
            # latency.
            per_frame = compute / max(generated, 1)
            if service is not None and per_frame > service.manifest.get("unit_seconds", 0):
                service.update_manifest(
                    unit_seconds=round(per_frame, 2),
                    unit_seconds_source="measured on this machine, per frame")

        job.artefact("timings.json", json.dumps({
            "sample_rate": rt.sample_rate,
            "frame_rate": rt.frame_rate,
            "voice": voice,
            "flow_steps": flow_steps,
            "cfg_alpha": cfg_alpha,
            "settings": dict(settings),
            "segments": timings,
        }, indent=2))
        return {
            "sample_rate": rt.sample_rate,
            "frame_rate": rt.frame_rate,
            "segments": len(segments),
            "samples": total_samples,
            "frames": total_frames,
            "audio_seconds": round(total_samples / float(rt.sample_rate), 3),
            "voice": voice,
            "flow_steps": flow_steps,
            "cfg_alpha": cfg_alpha,
        }
    return handle


def _cuda_reason():
    """Why this machine cannot run it, or None. Checked once, before publishing.

    NOT A SystemExit, unlike chatterbox's equivalent, and the difference is
    where the process is in its life. Exiting before publish_manifest() means
    service.json never exists, so the agent's scheduler relaunches this every
    500 ms for ever while the client polls `queued` - loud in logs\\, invisible
    everywhere else. Publishing the reason means `GET /v1/services` answers the
    question and every job fails with the same sentence instead of hanging.
    """
    try:
        import torch
    except Exception as exc:                      # pragma: no cover - install fault
        return "torch will not import here (%s); re-run provision.ps1" % exc
    if not torch.cuda.is_available():
        return ("torch %s cannot see the GPU. This is almost always the CPU-only "
                "wheel from PyPI instead of the cu126 wheel from "
                "download.pytorch.org. There is NO CPU path for this "
                "checkpoint - torchao's int4 kernel asks for a CUDA device "
                "capability before any device dispatch - so re-run "
                "provision.ps1." % torch.__version__)
    return None


def _validate_flags(args):
    """Refuse a flag combination that would fail EVERY job, at start.

    THE DEFECT THIS PREVENTS. --low-pass-hz 20000 at a 24 kHz rate is above
    Nyquist, and scipy answers that with "Digital filter critical frequencies
    must be 0 < Wn < 1" from four frames inside butter(), once per job, for ever.
    A number in worker.ini that can only ever produce a failed job should be one
    sentence when the process starts, not a stack trace per client.
    """
    nyquist = NATIVE_SAMPLE_RATE / 2.0
    if args.low_pass_hz and not (0 < args.low_pass_hz < nyquist):
        raise SystemExit(
            "FATAL: --low-pass-hz %s is not between 0 and %.0f, which is Nyquist "
            "for this codec's %d Hz. 0 turns the filter off; upstream uses 10000."
            % (args.low_pass_hz, nyquist, NATIVE_SAMPLE_RATE))
    if args.fade_ms < 0:
        raise SystemExit("FATAL: --fade-ms %s is negative." % args.fade_ms)
    if args.peak and not (0 < args.peak <= 1.0):
        raise SystemExit(
            "FATAL: --peak %s is not between 0 and 1. 0 turns normalisation off, "
            "which is the default and the right setting for multi-segment jobs."
            % args.peak)
    for name, spec in sorted(CONTROLS.items()):
        value = getattr(args, name)
        if not (spec["min"] <= value <= spec["max"]):
            raise SystemExit(
                "FATAL: --%s %s is outside %s..%s, so every job would be refused "
                "against a deployment default nobody could satisfy."
                % (name.replace("_", "-"), value, spec["min"], spec["max"]))
    if args.max_frames < 1:
        raise SystemExit("FATAL: --max-frames %s is not a frame count."
                         % args.max_frames)


def main():
    _assert_utf8_mode()

    # BOTH DEFAULTS COME OFF IDLEGPU_SERVICE_ROOT, which the agent sets to this
    # service's own install directory. So worker.ini names neither, and a
    # by-hand run in a copy of the tree finds its own copy of everything.
    service_root = Path(os.getenv("IDLEGPU_SERVICE_ROOT", "."))
    default_model = str(service_root / "models" / "original")
    default_wrapper = str(service_root / "voxtral-int4" / "src")
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--queue", required=True, help="the service's queue directory")
    ap.add_argument("--model-dir", default=default_model,
                    help="the checkpoint directory, holding consolidated.safetensors, "
                         "tekken.json and voice_embedding/ (default: inside the "
                         "service's own install directory)")
    ap.add_argument("--wrapper-dir", default=default_wrapper,
                    help="the voxtral-int4 source directory that provision.ps1 "
                         "unpacked, at the commit its pin names (default: inside "
                         "the service's own install directory)")
    ap.add_argument("--device", default="cuda",
                    help="cuda. There is no cpu here and naming one would be a "
                         "promise this checkpoint cannot keep")
    # ---- the two quality knobs, settable per job, defaulted per deployment ----
    ap.add_argument("--flow-steps", type=int, default=32,
                    help="default flow-matching steps when a job does not say. "
                         "THE MAIN QUALITY KNOB: 32 was judged clearly best by "
                         "ear on an RTX 3070, 16 close behind, 8 and 4 audibly "
                         "worse. Upstream defaults to 8 (default: 32)")
    ap.add_argument("--cfg-alpha", type=float, default=1.2,
                    help="default guidance strength when a job does not say. "
                         "1.2 is full CFG; 1.0 is documented upstream as faster "
                         "and garbled (default: 1.2)")
    # ---- load-time and artefact settings, refused as job parameters ----
    ap.add_argument("--group-size", type=int, default=32,
                    help="int4 quantisation group size. The shipped default is "
                         "64; 32 is finer and cost no measurable VRAM. Consumed "
                         "once inside the load (default: 32)")
    ap.add_argument("--max-frames", type=int, default=2000,
                    help="per-segment ceiling in 12.5 Hz frames, so 2000 is 160 "
                         "seconds of audio. The wrapper's fast path defaults to "
                         "500, a 40 s ceiling, which silently truncates a long "
                         "sentence (default: 2000)")
    ap.add_argument("--fade-ms", type=float, default=120.0,
                    help="squared fade-in over the start of every segment, which "
                         "buries the upstream warm-up glitch without dropping a "
                         "single sample of speech (default: 120)")
    ap.add_argument("--low-pass-hz", type=float, default=0.0,
                    help="6th-order Butterworth low pass, 0 to disable. Upstream "
                         "applies one at 10000 and it sounds like a pilot mic "
                         "(default: 0, off)")
    ap.add_argument("--peak", type=float, default=0.0,
                    help="peak-normalise each segment to this level, 0 to "
                         "disable. OFF by default: a job is many segments and a "
                         "per-segment gain makes the loudness step between "
                         "sentences (default: 0, off)")
    ap.add_argument("--min-free-vram-mib", type=int, default=8400,
                    help="refuse to start a load with less than this free. The "
                         "load peaks at 8265 MiB on an 8192 MiB card, so on a "
                         "3070 this means the desktop has to be quiet "
                         "(default: 8400)")
    ap.add_argument("--idle-exit-seconds", type=float, default=600.0,
                    help="exit after this long with nothing to do, so another "
                         "service can have the card. Long, because the cold load "
                         "is 63 s and a short window in front of it is a card "
                         "that spends its evening loading (default: 600)")
    ap.add_argument("--once", action="store_true", help="drain the queue and exit")
    ap.add_argument("--no-stdin-watch", action="store_true",
                    help="for running by hand, where stdin is a terminal")
    ap.add_argument("--fake-model", action="store_true",
                    help="exercise the whole path with no torch and no GPU")
    ap.add_argument("--fake-frames", type=int, default=40,
                    help="how many frames a fake utterance takes (default 40)")
    args = ap.parse_args()
    _validate_flags(args)

    settings = {
        "group_size": args.group_size,
        "max_frames": args.max_frames,
        "fade_ms": args.fade_ms,
        "low_pass_hz": args.low_pass_hz,
        "peak": args.peak,
        "min_free_vram_mib": args.min_free_vram_mib,
    }
    defaults = {"flow_steps": args.flow_steps, "cfg_alpha": args.cfg_alpha}

    model_dir = Path(args.model_dir)
    if args.fake_model:
        rt = FakeRuntime(model_dir / "voice_embedding", frames=args.fake_frames)
        unavailable = None
    else:
        unavailable = _cuda_reason()
        if unavailable:
            svc.log("this machine cannot run this service", reason=unavailable)
        rt = Runtime(model_dir, args.wrapper_dir, device=args.device,
                     group_size=args.group_size, max_frames=args.max_frames,
                     min_free_vram_mib=args.min_free_vram_mib,
                     unavailable=unavailable)

    voices = voice_names(rt.voice_dir)
    if not voices:
        # NOT FATAL, AND SAID OUT LOUD. The ready marker is provisioning's last
        # act, so reaching here with no embeddings means somebody moved or
        # part-deleted the weights. Publishing an empty list and refusing every
        # job by name is more useful than a process that will not start: the
        # refusal names the directory, and `idlegpu services` shows it.
        svc.log("no voice embeddings found; every job will be refused",
                voice_dir=rt.voice_dir)

    # PUBLISHED BEFORE ANYTHING IS LOADED. See Runtime's docstring: this
    # checkpoint peaks at 8265 MiB on an 8192 MiB card, and a load that dies
    # before service.json exists is an invisible relaunch loop.
    published_settings = dict(settings)
    published_settings.update(defaults)
    m = manifest(voices, published_settings, defaults, rt.sample_rate,
                 getattr(rt, "device", "cuda"), SEEDS["unit_seconds"],
                 rt.vram_mib, unavailable=unavailable)
    m["unit_seconds_source"] = "unverified default until a frame has been timed"
    m["cold_load_source"] = "measured on an RTX 3070, not on this machine"
    ref = [None]
    service = svc.Service(args.queue, m, build_handler(rt, settings, defaults, ref),
                          idle_exit_seconds=args.idle_exit_seconds)
    ref[0] = service
    service.run(once=args.once, watch_stdin_thread=not args.no_stdin_watch)


if __name__ == "__main__":
    main()
