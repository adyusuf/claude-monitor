"""Decisions asked on the live board (docs/live-board.md): the needs_decision status,
the user's click, and how it reaches Claude — injected mid-turn, or through the Stop hook,
which waits a bounded time for it. Also: a subagent's tool call never consumes a notice
meant for the orchestrator (modes C/D/E)."""
import io
import json
import os
import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "board"))

import board  # noqa: E402
import board_config  # noqa: E402
import board_hook  # noqa: E402
import board_server  # noqa: E402
from board_store import fold, read_events  # noqa: E402
from board_control import apply_control, peek_changes, read_control  # noqa: E402


class DecisionCase(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.bdir = Path(self.tmp.name)
        patcher = mock.patch.dict(os.environ, {"BOARD_DIR": self.tmp.name})
        patcher.start()
        self.addCleanup(patcher.stop)

    def cli(self, *argv):
        return board.run(list(argv))

    def ask(self, tid="T-1", *options):
        self.cli("add", tid, "split the PR?")
        extra = ["--options", "|".join(options)] if options else []
        self.cli("set", tid, "--status", "needs_decision", "--note", "one PR or two?", *extra)

    def task(self, tid="T-1"):
        return fold(read_events(self.bdir), read_control(self.bdir))["tasks"][tid]

    def stop(self, wait, session="s1", poll=0.05):
        return board_hook.wait_for_decision(self.bdir, session, wait, poll)


class StoreAndCli(DecisionCase):
    def test_a_question_carries_its_options_and_the_answer_folds_onto_it(self):
        self.ask("T-1", "split", "keep one")
        self.assertEqual(self.task()["options"], ["split", "keep one"])
        self.assertIsNone(self.task()["decision"])
        apply_control(self.bdir, "decide", "T-1", "split", "tests first")
        decision = self.task()["decision"]
        self.assertEqual((decision["choice"], decision["note"]), ("split", "tests first"))

    def test_asking_again_hides_the_old_answer(self):
        self.ask()
        apply_control(self.bdir, "decide", "T-1", "continue")
        with mock.patch("board_store.now_iso", return_value="2999-01-01T00:00:00+00:00"):
            self.cli("set", "T-1", "--note", "and now?")
        self.assertIsNone(self.task()["decision"])

    def test_a_decision_without_a_choice_is_refused(self):
        with self.assertRaises(ValueError):
            apply_control(self.bdir, "decide", "T-1", "")

    def test_cli_rejects_empty_or_overlong_options(self):
        with mock.patch.object(sys, "stderr", io.StringIO()):
            for bad in ("|", "x" * 65):
                with self.assertRaises(SystemExit):
                    self.cli("set", "T-1", "--options", bad)

    def test_a_typo_in_the_wait_setting_falls_back_to_the_default(self):
        with mock.patch.dict(os.environ, {"BOARD_DECISION_WAIT": "three minutes"}):
            self.assertEqual(board_config._int_env("BOARD_DECISION_WAIT", 180), 180)


class StopHookWaits(DecisionCase):
    def test_no_open_question_ends_the_turn_at_once(self):
        start = time.monotonic()
        self.assertIsNone(self.stop(wait=5))
        self.assertLess(time.monotonic() - start, 1)

    def test_a_click_during_the_wait_keeps_the_turn_going(self):
        self.ask()
        threading.Timer(0.3, apply_control, (self.bdir, "decide", "T-1", "continue", "go")).start()
        out = self.stop(wait=5)
        self.assertEqual(out["decision"], "block")
        self.assertIn("decided T-1: continue — note: go", out["reason"])
        self.assertEqual(peek_changes(self.bdir, "s1"), [])  # delivered once, not again
        kinds = [e["type"] for e in read_events(self.bdir)]
        self.assertEqual(kinds[-1], "turn_start")

    def test_no_click_in_time_lets_the_turn_end(self):
        self.ask()
        start = time.monotonic()
        self.assertIsNone(self.stop(wait=0.4))
        self.assertGreaterEqual(time.monotonic() - start, 0.4)

    def test_a_click_made_while_claude_was_working_is_handed_over_even_without_waiting(self):
        self.ask()
        apply_control(self.bdir, "decide", "T-1", "reject")
        out = self.stop(wait=0)
        self.assertIn("decided T-1: reject", out["reason"])

    def test_other_changes_do_not_prolong_the_turn_and_stay_for_the_next_one(self):
        apply_control(self.bdir, "remove_task", "T-9")
        self.assertIsNone(self.stop(wait=0))
        self.assertEqual([c["value"] for c in peek_changes(self.bdir, "s1")], ["T-9"])

    def test_the_stop_event_prints_a_top_level_block(self):
        self.ask()
        apply_control(self.bdir, "decide", "T-1", "continue")
        payload = {"hook_event_name": "Stop", "session_id": "s1", "last_assistant_message": ""}
        with mock.patch.object(sys, "stdin", io.StringIO(json.dumps(payload))), \
                mock.patch.object(sys, "stdout", io.StringIO()) as out:
            board_hook.main()
        self.assertEqual(json.loads(out.getvalue())["decision"], "block")


class SubagentsDoNotSwallowNotices(DecisionCase):
    def post(self, **extra):
        return board_hook.handle({"hook_event_name": "PostToolUse", "session_id": "s1",
                                  "tool_name": "Bash", "tool_input": {}, "tool_response": {}, **extra})

    def test_only_the_orchestrator_consumes_a_board_change(self):
        apply_control(self.bdir, "remove_task", "T-2")
        self.assertIsNone(self.post(agent_id="sub-1", agent_type="developer"))
        out = self.post()
        self.assertIn("removed task T-2", out["hookSpecificOutput"]["additionalContext"])


class ServerValidation(unittest.TestCase):
    def test_a_decision_needs_a_clean_choice_and_a_short_note(self):
        self.assertEqual(board_server.decision_fields({"choice": " split ", "note": " ok "}), ("split", "ok"))
        for bad in ({}, {"choice": ""}, {"choice": "x" * 65}, {"choice": "a\nb"},
                    {"choice": "ok", "note": "n" * 501}, {"choice": "ok", "note": 5}):
            with self.assertRaises(ValueError):
                board_server.decision_fields(bad)
        self.assertEqual(board_server.validate_control({"action": "decide", "value": "T-4"}),
                         ("decide", "T-4"))


if __name__ == "__main__":
    unittest.main()
