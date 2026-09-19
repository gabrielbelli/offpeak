"""One controller, two checkpoints, and the ways a second engine goes wrong.

WHY THIS FILE EXISTS. Turbo's generate() ACCEPTS exaggeration and cfg_weight and
then throws them away with a logged warning, because the checkpoint sets
hp.emotion_adv = False so the emotion conditioning layer is never built, and
inference_turbo has no classifier-free-guidance path at all. It has no
language_id parameter of any kind. Every one of those failures is silent from the
outside: the job succeeds, the audio comes back, and it ignored most of what was
asked for. Nothing in the C# tests can reach any of it.

Run it with anything: python -m unittest discover services/chatterbox/tests, or
pytest. It imports the controller and needs NO torch, no GPU, no agent, no
network and no model weights - the controller only imports torch inside
Runtime.load(), which nothing here calls.
"""

import os
import sys
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(_HERE, "..", "..", "lib"))
sys.path.insert(0, os.path.join(_HERE, ".."))

import controller                                                   # noqa: E402


class FakeRuntimeShape(object):
    """Everything check_params reads off a Runtime, and nothing else."""

    def __init__(self, engine, sample_rate=24000):
        self.engine = engine
        self.spec = controller.ENGINES[engine]
        self.sample_rate = sample_rate


class TheEngineTable(unittest.TestCase):
    def test_turbo_manifest_declares_no_language_exaggeration_or_cfg_weight(self):
        """THE HOUSE RULE, ASSERTED. Every field is honoured or refused by name;
        none is accepted and dropped. A schema that listed exaggeration for turbo
        would be this controller promising expression it cannot deliver."""
        m = controller.manifest("turbo", 24000, "cuda", 4.0, 3805)
        schema = m["params_schema"]
        for absent in ("language", "exaggeration", "cfg_weight"):
            self.assertNotIn(
                absent, schema,
                "turbo's manifest must not advertise %r: generate() would accept it "
                "and discard it" % absent)
        self.assertIn("temperature", schema, "temperature genuinely survives on turbo")
        self.assertIn("segments", schema)
        self.assertIn("reference_sha256", schema)

    def test_the_multilingual_manifest_still_declares_all_three(self):
        """The other engine has not lost anything on the way past."""
        m = controller.manifest("multilingual", 24000, "cuda", 8.0, 4199)
        schema = m["params_schema"]
        for present in ("language", "exaggeration", "cfg_weight", "temperature"):
            self.assertIn(present, schema)

    def test_turbo_publishes_the_five_second_reference_minimum(self):
        """Turbo asserts this inside prepare_conditionals, so a shorter clip fails
        the job. Published so a client can refuse before it submits rather than
        after a job id exists."""
        self.assertEqual(controller.manifest("turbo", 24000, "cuda", 4.0, 0)
                         ["min_reference_seconds"], 5.0)
        self.assertEqual(controller.manifest("multilingual", 24000, "cuda", 8.0, 0)
                         ["min_reference_seconds"], 0.0)

    def test_turbo_seeds_a_lower_unit_seconds_on_the_card_but_not_on_the_processor(self):
        """unit_seconds is the floor on how long after a polite request the machine
        can still be busy, and it is only ever corrected UPWARDS. Turbo is 2.36x
        quicker on a 3070, so the card's seed comes down. Turbo on a PROCESSOR has
        never been measured, so inventing a smaller number there would understate
        that floor to every client until the first segment - which is the direction
        that costs somebody their game."""
        turbo = controller.ENGINES["turbo"]["unit_seconds"]
        base = controller.ENGINES["multilingual"]["unit_seconds"]
        self.assertLess(turbo["cuda"], base["cuda"])
        self.assertEqual(turbo["cpu"], base["cpu"])


class TheManifestId(unittest.TestCase):
    def setUp(self):
        self._had = os.environ.get("OFFPEAK_SERVICE_ID")

    def tearDown(self):
        if self._had is None:
            os.environ.pop("OFFPEAK_SERVICE_ID", None)
        else:
            os.environ["OFFPEAK_SERVICE_ID"] = self._had

    def test_manifest_id_comes_from_the_service_id_not_a_literal(self):
        """THE DEFECT THIS PREVENTS. Nothing reconciles the id in worker.ini with
        the id inside the manifest: the agent splices the manifest in verbatim
        under a key labelled with the section id. A controller copied to make a
        second engine, with the first engine's id left in, publishes
            {"id":"chatterbox-turbo", ..., "manifest":{"id":"chatterbox"}}
        and every layer is satisfied while anything routing on the manifest id
        hands back the other engine."""
        os.environ["OFFPEAK_SERVICE_ID"] = "speech-on-the-other-box"
        self.assertEqual(controller.manifest("turbo", 24000, "cuda", 4.0, 0)["id"],
                         "speech-on-the-other-box")
        self.assertEqual(controller.manifest("multilingual", 24000, "cuda", 8.0, 0)["id"],
                         "speech-on-the-other-box")

    def test_the_two_engines_do_not_default_to_the_same_id(self):
        """Run by hand outside the agent there is no OFFPEAK_SERVICE_ID, and the
        fallback must still tell the two apart."""
        os.environ.pop("OFFPEAK_SERVICE_ID", None)
        self.assertEqual(controller.service_id("multilingual"), "chatterbox")
        self.assertEqual(controller.service_id("turbo"), "chatterbox-turbo")


class RefusingWhatItCannotHonour(unittest.TestCase):
    def test_turbo_fails_a_job_carrying_an_undeclared_param(self):
        """THE LAST LINE OF DEFENCE, AND THE ONLY ONE THAT SURVIVES A VERSION SKEW.

        The server that submits these jobs runs somewhere else and can be older
        than this file. An older one sends exaggeration, cfg_weight and language to
        every speech service it knows, because until turbo existed every speech
        service honoured all three. Ignoring them here would tell that server the
        job succeeded and hand its caller audio that ignored most of the request.
        """
        rt = FakeRuntimeShape("turbo")
        for bad in ("exaggeration", "cfg_weight", "language"):
            with self.assertRaises(ValueError) as caught:
                controller.check_params(rt, {"segments": ["hello"], bad: 0.3})
            message = str(caught.exception)
            self.assertIn(bad, message,
                          "the refusal must NAME the field, or nobody can fix it")
            self.assertIn("chatterbox-turbo", message,
                          "and name the engine that cannot honour it")

    def test_a_turbo_job_with_only_what_turbo_honours_is_accepted(self):
        rt = FakeRuntimeShape("turbo")
        controller.check_params(rt, {
            "segments": ["hello there"],
            "reference_sha256": "a" * 64,
            "temperature": 0.6,
            "sample_rate": 24000,
        })

    def test_the_controllers_own_working_state_is_not_refused(self):
        """_reference_path is set by the handler after resolving the digest. It
        never came off the wire and must not be mistaken for a client field."""
        rt = FakeRuntimeShape("turbo")
        controller.check_params(rt, {"segments": ["hi"], "_reference_path": "C:\\x.wav"})

    def test_the_deployed_baseline_wire_is_not_broken_by_strictness(self):
        """EXACTLY WHAT THE DEPLOYED SERVER SENDS TODAY, from tts-long's
        remote.py. This service has been live for months against this body and
        `sample_rate` has never been in its schema. Turning strictness on for
        baseline would fail every job in production to close a hole nobody has
        walked through, so baseline is not strict and this asserts that the
        decision is deliberate rather than forgotten."""
        rt = FakeRuntimeShape("multilingual")
        controller.check_params(rt, {
            "segments": ["one", "two"],
            "language": "en",
            "exaggeration": 0.3,
            "cfg_weight": 0.3,
            "temperature": 0.6,
            "sample_rate": 24000,
            "reference_sha256": "b" * 64,
        })

    def test_a_rate_the_model_cannot_produce_is_refused_on_both_engines(self):
        """DECLARED MEANS CHECKED. sample_rate is in both schemas, so a client
        naming a rate this checkpoint does not produce is told so rather than
        handed samples at another rate to assemble wrongly."""
        for engine in ("turbo", "multilingual"):
            rt = FakeRuntimeShape(engine, sample_rate=24000)
            with self.assertRaises(ValueError) as caught:
                controller.check_params(rt, {"segments": ["hi"], "sample_rate": 48000})
            self.assertIn("48000", str(caught.exception))


class WhatReachesGenerate(unittest.TestCase):
    """speak() builds its keyword arguments from the engine table, so the test is
    what the table produces rather than what a comment claims."""

    class Recorder(object):
        def __init__(self):
            self.kwargs = None

        def generate(self, text, **kwargs):
            self.kwargs = dict(kwargs)
            raise self.Stop()

        class Stop(Exception):
            pass

    def _kwargs_for(self, engine, params):
        rt = controller.Runtime.__new__(controller.Runtime)
        rt.engine = engine
        rt.spec = controller.ENGINES[engine]
        rt.resolved = "cuda"
        rt.threads = 0
        rec = self.Recorder()
        rt.model = rec
        try:
            rt.speak("hello", params)
        except self.Recorder.Stop:
            pass
        return rec.kwargs

    def test_turbo_receives_no_language_exaggeration_or_cfg_weight_at_all(self):
        """ABSENT, NOT None. generate() has default values for exaggeration and
        cfg_weight and no language_id parameter whatsoever, so passing None for the
        first two would still trip its own warning path and passing the third would
        raise TypeError."""
        kwargs = self._kwargs_for("turbo", {"temperature": 0.6})
        for absent in ("language_id", "exaggeration", "cfg_weight"):
            self.assertNotIn(absent, kwargs)
        self.assertEqual(kwargs["temperature"], 0.6)

    def test_a_turbo_default_is_never_invented_for_a_control_it_lacks(self):
        """With nothing supplied at all, turbo must still receive only what it
        honours. A deployment default for exaggeration reaching generate() here
        would be a value nobody typed, discarded silently."""
        kwargs = self._kwargs_for("turbo", {})
        self.assertEqual(sorted(kwargs), ["audio_prompt_path", "temperature"])

    def test_multilingual_still_receives_every_control_it_always_did(self):
        kwargs = self._kwargs_for("multilingual", {})
        self.assertEqual(kwargs["language_id"], "en")
        self.assertEqual(kwargs["exaggeration"], 0.5)
        self.assertEqual(kwargs["cfg_weight"], 0.5)
        self.assertEqual(kwargs["temperature"], 0.8)

    def test_a_supplied_value_beats_the_default_on_both_engines(self):
        kwargs = self._kwargs_for("multilingual", {"language": "pt", "cfg_weight": 0.1})
        self.assertEqual(kwargs["language_id"], "pt")
        self.assertEqual(kwargs["cfg_weight"], 0.1)


class NoBranchOnAnEngineName(unittest.TestCase):
    def test_speak_and_check_params_never_test_for_turbo_by_name(self):
        """A third engine must be a row in ENGINES and no code. If a branch on the
        NAME creeps back in, adding one costs an audit of this file instead.

        Read from the SYNTAX TREE and not the text, so a comment that mentions
        turbo - and the comments here have to, because what turbo cannot do is
        exactly what needs explaining - is not mistaken for a branch on it."""
        import ast
        import inspect
        import textwrap

        names = set(controller.ENGINES)
        for func in (controller.Runtime.speak, controller.check_params,
                     controller.params_schema, controller.manifest):
            tree = ast.parse(textwrap.dedent(inspect.getsource(func)))
            body = tree.body[0].body
            if body and isinstance(body[0], ast.Expr) and \
                    isinstance(body[0].value, ast.Constant):
                body = body[1:]                    # the docstring is prose too
            found = [n.value for stmt in body for n in ast.walk(stmt)
                     if isinstance(n, ast.Constant) and n.value in names]
            self.assertEqual(
                found, [],
                "%s names an engine in its code (%s); a third engine must be a row "
                "in ENGINES and no branch" % (func.__name__, found))


if __name__ == "__main__":
    unittest.main()
