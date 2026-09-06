"""The agent talks to a controller down one pipe. This is that pipe.

WHY THIS FILE EXISTS. The C# side has 400 tests and none of them can reach
Python. The stdin protocol is the one place the two halves have to agree on a
wire format, and it now carries two verbs rather than one, so a change on either
side that silently stops the other hearing it would show up as "the cap moved and
the job did not" - which reads as a kernel problem and is not one.

Run it with anything: python -m unittest discover services/tests, or pytest.
It imports the shipped library and needs nothing else - no torch, no GPU, no
agent, no network.
"""

import io
import os
import sys
import threading
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "lib"))

import idlegpu_service as svc


class StdinProtocol(unittest.TestCase):
    def setUp(self):
        svc.YIELD.clear()
        svc.CPU_THREADS[0] = 0
        svc._THREADS_SEEN.clear()
        self._real_stdin = sys.stdin

    def tearDown(self):
        sys.stdin = self._real_stdin
        svc.YIELD.clear()
        svc.CPU_THREADS[0] = 0
        svc._THREADS_SEEN.clear()

    def feed(self, text):
        """Send these lines and then close the pipe, like a departing agent."""
        sys.stdin = io.StringIO(text)
        t = threading.Thread(target=svc.watch_stdin)
        t.start()
        t.join(timeout=5)
        self.assertFalse(t.is_alive(), "the stdin watcher did not finish")

    def feed_open(self, text):
        """Send these lines and LEAVE THE PIPE OPEN, like a live agent.

        Closing it is itself an instruction - end of input is the dead man's
        switch - so a test that wants to know whether a VERB set the yield flag
        has to keep the pipe open, or it is only ever measuring the close.
        """
        gate = threading.Event()

        class HeldOpen(object):
            def __init__(self, body):
                self._lines = body.splitlines(True)

            def __iter__(self):
                for line in self._lines:
                    yield line
                gate.wait(5)          # the agent is still there, saying nothing

        sys.stdin = HeldOpen(text)
        t = threading.Thread(target=svc.watch_stdin)
        t.daemon = True
        t.start()
        # Long enough for the watcher to have consumed everything and be waiting.
        threading.Event().wait(0.2)

        def release():
            """Close the pipe AND wait for the watcher to notice.

            Not joining here made this file flaky, and the flakiness was a
            miniature of the bug it is testing for: the released thread reached
            end of input and set the yield flag AFTER the next test's setUp had
            cleared it, so a later test failed for something an earlier one did.
            A test that leaves a thread running is a test that reports on its
            neighbour.
            """
            gate.set()
            t.join(timeout=5)

        return release

    def test_a_threads_line_moves_the_thread_count(self):
        """The agent's cap moved, so the thread count should move with it.

        Sixteen threads inside a tenth of a machine finish no sooner than two
        and evict far more of the owner's cache on the way. This cannot be an
        environment variable, because those are frozen at process start: a job
        that came up while nobody was signed in would keep a thread per core
        after the owner sat down.
        """
        self.assertEqual(svc.wanted_threads(), 0, "nothing asked for yet")
        release = self.feed_open("THREADS 4\n")
        self.assertEqual(svc.wanted_threads(), 4)
        self.assertFalse(svc.YIELD.is_set(),
                         "a thread change is not a yield; the job is throttled, not stopped")
        release()

    def test_the_last_threads_line_wins(self):
        """The owner can move the limits as often as they like."""
        self.feed("THREADS 8\nTHREADS 2\nTHREADS 6\n")
        self.assertEqual(svc.wanted_threads(), 6)

    def test_zero_and_rubbish_are_ignored_rather_than_obeyed(self):
        """Zero threads is not a smaller job, it is no job.

        A malformed line must leave the controller exactly as it was rather than
        stopping it: the kernel's cap is the promise, and this is the
        optimisation beside it. Degrading to the previous thread count is
        correct; degrading to zero is a hang.
        """
        self.feed("THREADS 4\nTHREADS 0\nTHREADS -3\nTHREADS abc\nTHREADS\n")
        self.assertEqual(svc.wanted_threads(), 4)

    def test_yield_still_stops_and_still_wins(self):
        """The verb that was there first must not have been broken by the new one."""
        self.feed("THREADS 4\nYIELD\n")
        self.assertTrue(svc.YIELD.is_set())
        self.assertEqual(svc.wanted_threads(), 4)

    def test_an_unknown_verb_is_ignored_not_fatal(self):
        """A NEWER AGENT MUST NOT KILL AN OLDER CONTROLLER.

        The agent and the controllers are deployed separately - the controller
        scripts live in the services directory and the agent is one exe beside
        them - so a mixed install is normal rather than exceptional. An unknown
        line degrades to the behaviour the controller already had.
        """
        release = self.feed_open("SOMETHING-NEW 1\nTHREADS 3\n")
        self.assertEqual(svc.wanted_threads(), 3)
        self.assertFalse(svc.YIELD.is_set(), "an unknown verb does not stop the job")
        release()

    def test_end_of_input_is_the_dead_man_switch(self):
        """The agent is gone, so the controller must not become an orphan.

        The kernel closes this pipe when the agent's process object is torn
        down, so a crashed agent cannot leave a process behind holding a model.
        The job object is the other guarantee; this one costs a flag.
        """
        self.feed("THREADS 4\n")
        self.assertTrue(svc.YIELD.is_set(), "end of stdin means stop")


if __name__ == "__main__":
    unittest.main(verbosity=2)
