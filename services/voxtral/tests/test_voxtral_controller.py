"""A third engine, a second torch, and the ways this one goes wrong quietly.

WHY THIS FILE EXISTS. Every failure mode Voxtral adds is silent from the
outside: a job succeeds, audio comes back, and it is the wrong length, the wrong
voice, the wrong bandwidth, or three and a half minutes of somebody's GPU thrown
away because the yield flag was only ever read between utterances. None of it is
reachable from the C# tests, which know no word for audio.

  * postprocess_audio RESAMPLES 24000 -> 48000 and three of the four writers in
    the upstream repository then write the result back at 24000. That is a file
    that plays at half speed, with nothing anywhere reporting an error.
  * generate_speech_fast catches RuntimeError inside its frame loop and returns
    whatever it had as a SUCCESS. A card that ran out halfway through delivers a
    truncated sentence with no error attached.
  * Its 10 kHz Butterworth is applied to every file, unasked.
  * Its frame loop has no interruption point at all, so as shipped one utterance
    is one uninterruptible unit of about three and a half minutes.
  * It has twenty fixed speakers and no speaker encoder, so every field an older
    server sends to a cloning engine has to be refused BY NAME rather than
    ignored.

Run it with anything: python -m unittest discover services/voxtral/tests, or
pytest. It imports the controller and needs NO torch, no GPU, no agent, no
network and no model weights - the controller only imports torch inside
Runtime.ensure_loaded(), which nothing here calls.
"""

import ast
import inspect
import json
import os
import subprocess
import sys
import tempfile
import textwrap
import unittest
from pathlib import Path

_HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(_HERE, "..", "..", "lib"))
sys.path.insert(0, os.path.join(_HERE, ".."))

import controller                                                   # noqa: E402
import offpeak_service as svc                                       # noqa: E402

try:
    import scipy.signal                                             # noqa: F401
    _HAS_SCIPY = True
except ImportError:
    # scipy is a real dependency of this service - audio_postprocess imports it
    # at module scope and the wrapper's README does not mention it - but this
    # controller only reaches for it when --low-pass-hz is non-zero, which is
    # not the default. So the suite runs anywhere and one test opts out.
    _HAS_SCIPY = False


# The twenty that ship inside mistralai/Voxtral-4B-TTS-2603. Written out HERE,
# in the test, and read off the disk in the controller: the point of the check is
# that the two agree, and a controller that imported this list would be asserting
# against itself.
TWENTY = [
    "ar_male", "casual_female", "casual_male", "cheerful_female", "de_female",
    "de_male", "es_female", "es_male", "fr_female", "fr_male", "hi_female",
    "hi_male", "it_female", "it_male", "neutral_female", "neutral_male",
    "nl_female", "nl_male", "pt_female", "pt_male",
]

DEFAULTS = {"flow_steps": 32, "cfg_alpha": 1.2}
SETTINGS = {"group_size": 32, "max_frames": 2000, "fade_ms": 120.0,
            "low_pass_hz": 0.0, "peak": 0.0, "min_free_vram_mib": 8400}


def _voice_dir(root):
    """Twenty empty .pt files. The controller reads the directory rather than
    carrying a list, so this is what a checkpoint looks like from its side."""
    d = Path(root) / "voice_embedding"
    d.mkdir(parents=True, exist_ok=True)
    for name in TWENTY:
        (d / ("%s.pt" % name)).write_bytes(b"")
    return d


class RuntimeShape(object):
    """Everything check_params reads off a Runtime, and nothing else."""

    def __init__(self, sample_rate=24000):
        self.sample_rate = sample_rate


class TheManifestId(unittest.TestCase):
    def setUp(self):
        self._had = os.environ.get("OFFPEAK_SERVICE_ID")

    def tearDown(self):
        if self._had is None:
            os.environ.pop("OFFPEAK_SERVICE_ID", None)
        else:
            os.environ["OFFPEAK_SERVICE_ID"] = self._had

    def _manifest(self):
        return controller.manifest(TWENTY, SETTINGS, DEFAULTS, 24000, "cuda",
                                   1.0, 3700)

    def test_the_id_comes_from_the_agent_and_not_from_a_literal(self):
        """THE DEFECT THIS PREVENTS. Nothing reconciles the id in worker.ini with
        the id inside the manifest: the agent splices the manifest in verbatim
        under a key labelled with the section id. A controller copied to make
        another service, with the first one's id left in, publishes
            {"id":"voxtral", ..., "manifest":{"id":"chatterbox"}}
        and every layer is satisfied while anything routing on the manifest id
        hands back the other engine."""
        os.environ["OFFPEAK_SERVICE_ID"] = "speech-on-the-other-box"
        self.assertEqual(self._manifest()["id"], "speech-on-the-other-box")

    def test_run_by_hand_it_still_names_itself(self):
        os.environ.pop("OFFPEAK_SERVICE_ID", None)
        self.assertEqual(controller.service_id(), "voxtral")


class TheHonestSurface(unittest.TestCase):
    """What a client can tell BEFORE submitting, rather than by receiving
    something else."""

    def setUp(self):
        self.m = controller.manifest(TWENTY, SETTINGS, DEFAULTS, 24000, "cuda",
                                     1.0, 3700)

    def test_it_advertises_no_control_this_checkpoint_cannot_honour(self):
        """THE HOUSE RULE, ASSERTED. Every field is honoured or refused by name;
        none is accepted and dropped. A schema listing exaggeration here would be
        this controller promising expression a Mistral flow-matching checkpoint
        has no conditioning layer for."""
        schema = self.m["params_schema"]
        for absent in ("exaggeration", "cfg_weight", "temperature", "language",
                       "reference_sha256"):
            self.assertNotIn(
                absent, schema,
                "the manifest must not advertise %r: this checkpoint has no such "
                "control and a client would believe it did" % absent)
        for present in ("segments", "voice", "sample_rate", "flow_steps",
                        "cfg_alpha"):
            self.assertIn(present, schema)

    def test_it_says_it_cannot_clone_as_a_fact_of_its_own(self):
        """min_reference_seconds = 0 already means "no minimum" for an engine
        that DOES take a clip. It cannot also mean "there is no speaker encoder
        here at all", so the second fact is published separately or a client
        offers a clip upload to a checkpoint with nowhere to put it."""
        self.assertIs(self.m["reference_audio"], False)
        self.assertIn("min_reference_seconds", self.m)

    def test_the_rate_it_publishes_is_the_rate_the_codec_produces(self):
        """THE 24000/48000 TRAP. Audio that has been through the wrapper's
        postprocess is 48 kHz; generate.py, generate_fast.py and
        benchmark_all.py all write it back at 24000 anyway. Two published fields
        that could disagree are a third way to ship the same bug."""
        self.assertEqual(self.m["native_sample_rate"], 24000)
        self.assertEqual(self.m["outputs"], "audio/pcm-f32@24000")

    def test_the_frame_rate_and_the_sample_rate_describe_one_grid(self):
        """frames / frame_rate is the audio duration, and it is the one number
        on this stack that comes off a 12.5 Hz grid. Publishing a rate that does
        not divide the sample rate into whole frames would make every duration a
        client computes slightly wrong."""
        self.assertEqual(self.m["frame_rate"], 12.5)
        per_frame = self.m["native_sample_rate"] / self.m["frame_rate"]
        self.assertEqual(per_frame, 1920)

    def test_the_install_time_settings_are_visible_without_an_ssh_session(self):
        """SSH to this machine lands in session 0 and can start nothing, so
        "what is that runner actually serving" has to be answerable with curl.
        These four are exactly the ones a job may not change."""
        published = self.m["settings"]
        for name in ("group_size", "max_frames", "fade_ms", "low_pass_hz"):
            self.assertIn(name, published)
        self.assertEqual(published["group_size"], 32)
        self.assertEqual(published["max_frames"], 2000)
        self.assertEqual(published["fade_ms"], 120.0)
        self.assertEqual(published["low_pass_hz"], 0.0)

    def test_it_declares_that_it_refuses_what_it_does_not_declare(self):
        self.assertIs(self.m["refuses_undeclared_params"], True)

    def test_the_voices_are_published_so_nobody_keeps_a_second_list(self):
        self.assertEqual(self.m["voices"], TWENTY)

    def test_a_machine_that_cannot_run_it_says_so_in_the_manifest(self):
        """Rather than failing every job with the same sentence and no way to
        see it coming. A CPU-only torch wheel is the most common install fault
        and there is NO cpu path for this checkpoint to fall back to."""
        m = controller.manifest(TWENTY, SETTINGS, DEFAULTS, 24000, "cuda", 1.0, 0,
                                unavailable="torch cannot see the GPU")
        self.assertIn("unavailable", m)


class RefusingWhatItCannotHonour(unittest.TestCase):
    """THE LAST LINE OF DEFENCE, AND THE ONLY ONE THAT SURVIVES A VERSION SKEW.

    The server that submits these jobs runs somewhere else and can be older than
    this file. An older one sends exaggeration, cfg_weight, temperature and
    language to every speech service it knows about, because until this one
    existed every speech service on the stack honoured at least three of the
    four. Ignoring them here would tell that server the job succeeded and hand
    its caller audio that ignored most of the request.
    """

    def _check(self, params, sample_rate=24000):
        controller.check_params(RuntimeShape(sample_rate), params, TWENTY,
                                DEFAULTS, SETTINGS)

    def test_a_cloning_engine_s_fields_are_refused_by_name(self):
        for bad, value in (("exaggeration", 0.3), ("cfg_weight", 0.3),
                           ("temperature", 0.6), ("language", "pt"),
                           ("reference_sha256", "a" * 64)):
            with self.assertRaises(ValueError) as caught:
                self._check({"segments": ["hi"], "voice": "pt_male", bad: value})
            message = str(caught.exception)
            self.assertIn(bad, message,
                          "the refusal must NAME the field, or nobody can fix it")
            self.assertIn("voxtral", message,
                          "and name the service that cannot honour it")

    def test_the_load_time_flags_are_refused_as_job_params(self):
        """THE HOUSE RULE, CUTTING THE OTHER WAY. group_size is consumed once
        inside a 63-second load; max_frames sizes the rotary table; fade_ms,
        low_pass_hz and peak must be identical across every segment of a
        listening comparison or two rows in the tuning log differ in a way that
        is not a model setting. All five are real settings of this service and
        none of them can be changed for one job - so they are refused by name
        with their current value, not swallowed because they are honourable at
        load."""
        for name, value in (("group_size", 64), ("max_frames", 500),
                            ("fade_ms", 0), ("low_pass_hz", 10000),
                            ("peak", 0.95)):
            with self.assertRaises(ValueError) as caught:
                self._check({"segments": ["hi"], "voice": "pt_male", name: value})
            message = str(caught.exception)
            self.assertIn(name, message)
            self.assertIn("install-time", message,
                          "the sentence has to say WHY it cannot be per job")
            self.assertIn(str(SETTINGS[name]), message,
                          "and say what it actually is here, or the caller "
                          "cannot tell whether they needed to ask")
            self.assertIn("worker.ini", message,
                          "and say where it CAN be changed")

    def test_a_job_with_no_voice_is_refused_rather_than_given_one(self):
        """There are twenty speakers and no way to derive one from the text, so
        a default chosen here is audio in a voice nobody asked for, delivered as
        a success. Upstream's generate_speech_fast defaults to neutral_female."""
        with self.assertRaises(ValueError) as caught:
            self._check({"segments": ["hi"]})
        self.assertIn("voice", str(caught.exception))

    def test_a_voice_this_checkpoint_does_not_carry_is_refused_by_name(self):
        with self.assertRaises(ValueError) as caught:
            self._check({"segments": ["hi"], "voice": "gabriel"})
        message = str(caught.exception)
        self.assertIn("gabriel", message)
        self.assertIn("pt_male", message, "and list what it does carry")

    def test_a_control_outside_the_bounds_the_catalogue_declares_is_refused(self):
        """The bounds are duplicated from the server's CONTROL_RANGES on purpose:
        the far end of the wire is the only place a version skew can be caught."""
        for params in ({"flow_steps": 0}, {"flow_steps": 65},
                       {"cfg_alpha": 0.5}, {"cfg_alpha": 3.5}):
            with self.assertRaises(ValueError):
                self._check(dict({"segments": ["hi"], "voice": "pt_male"}, **params))

    def test_a_fractional_flow_steps_is_refused_before_it_reaches_range(self):
        """It is a loop count in the solver. 32.5 through a config key reaches
        range() as a TypeError four frames into somebody's job, three and a half
        minutes after they submitted it."""
        with self.assertRaises(ValueError) as caught:
            self._check({"segments": ["hi"], "voice": "pt_male", "flow_steps": 32.5})
        self.assertIn("flow_steps", str(caught.exception))

    def test_a_rate_this_codec_cannot_produce_is_refused(self):
        """DECLARED MEANS CHECKED. 48000 is the number the upstream repository
        would hand you, and it is the one a caller is most likely to send."""
        with self.assertRaises(ValueError) as caught:
            self._check({"segments": ["hi"], "voice": "pt_male",
                         "sample_rate": 48000})
        self.assertIn("48000", str(caught.exception))

    def test_a_job_with_only_what_it_honours_is_accepted(self):
        self._check({"segments": ["hello there"], "voice": "neutral_female",
                     "flow_steps": 16, "cfg_alpha": 1.2, "sample_rate": 24000})

    def test_the_controllers_own_working_state_is_not_refused(self):
        self._check({"segments": ["hi"], "voice": "pt_male", "_started": 1.0})


class WhatHappensToTheSamples(unittest.TestCase):
    """_shape is everything between the codec and the artefact, and every one of
    these assertions is about something the upstream post-processor does that
    this service must not."""

    def _tone(self, hz, seconds=0.5, rate=24000, amp=0.2):
        import math
        n = int(rate * seconds)
        return [amp * math.sin(2 * math.pi * hz * i / rate) for i in range(n)]

    def test_it_does_not_resample(self):
        """THE HALF-SPEED BUG, AND THE ONLY ASSERTION THAT CATCHES IT HERE.
        postprocess_audio returns 48 kHz and three of its four callers write the
        result at 24000. Length in equals length out is what says nothing
        resampled."""
        samples = self._tone(440)
        out = controller._shape(samples, 24000, 120.0, 0.0, 0.0)
        self.assertEqual(len(out), len(samples))

    def test_it_does_not_low_pass_by_default(self):
        """Upstream applies a 6th-order Butterworth at 10 kHz to every file and
        the owner heard it in one pass and called it a pilot mic. 11 kHz is above
        that corner and below Nyquist, so it survives here and would not there.

        11000 AND NOT 12000: at a 24 kHz rate, sin(2*pi*12000*i/24000) is
        sin(pi*i), which is zero at every sample. A test tone exactly at Nyquist
        measures nothing and passes against any filter at all."""
        loud = self._tone(11000)
        out = controller._shape(loud, 24000, 0.0, 0.0, 0.0)
        self.assertGreater(abs(out).max(), 0.15,
                           "an 11 kHz tone must come out at roughly the level it "
                           "went in; if it does not, a low pass is on")

    @unittest.skipUnless(_HAS_SCIPY, "scipy is only needed when the filter is on")
    def test_the_low_pass_is_a_flag_and_not_a_deletion(self):
        """It is the owner's ear and somebody else's codec noise, so turning it
        back on has to still work. The transient is skipped: a sosfilt starts
        from rest, so the first few hundred samples of any tone are a step
        response and not the steady state being measured."""
        loud = self._tone(11000)
        out = controller._shape(loud, 24000, 0.0, 10000.0, 0.0)
        self.assertLess(abs(out[2000:]).max(), 0.05)

    def test_it_does_not_normalise_by_default(self):
        """A job is many segments generated independently and a per-segment gain
        makes the loudness STEP between sentences, which a listener blames on the
        model. The peak is published in the sidecar instead so the gain can be
        applied where the whole job is."""
        quiet = self._tone(440, amp=0.05)
        out = controller._shape(quiet, 24000, 0.0, 0.0, 0.0)
        self.assertLess(abs(out).max(), 0.06)

    def test_the_warm_up_glitch_is_faded_and_not_cut(self):
        """DROPPING FRAMES WAS TRIED AND IT ATE THE FIRST WORD. A squared ramp
        over the first 120 ms buries the burst under an amplitude envelope while
        leaving every sample of speech present. So: the start is silenced, the
        length is unchanged, and the ramp is squared rather than linear."""
        flat = [1.0] * 24000
        out = controller._shape(flat, 24000, 120.0, 0.0, 0.0)
        self.assertEqual(len(out), 24000, "not one sample may be dropped")
        self.assertAlmostEqual(float(out[0]), 0.0, places=5)
        n = int(0.12 * 24000)
        self.assertAlmostEqual(float(out[n // 2]), 0.25, places=2,
                               msg="halfway through a SQUARED ramp is 0.25, not 0.5")
        self.assertAlmostEqual(float(out[n + 10]), 1.0, places=5,
                               msg="past the fade nothing is touched")

    def test_the_wire_format_is_little_endian_float32(self):
        out = controller._shape([0.5] * 100, 24000, 0.0, 0.0, 0.0)
        self.assertEqual(out.dtype.str, "<f4")


class TheYieldCheckIsInsideTheFrameLoop(unittest.TestCase):
    def test_speak_checks_the_yield_flag_between_frames(self):
        """THE WHOLE REASON THIS CONTROLLER OWNS A LOOP IT DID NOT WRITE.

        generate_speech_fast's loop has no interruption point, so calling it as
        shipped makes one utterance a single uninterruptible unit of about three
        and a half minutes: the owner sits down, YieldGraceSeconds expires, the
        job object kills the process, and all of that GPU time is thrown away.
        Read from the SYNTAX TREE, so a comment promising the check is not
        mistaken for the check.
        """
        tree = ast.parse(textwrap.dedent(inspect.getsource(controller.Runtime.speak)))
        loops = [n for n in ast.walk(tree) if isinstance(n, (ast.For, ast.While))]
        self.assertTrue(loops, "speak() has no frame loop at all")
        inside = [
            n for loop in loops for n in ast.walk(loop)
            if isinstance(n, ast.Call) and isinstance(n.func, ast.Attribute)
            and n.func.attr == "check"
        ]
        self.assertTrue(
            inside,
            "job.check() is not called inside speak()'s frame loop. Between "
            "utterances is not between units: at 0.104x realtime one utterance "
            "is minutes, and the manifest's unit_seconds would be a lie.")

    def test_a_cuda_error_is_not_swallowed_into_a_successful_job(self):
        """generate_fast.py:189 catches RuntimeError INSIDE the loop and breaks,
        returning partial audio as a success; :216 returns one sample of silence,
        also as a success. Both are a truncated sentence with no error attached
        to it. The vendored loop must catch neither."""
        tree = ast.parse(textwrap.dedent(inspect.getsource(controller.Runtime.speak)))
        for node in ast.walk(tree):
            if not isinstance(node, ast.ExceptHandler):
                continue
            names = []
            if isinstance(node.type, ast.Name):
                names = [node.type.id]
            elif isinstance(node.type, ast.Tuple):
                names = [e.id for e in node.type.elts if isinstance(e, ast.Name)]
            self.assertNotIn(
                "RuntimeError", names,
                "speak() swallows RuntimeError, which is what a CUDA "
                "out-of-memory raises; upstream does this and calls the result "
                "a success")


class NoBranchOnAServiceName(unittest.TestCase):
    def test_the_wire_surface_never_tests_for_this_service_by_name(self):
        """A second checkpoint behind this controller must be data and no code.
        Read from the SYNTAX TREE and not the text, so the docstrings - which
        have to name it, because what it cannot do is exactly what needs
        explaining - are not mistaken for a branch on it."""
        for func in (controller.Runtime.speak, controller.check_params,
                     controller.params_schema, controller.manifest,
                     controller._shape):
            tree = ast.parse(textwrap.dedent(inspect.getsource(func)))
            body = tree.body[0].body
            if body and isinstance(body[0], ast.Expr) and \
                    isinstance(body[0].value, ast.Constant):
                body = body[1:]                    # the docstring is prose too
            found = [n.value for stmt in body for n in ast.walk(stmt)
                     if isinstance(n, ast.Constant)
                     and n.value == controller.DEFAULT_SERVICE_ID]
            self.assertEqual(
                found, [],
                "%s names the service in its code; the id comes from "
                "OFFPEAK_SERVICE_ID and nowhere else" % func.__name__)


class TheVoicesComeOffTheDisk(unittest.TestCase):
    def test_the_twenty_are_read_from_the_checkpoint_and_not_listed_in_code(self):
        """A hard-coded list is a second copy of a fact that a reinstall or a
        checkpoint revision can change underneath it, and the first symptom
        would be a voice offered in somebody's picker that fails on submission."""
        with tempfile.TemporaryDirectory() as root:
            d = _voice_dir(root)
            self.assertEqual(controller.voice_names(d), TWENTY)
            (d / "pt_male.pt").unlink()
            self.assertNotIn("pt_male", controller.voice_names(d))

    def test_a_missing_voice_directory_is_an_empty_list_and_not_a_crash(self):
        self.assertEqual(controller.voice_names("/no/such/place"), [])


class Utf8ModeIsAsserted(unittest.TestCase):
    """The wrapper's generate.py opens tekken.json with no encoding, so Windows
    uses cp1252 and the tokeniser dies inside json.load on 14.9 MB of base64 and
    Unicode. Measured on the test machine: without -X utf8 the tokeniser cannot
    be constructed at all. The flag lives in one line of worker.ini and can be
    lost by an edit, so losing it must be one sentence at start rather than a
    stack trace four frames deep in tiktoken at the first job."""

    SNIPPET = ("import sys; sys.path.insert(0, %r); sys.path.insert(0, %r); "
               "import controller; controller._assert_utf8_mode(); print('ok')")

    def _run(self, *flags):
        code = self.SNIPPET % (os.path.join(_HERE, "..", "..", "lib"),
                               os.path.join(_HERE, ".."))
        env = dict(os.environ)
        env.pop("PYTHONUTF8", None)
        return subprocess.run([sys.executable, *flags, "-c", code],
                              capture_output=True, text=True, env=env)

    def test_it_refuses_to_start_without_utf8_mode(self):
        done = self._run("-X", "utf8=0")
        self.assertNotEqual(done.returncode, 0)
        self.assertIn("tekken.json", done.stderr)

    def test_it_starts_with_utf8_mode(self):
        done = self._run("-X", "utf8")
        self.assertEqual(done.returncode, 0, done.stderr)


class TheLoadIsTheOneSpanThatCannotBeBrokenUp(unittest.TestCase):
    """Everywhere else the yield check is between frames. The 63-second load is
    the exception, and the two things that can be done about it are done."""

    def _runtime(self, **kw):
        return controller.Runtime("/no/such/model", "/no/such/wrapper", **kw)

    def test_a_machine_that_cannot_run_it_fails_the_job_with_the_reason(self):
        """A CPU-only torch wheel is the most common install fault and this
        checkpoint has NO cpu path to fall back onto. The reason is published in
        the manifest so a client can stop submitting, and it has to reach the
        job too, or the failed record carries whatever torch says when it is
        asked for a device it cannot see."""
        rt = self._runtime(unavailable="torch cannot see the GPU here")
        with self.assertRaises(RuntimeError) as caught:
            rt.ensure_loaded()
        self.assertIn("cannot see the GPU", str(caught.exception))

    def test_a_load_does_not_start_when_the_owner_is_already_back(self):
        """63 seconds that nothing can interrupt. Starting one after the yield
        flag is already set spends a minute of somebody's frame time and then
        gets killed by the job object anyway. Raising Yielded puts the lease back
        in pending/ and it runs when they leave."""
        svc.YIELD.set()
        try:
            with self.assertRaises(svc.Yielded):
                self._runtime().ensure_loaded()
        finally:
            svc.YIELD.clear()


class FlagsThatWouldFailEveryJob(unittest.TestCase):
    """A number in worker.ini that can only ever produce a failed job should be
    one sentence when the process starts, not a stack trace per client."""

    class Args(object):
        def __init__(self, **kw):
            self.low_pass_hz = 0.0
            self.fade_ms = 120.0
            self.peak = 0.0
            self.flow_steps = 32
            self.cfg_alpha = 1.2
            self.max_frames = 2000
            self.__dict__.update(kw)

    def test_the_shipped_flags_are_accepted(self):
        controller._validate_flags(self.Args())

    def test_a_low_pass_above_nyquist_is_refused_at_start(self):
        """scipy answers this with "Digital filter critical frequencies must be
        0 < Wn < 1" from four frames inside butter(), once per job, for ever."""
        with self.assertRaises(SystemExit) as caught:
            controller._validate_flags(self.Args(low_pass_hz=20000.0))
        self.assertIn("Nyquist", str(caught.exception))

    def test_a_deployment_default_outside_the_bounds_is_refused_at_start(self):
        """Otherwise every job is refused against a default nobody could
        satisfy, and the refusal names the caller's field rather than the
        operator's flag."""
        for bad in ({"flow_steps": 0}, {"flow_steps": 65}, {"cfg_alpha": 0.5}):
            with self.assertRaises(SystemExit):
                controller._validate_flags(self.Args(**bad))

    def test_a_peak_outside_zero_to_one_is_refused_at_start(self):
        with self.assertRaises(SystemExit):
            controller._validate_flags(self.Args(peak=1.5))


class OneWholeJob(unittest.TestCase):
    """The whole path - claim by rename, artefacts before the terminal record,
    the sidecar, the frame arithmetic - with no torch and no GPU."""

    def _run_one(self, params, frames=4):
        """Returns (terminal records by name, artefact names, parsed sidecar).

        READ INSIDE THE TEMPORARY DIRECTORY, because handing back paths from a
        directory that has already been removed is a test that asserts on
        nothing but FileNotFoundError.
        """
        with tempfile.TemporaryDirectory() as root:
            _voice_dir(root)
            queue = Path(root) / "queue"
            (queue / "pending").mkdir(parents=True)
            (queue / "pending" / "abc123.json").write_text(
                json.dumps({"params": params}), encoding="utf-8")
            rt = controller.FakeRuntime(Path(root) / "voice_embedding",
                                        frame_seconds=0.0, frames=frames)
            ref = [None]
            service = svc.Service(queue, controller.manifest(
                TWENTY, SETTINGS, DEFAULTS, 24000, "fake", 1.0, 0),
                controller.build_handler(rt, SETTINGS, DEFAULTS, ref),
                idle_exit_seconds=0.01, poll_seconds=0.01)
            ref[0] = service
            service.run(once=True, watch_stdin_thread=False)
            done = queue / "done"
            records = {p.name: json.loads(p.read_text(encoding="utf-8"))
                       for p in done.glob("*.json")
                       if p.name.endswith((".done.json", ".failed.json",
                                           ".cancelled.json"))}
            sidecar_path = done / "abc123.timings.json"
            sidecar = (json.loads(sidecar_path.read_text(encoding="utf-8"))
                       if sidecar_path.exists() else None)
            parts = {p.name: p.stat().st_size for p in done.glob("*.f32")}
            return records, parts, sidecar

    def test_a_good_job_lands_done_with_the_grid_it_came_off(self):
        records, _parts, _sidecar = self._run_one(
            {"segments": ["hello there"], "voice": "pt_male"}, frames=4)
        self.assertIn("abc123.done.json", records,
                      "expected a done record, got %s" % sorted(records))
        rec = records["abc123.done.json"]
        self.assertEqual(rec["sample_rate"], 24000)
        self.assertEqual(rec["frame_rate"], 12.5)
        self.assertEqual(rec["frames"], 4)
        # THE ASSERTION THE WHOLE SIDECAR EXISTS FOR. frames / frame_rate is the
        # duration exactly, on a 12.5 Hz grid nothing else on this stack has.
        # A resample anywhere between the codec and here breaks it.
        self.assertAlmostEqual(rec["audio_seconds"],
                               rec["frames"] / rec["frame_rate"], places=3)
        self.assertEqual(rec["voice"], "pt_male")
        self.assertEqual(rec["flow_steps"], 32)

    def test_the_settings_that_produced_the_sound_are_in_the_sidecar(self):
        """A tuning log where two rows differ and nothing says how is a tuning
        log nobody can act on."""
        _records, _parts, sidecar = self._run_one(
            {"segments": ["one", "two"], "voice": "de_female", "flow_steps": 16},
            frames=3)
        self.assertEqual(sidecar["flow_steps"], 16)
        self.assertEqual(sidecar["cfg_alpha"], 1.2)
        self.assertEqual(sidecar["voice"], "de_female")
        self.assertEqual(sidecar["settings"]["fade_ms"], 120.0)
        self.assertEqual(len(sidecar["segments"]), 2)
        for row in sidecar["segments"]:
            self.assertEqual(row["frames"], 3)
            self.assertEqual(row["frame_rate"], 12.5)
            self.assertIn("peak", row)

    def test_a_refused_job_fails_before_the_model_is_ever_loaded(self):
        """A job that is going to be refused must be refused while its client is
        still holding the submit, not after a 63-second load and three and a half
        minutes on a card somebody wanted back."""
        records, _parts, _sidecar = self._run_one(
            {"segments": ["hi"], "voice": "pt_male", "exaggeration": 0.4})
        self.assertIn("abc123.failed.json", records,
                      "expected a failed record, got %s" % sorted(records))
        rec = records["abc123.failed.json"]
        self.assertIn("exaggeration", rec["error"])

    def test_the_artefacts_are_one_per_segment_at_four_bytes_a_sample(self):
        _records, parts, _sidecar = self._run_one(
            {"segments": ["one", "two", "three"], "voice": "es_male"}, frames=2)
        self.assertEqual(sorted(parts),
                         ["abc123.0.f32", "abc123.1.f32", "abc123.2.f32"])
        for size in parts.values():
            self.assertEqual(size, 2 * 1920 * 4)


if __name__ == "__main__":
    unittest.main()
