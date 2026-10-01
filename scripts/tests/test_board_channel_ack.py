"""The channel ledger and registry (scripts/board/board_channel_ack.py, board_channel_reg.py,
docs/live-board.md §5): a task pushed into an idle session is held back from the hooks
only while the push is believed to have worked; reachability is claimed only when verified."""
import json
import os
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "board"))

import board_channel_ack as ack  # noqa: E402
import board_channel_reg as reg  # noqa: E402
import board_hook  # noqa: E402
from board_config import (ACK_FILE, CHANNEL_CONFIRM_S, CHANNEL_DELIVERED_FILE, CHANNEL_DELIVERED_KEPT,  # noqa: E402
                          CHANNEL_DIR, CHANNEL_TTL_S, ControlAction)
from board_store import append_event, read_control, record_change  # noqa: E402

S1, S2 = "11111111-aaaa", "22222222-bbbb"


class BoardCase(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.bdir = Path(self.tmp.name) / ".claude" / "board"
        patcher = mock.patch.dict(os.environ, {"BOARD_DIR": str(self.bdir)})
        patcher.start()
        self.addCleanup(patcher.stop)
        for sid in (S1, S2):
            append_event(self.bdir, {"type": "turn_stop", "session": sid})

    def queue(self, session, text="do the thing", action=ControlAction.QUEUE_TASK):
        ctl = record_change(self.bdir, read_control(self.bdir),
                            {"action": action, "value": session, "session": session, "text": text})
        return ctl["version"]


class LedgerTests(BoardCase):
    def test_a_fresh_push_is_held_back_then_released_when_nothing_confirms_it(self):
        ack.mark(self.bdir, S1, 3, now=1000.0)
        self.assertEqual(ack.held_back(self.bdir, S1, now=1000.0 + CHANNEL_CONFIRM_S - 1), {3})
        self.assertEqual(ack.held_back(self.bdir, S1, now=1000.0 + CHANNEL_CONFIRM_S), set())

    def test_a_confirmed_push_stays_held_back_for_good(self):
        ack.mark(self.bdir, S1, 3, now=1000.0)
        ack.confirm(self.bdir, S1, now=1001.0)
        self.assertEqual(ack.held_back(self.bdir, S1, now=1000.0 + 10 * CHANNEL_CONFIRM_S), {3})

    def test_confirm_only_covers_pushes_made_before_it(self):
        ack.mark(self.bdir, S1, 3, now=1000.0)
        ack.confirm(self.bdir, S1, now=999.0)
        self.assertEqual(ack.held_back(self.bdir, S1, now=1000.0 + 2 * CHANNEL_CONFIRM_S), set())

    def test_sessions_are_independent_and_unknown_ones_hold_nothing(self):
        ack.mark(self.bdir, S1, 3, now=1000.0)
        ack.confirm(self.bdir, S2, now=1001.0)
        self.assertEqual(ack.held_back(self.bdir, S2, now=1001.0), set())
        self.assertEqual(ack.held_back(self.bdir, S1, now=1001.0), {3})

    def test_pushed_lists_every_version_ever_marked_whatever_its_state(self):
        ack.mark(self.bdir, S1, 3, now=1000.0)
        ack.mark(self.bdir, S1, 5, now=1000.0)
        ack.confirm(self.bdir, S1, now=1001.0)
        ack.mark(self.bdir, S1, 7, now=time.time() - 10 * CHANNEL_CONFIRM_S)
        self.assertEqual(ack.pushed(self.bdir, S1), {3, 5, 7})
        self.assertEqual(ack.pushed(self.bdir, S2), set())

    def test_unmark_takes_a_push_back_and_tolerates_unknowns(self):
        ack.mark(self.bdir, S1, 3, now=1000.0)
        ack.unmark(self.bdir, S1, 3)
        ack.unmark(self.bdir, S1, 99)
        ack.unmark(self.bdir, S2, 3)
        self.assertEqual(ack.held_back(self.bdir, S1, now=1000.0), set())

    def test_only_the_newest_marks_are_kept(self):
        for v in range(CHANNEL_DELIVERED_KEPT + 5):
            ack.mark(self.bdir, S1, v, now=1000.0)
        kept = ack.held_back(self.bdir, S1, now=1000.0)
        self.assertEqual(len(kept), CHANNEL_DELIVERED_KEPT)
        self.assertNotIn(0, kept)
        self.assertIn(CHANNEL_DELIVERED_KEPT + 4, kept)

    def test_a_damaged_ledger_holds_nothing_and_is_rewritten(self):
        path = self.bdir / CHANNEL_DELIVERED_FILE
        for garbage in ("{not json", "[1, 2]", json.dumps({S1: "x"}), json.dumps({S1: {"3": "x", "no": {}}})):
            path.write_text(garbage)
            self.assertEqual(ack.held_back(self.bdir, S1, now=1000.0), set(), garbage)
            ack.confirm(self.bdir, S1, now=1000.0)
        ack.mark(self.bdir, S1, 4, now=1000.0)
        self.assertEqual(ack.held_back(self.bdir, S1, now=1000.0), {4})


class FilterTests(BoardCase):
    def test_only_this_sessions_delivered_task_is_filtered(self):
        mine, theirs = self.queue(S1), self.queue(S2)
        general = record_change(self.bdir, read_control(self.bdir),
                                {"action": ControlAction.REMOVE_TASK, "value": "T-1"})["version"]
        for v in (mine, theirs, general):
            ack.mark(self.bdir, S1, v, now=time.time())
        seen = [c["v"] for c in ack.peek_changes(self.bdir, S1)]
        self.assertEqual(seen, [theirs, general])

    def test_unseen_changes_returns_the_filtered_list_and_marks_it_seen(self):
        held, free = self.queue(S1), self.queue(S1, "second")
        ack.mark(self.bdir, S1, held, now=time.time())
        got = ack.unseen_changes(self.bdir, S1)
        self.assertEqual([c["v"] for c in got], [free])
        self.assertEqual(json.loads((self.bdir / ACK_FILE).read_text())[S1], free)
        self.assertEqual(ack.unseen_changes(self.bdir, S1), [])

    def test_an_unconfirmed_push_comes_back_to_the_hooks_after_the_grace(self):
        v = self.queue(S1)
        ack.mark(self.bdir, S1, v, now=time.time() - 10 * CHANNEL_CONFIRM_S)
        self.assertEqual([c["v"] for c in ack.peek_changes(self.bdir, S1)], [v])


class HookTests(BoardCase):
    def hook(self, event, session=S1):
        return board_hook.handle({"hook_event_name": event, "session_id": session, "cwd": self.tmp.name})

    def test_a_task_the_channel_pushed_is_not_delivered_again_and_is_confirmed_by_the_turn(self):
        v = self.queue(S1, "ship it")
        ack.mark(self.bdir, S1, v, now=time.time())
        self.assertIsNone(self.hook("UserPromptSubmit"))
        later = time.time() + 10 * CHANNEL_CONFIRM_S
        self.assertEqual(ack.held_back(self.bdir, S1, now=later), {v})

    def test_a_task_the_channel_never_pushed_still_reaches_the_hook(self):
        self.queue(S1, "ship it")
        out = self.hook("UserPromptSubmit")
        self.assertIn("ship it", out["hookSpecificOutput"]["additionalContext"])

    def test_the_stop_hook_confirms_too(self):
        v = self.queue(S1)
        ack.mark(self.bdir, S1, v, now=time.time())
        with mock.patch.object(board_hook, "DECISION_WAIT_S", 0):
            self.assertIsNone(self.hook("Stop"))
        self.assertEqual(ack.held_back(self.bdir, S1, now=time.time() + 10 * CHANNEL_CONFIRM_S), {v})


class RegistryTests(BoardCase):
    def test_a_fresh_live_server_is_reachable_and_unregister_removes_it(self):
        reg.register(self.bdir, S1, os.getpid(), now=1000.0)
        self.assertEqual(reg.reachable(self.bdir, now=1000.0 + CHANNEL_TTL_S), {S1})
        reg.unregister(self.bdir, S1)
        reg.unregister(self.bdir, S1)
        self.assertEqual(reg.reachable(self.bdir, now=1000.0), set())

    def test_a_stale_beat_or_a_dead_process_is_not_reachable(self):
        dead = subprocess.Popen([sys.executable, "-c", "pass"])
        dead.wait()
        reg.register(self.bdir, S1, os.getpid(), now=1000.0)
        reg.register(self.bdir, S2, dead.pid, now=1000.0)
        self.assertEqual(reg.reachable(self.bdir, now=1000.0 + CHANNEL_TTL_S + 1), set())
        self.assertEqual(reg.reachable(self.bdir, now=1000.0), {S1})

    def test_a_damaged_or_mismatched_file_is_ignored(self):
        folder = self.bdir / CHANNEL_DIR
        folder.mkdir(parents=True)
        (folder / f"{S1}.json").write_text("{nope")
        (folder / f"{S2}.json").write_text(json.dumps({"session": S1, "pid": os.getpid(), "beat": 1000.0}))
        (folder / "short.json").write_text(json.dumps({"session": "short", "pid": os.getpid(), "beat": 1000.0}))
        (folder / "33333333-cccc.json").write_text(json.dumps({"session": "33333333-cccc", "pid": "x", "beat": 1000.0}))
        (folder / "44444444-dddd.json").write_text(json.dumps({"session": "44444444-dddd", "pid": os.getpid(), "beat": "now"}))
        self.assertEqual(reg.reachable(self.bdir, now=1000.0), set())

    def test_alive_handles_odd_pids_and_foreign_processes(self):
        self.assertFalse(reg._alive(0))
        self.assertFalse(reg._alive("12"))
        self.assertFalse(reg._alive(True))
        self.assertTrue(reg._alive(os.getpid()))
        with mock.patch.object(reg.os, "kill", side_effect=PermissionError):
            self.assertTrue(reg._alive(12345))

    def test_the_flag_must_name_this_server_after_a_channel_flag(self):
        yes = ["claude --mcp-config a.json --dangerously-load-development-channels server:board-channel",
               "claude --channels plugin:x@y server:board-channel", "claude --channels=server:board-channel",
               "claude --dangerously-load-development-channels=server:board-channel --model x"]
        no = ["claude --mcp-config server:board-channel", "claude --channels server:other",
              "claude --channels plugin:x --model server:board-channel", "claude", ""]
        for cmd in yes:
            self.assertTrue(reg.flagged(cmd), cmd)
        for cmd in no:
            self.assertFalse(reg.flagged(cmd), cmd)

    def test_launched_with_channel_reads_the_parents_command_and_fails_closed(self):
        ok = mock.Mock(stdout="claude --channels server:board-channel\n")
        with mock.patch.object(reg.subprocess, "run", return_value=ok) as run:
            self.assertTrue(reg.launched_with_channel(42))
        self.assertEqual(run.call_args[0][0], ["ps", "-o", "command=", "-p", "42"])
        with mock.patch.object(reg.subprocess, "run", side_effect=OSError):
            self.assertFalse(reg.launched_with_channel(42))
        with mock.patch.object(reg.subprocess, "run", side_effect=subprocess.CalledProcessError(1, "ps")):
            self.assertFalse(reg.launched_with_channel(999999))


if __name__ == "__main__":
    unittest.main()
