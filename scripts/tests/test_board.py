"""The live board (scripts/board/, docs/live-board.md): hook, event fold, controls, CLI.
The server and board location live in test_board_server.py, registry and auto-start in
test_board_ensure.py (#9)."""
import io
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "board"))

import board  # noqa: E402
import board_hook  # noqa: E402
from board_config import EVENTS_FILE  # noqa: E402
from board_store import (apply_control, fold, read_control, read_events,  # noqa: E402
                         unseen_changes)


def pre(tuid, agent_type, desc, bg=True, session="s1"):
    return {"hook_event_name": "PreToolUse", "session_id": session, "tool_name": "Agent",
            "tool_use_id": tuid, "tool_input": {"subagent_type": agent_type, "description": desc,
                                                "prompt": "x", "run_in_background": bg}}


def post(tuid, response, session="s1"):
    return {"hook_event_name": "PostToolUse", "session_id": session, "tool_name": "Agent",
            "tool_use_id": tuid, "tool_input": {}, "tool_response": response, "duration_ms": 5}


def stop(agent_id, agent_type, session="s1"):
    return {"hook_event_name": "SubagentStop", "session_id": session, "agent_id": agent_id,
            "agent_type": agent_type, "last_assistant_message": "ok"}


class BoardTestCase(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.bdir = Path(self.tmp.name)
        patcher = mock.patch.dict(os.environ, {"BOARD_DIR": self.tmp.name})
        patcher.start()
        self.addCleanup(patcher.stop)
        self.addCleanup(self.tmp.cleanup)

    def state(self):
        return fold(read_events(self.bdir), read_control(self.bdir))

    def cli(self, *argv):
        return board.run(list(argv))


class LifecycleTests(BoardTestCase):
    def test_background_agent_runs_then_finishes(self):
        self.cli("add", "T-1", "F-063 proxies", "--branch", "fix/f063", "--role", "developer")
        self.assertIsNone(board_hook.handle(pre("tu1", "analyst", "[T-1] trace flow")))
        self.assertEqual(self.state()["tasks"]["T-1"]["status"], "running")
        board_hook.handle(post("tu1", {"status": "async_launched", "agentId": "ag1"}))
        s = self.state()
        self.assertEqual(s["agents"]["tu1"]["status"], "running")
        self.assertEqual(s["agents"]["tu1"]["agent_id"], "ag1")
        board_hook.handle(stop("ag1", "analyst"))
        s = self.state()
        self.assertEqual(s["agents"]["tu1"]["status"], "done")
        self.assertEqual(s["tasks"]["T-1"]["status"], "agent_done")
        self.assertEqual(s["tasks"]["T-1"]["role"], "developer")  # planned role is kept

    def test_foreground_agent_finishes_on_post(self):
        board_hook.handle(pre("tu2", "test-writer", "[T-2] tests", bg=False))
        board_hook.handle(post("tu2", {"status": "completed", "content": []}))
        s = self.state()
        self.assertEqual(s["agents"]["tu2"]["status"], "done")
        self.assertEqual(s["tasks"]["T-2"]["status"], "agent_done")
        self.assertEqual(s["tasks"]["T-2"]["role"], "test-writer")

    def test_task_stays_running_while_one_of_two_agents_is_live(self):
        board_hook.handle(pre("a", "developer", "[T-3] backend"))
        board_hook.handle(pre("b", "test-writer", "[T-3] tests"))
        board_hook.handle(post("a", {"status": "async_launched", "agentId": "A"}))
        board_hook.handle(post("b", {"status": "async_launched", "agentId": "B"}))
        board_hook.handle(stop("A", "developer"))
        self.assertEqual(self.state()["tasks"]["T-3"]["status"], "running")
        board_hook.handle(stop("B", "test-writer"))
        self.assertEqual(self.state()["tasks"]["T-3"]["status"], "agent_done")

    def test_orchestrator_closes_task_and_late_agent_events_do_not_reopen_it(self):
        board_hook.handle(pre("a", "analyst", "[T-4] x"))
        board_hook.handle(post("a", {"status": "async_launched", "agentId": "A"}))
        self.cli("set", "T-4", "--status", "done")
        board_hook.handle(stop("A", "analyst"))
        self.assertEqual(self.state()["tasks"]["T-4"]["status"], "done")

    def test_untagged_agent_and_unknown_stop_are_tracked_without_a_task(self):
        board_hook.handle(pre("u", "analyst", "no tag here"))
        board_hook.handle(stop("never-bound", "analyst"))
        s = self.state()
        self.assertIsNone(s["agents"]["u"]["task"])
        self.assertEqual(s["tasks"], {})

    def test_turn_open_and_closed(self):
        board_hook.handle({"hook_event_name": "UserPromptSubmit", "session_id": "s9", "prompt": "go"})
        self.assertTrue(self.state()["sessions"]["s9"]["turn_open"])
        board_hook.handle({"hook_event_name": "Stop", "session_id": "s9", "last_assistant_message": ""})
        self.assertFalse(self.state()["sessions"]["s9"]["turn_open"])


class ControlTests(BoardTestCase):
    def deny_text(self, out):
        self.assertIsNotNone(out)
        spec = out["hookSpecificOutput"]
        self.assertEqual(spec["permissionDecision"], "deny")
        return spec["permissionDecisionReason"]

    def test_disabled_role_is_denied_and_recorded(self):
        apply_control(self.bdir, "disable_role", "test-writer")
        self.assertIn("switched off", self.deny_text(board_hook.handle(pre("d", "test-writer", "[T-1] t"))))
        s = self.state()
        self.assertEqual(s["agents"]["d"]["status"], "denied")
        self.assertNotIn("T-1", s["tasks"])  # a denied spawn does not create or start a task

    def test_denied_agent_is_never_revived_by_a_stray_post(self):
        apply_control(self.bdir, "disable_role", "security")
        board_hook.handle(pre("z", "security", "[T-2] review"))
        board_hook.handle(post("z", {"status": "async_launched", "agentId": "Z"}))
        self.assertEqual(self.state()["agents"]["z"]["status"], "denied")

    def test_removed_task_is_denied_and_shown_removed(self):
        self.cli("add", "T-5", "SMS pause")
        apply_control(self.bdir, "remove_task", "T-5")
        self.assertIn("removed", self.deny_text(board_hook.handle(pre("r", "developer", "[T-5] go"))))
        self.assertEqual(self.state()["tasks"]["T-5"]["status"], "removed")
        apply_control(self.bdir, "restore_task", "T-5")
        self.assertIsNone(board_hook.handle(pre("r2", "developer", "[T-5] go")))

    def test_role_outside_mode_is_denied_only_when_plan_declares_roles(self):
        self.assertIsNone(board_hook.handle(pre("p0", "qa", "[T-1] r")))
        self.cli("plan", "--mode", "B", "--roles", "analyst,test-writer,doc-writer")
        self.assertIn("role set", self.deny_text(board_hook.handle(pre("p1", "qa", "[T-1] r"))))
        self.assertIsNone(board_hook.handle(pre("p2", "analyst", "[T-1] r")))

    def test_a_declared_empty_role_set_denies_every_agent(self):
        # Mode A has no agents. An empty set must mean "none allowed", not "no limit".
        self.cli("plan", "--mode", "A", "--roles", "")
        self.assertIn("(none)", self.deny_text(board_hook.handle(pre("a1", "analyst", "[T-1] r"))))
        self.cli("plan", "--mode", "A")
        self.assertIsNotNone(board_hook.handle(pre("a2", "analyst", "x")))

    def test_changes_are_injected_once_per_session(self):
        apply_control(self.bdir, "remove_task", "T-2")
        out = board_hook.handle({"hook_event_name": "PostToolUse", "session_id": "s1",
                                 "tool_name": "Bash", "tool_input": {}, "tool_response": {}})
        self.assertIn("removed task T-2", out["hookSpecificOutput"]["additionalContext"])
        again = board_hook.handle({"hook_event_name": "PostToolUse", "session_id": "s1",
                                   "tool_name": "Bash", "tool_input": {}, "tool_response": {}})
        self.assertIsNone(again)
        self.assertEqual(len(unseen_changes(self.bdir, "other-session")), 1)

    def test_data_files_are_private(self):
        self.cli("add", "T-1", "a")
        apply_control(self.bdir, "remove_task", "T-1")
        for name in (EVENTS_FILE, "control.json"):
            self.assertEqual((self.bdir / name).stat().st_mode & 0o777, 0o600, name)

    def test_unknown_control_action_raises(self):
        with self.assertRaises(ValueError):
            apply_control(self.bdir, "drop_table", "x")


class RobustnessTests(BoardTestCase):
    def test_hook_main_never_fails_the_session(self):
        with mock.patch.object(sys, "stdin", io.StringIO("not json")), \
                mock.patch.object(sys, "stderr", io.StringIO()) as err:
            self.assertEqual(board_hook.main(), 0)
        self.assertIn("board hook error", err.getvalue())

    def test_hook_main_prints_deny_json(self):
        apply_control(self.bdir, "disable_role", "qa")
        with mock.patch.object(sys, "stdin", io.StringIO(json.dumps(pre("x", "qa", "t")))), \
                mock.patch.object(sys, "stdout", io.StringIO()) as out:
            board_hook.main()
        self.assertEqual(json.loads(out.getvalue())["hookSpecificOutput"]["permissionDecision"], "deny")

    def test_corrupt_event_line_is_skipped_not_fatal(self):
        self.cli("add", "T-1", "a")
        with open(self.bdir / EVENTS_FILE, "a") as f:
            f.write("{broken\n")
        self.cli("add", "T-2", "b")
        with mock.patch.object(sys, "stderr", io.StringIO()):
            self.assertEqual(sorted(self.state()["tasks"]), ["T-1", "T-2"])

    def test_corrupt_control_file_reads_as_empty(self):
        (self.bdir / "control.json").write_text("{nope")
        with mock.patch.object(sys, "stderr", io.StringIO()):
            self.assertEqual(read_control(self.bdir)["removed_tasks"], [])


class ValidationTests(BoardTestCase):
    def test_cli_rejects_bad_task_id_and_role(self):
        with mock.patch.object(sys, "stderr", io.StringIO()):
            with self.assertRaises(SystemExit):
                self.cli("add", "task1", "x")
            with self.assertRaises(SystemExit):
                self.cli("plan", "--mode", "B", "--roles", "Bad Role")

    def test_cli_set_without_fields_is_an_error(self):
        with mock.patch.object(sys, "stderr", io.StringIO()):
            self.assertEqual(self.cli("set", "T-1"), 2)

    def test_cli_list_prints_folded_tasks(self):
        self.cli("add", "T-1", "first", "--role", "analyst")
        self.cli("set", "T-1", "--status", "waiting", "--note", "later")
        with mock.patch.object(sys, "stdout", io.StringIO()) as out:
            self.assertEqual(self.cli("list"), 0)
        self.assertEqual(out.getvalue().strip(), "T-1\twaiting\tanalyst\tfirst")


if __name__ == "__main__":
    unittest.main()
