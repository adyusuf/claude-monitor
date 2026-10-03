"""The plan reminder (T-23): a session that works with the board enabled but never plans on it is told
once, and only once. Seen live 03/10/2026: 60+ tool calls, an empty board page."""
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

BOARD = Path(__file__).resolve().parent.parent / "board"
sys.path.insert(0, str(BOARD))

import board_hook  # noqa: E402
import board_nudge  # noqa: E402
from board_store import append_event  # noqa: E402


class NudgeTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.bdir = Path(self.tmp.name) / "board"
        self.bdir.mkdir()
        patcher = mock.patch.object(board_nudge, "NUDGE_AFTER", 3)
        patcher.start()
        self.addCleanup(patcher.stop)

    def calls(self, session, n):
        return [board_nudge.tick(self.bdir, session) for _ in range(n)]

    def test_nothing_before_the_threshold_then_exactly_one_reminder(self):
        out = self.calls("s1", 8)
        self.assertEqual(out[:2], [None, None])
        self.assertIn("board-plan", out[2])
        self.assertIn("3 tool calls", out[2])
        self.assertEqual(out[3:], [None] * 5)

    def test_a_session_that_planned_on_the_board_is_not_nudged(self):
        append_event(self.bdir, {"type": "task_session", "session": "s1", "id": "T-1"})
        self.assertEqual(self.calls("s1", 6), [None] * 6)

    def test_another_sessions_plan_does_not_excuse_this_one(self):
        append_event(self.bdir, {"type": "task_session", "session": "other", "id": "T-1"})
        self.assertIsNotNone(self.calls("s1", 3)[2])

    def test_sessions_are_counted_separately(self):
        self.calls("a", 2)
        self.assertEqual(self.calls("b", 2), [None, None])
        self.assertIsNotNone(board_nudge.tick(self.bdir, "a"))

    def test_it_can_be_turned_off(self):
        with mock.patch.object(board_nudge, "NUDGE_AFTER", 0):
            self.assertEqual(self.calls("s1", 20), [None] * 20)
        self.assertFalse((self.bdir / board_nudge.STATE_FILE).exists())

    def test_no_session_id_means_no_reminder(self):
        self.assertEqual(self.calls("", 5), [None] * 5)

    def test_a_damaged_state_file_starts_over_instead_of_breaking_the_call(self):
        (self.bdir / board_nudge.STATE_FILE).write_text("{not json", encoding="utf-8")
        self.assertEqual(self.calls("s1", 2), [None, None])
        (self.bdir / board_nudge.STATE_FILE).write_text("[1, 2]", encoding="utf-8")
        self.assertIsNotNone(self.calls("s1", 3)[2])

    def test_an_unwritable_state_never_breaks_a_tool_call(self):
        with mock.patch.object(board_nudge, "atomic_write", side_effect=OSError("read-only")):
            self.assertEqual(self.calls("s1", 5), [None] * 5)

    def test_only_the_newest_sessions_are_remembered(self):
        with mock.patch.object(board_nudge, "NUDGE_SESSIONS_KEPT", 3):
            for n in range(6):
                board_nudge.tick(self.bdir, f"s{n}")
        kept = json.loads((self.bdir / board_nudge.STATE_FILE).read_text(encoding="utf-8"))
        self.assertEqual(list(kept), ["s3", "s4", "s5"])

    def test_the_state_file_is_private(self):
        board_nudge.tick(self.bdir, "s1")
        mode = (self.bdir / board_nudge.STATE_FILE).stat().st_mode & 0o777
        self.assertEqual(mode, 0o600)


class HookTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.bdir = Path(self.tmp.name) / "board"
        self.bdir.mkdir()
        env = mock.patch.dict(os.environ, {"BOARD_DIR": str(self.bdir)})
        env.start()
        self.addCleanup(env.stop)
        patcher = mock.patch.object(board_nudge, "NUDGE_AFTER", 3)
        patcher.start()
        self.addCleanup(patcher.stop)

    def post(self, **extra):
        return board_hook.handle({"hook_event_name": "PostToolUse", "session_id": "s1", "tool_name": "Read",
                                  "tool_input": {}, **extra})

    def test_the_reminder_arrives_as_post_tool_use_context_once(self):
        self.assertIsNone(self.post())
        self.assertIsNone(self.post())
        out = self.post()
        context = out["hookSpecificOutput"]
        self.assertEqual(context["hookEventName"], "PostToolUse")
        self.assertIn("board-plan", context["additionalContext"])
        self.assertIsNone(self.post())

    def test_a_subagents_tool_calls_do_not_count(self):
        for _ in range(6):
            self.assertIsNone(self.post(agent_id="sub-1"))
        self.assertFalse((self.bdir / board_nudge.STATE_FILE).exists())

    def test_planning_through_the_cli_in_the_same_session_silences_it(self):
        self.post()
        board_hook.handle({"hook_event_name": "PostToolUse", "session_id": "s1", "tool_name": "Bash",
                           "tool_input": {"command": "python3 board.py add T-1 first"}})
        self.assertIsNone(self.post())
        self.assertIsNone(self.post())


if __name__ == "__main__":
    unittest.main()
