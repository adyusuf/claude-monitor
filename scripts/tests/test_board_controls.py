"""Sending work to a session from the live board (scripts/board/board_api.py + board_hook.py,
docs/live-board.md §2d): queue a task, run a skill, switch the mode — validated
fail-closed, delivered only to the session it is meant for."""
import io
import json
import os
import sys
import tempfile
import unittest
from contextlib import redirect_stderr
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "board"))

import board_api  # noqa: E402
import board_hook  # noqa: E402
from board_config import MODE_FILE, QUEUE_TEXT_MAX  # noqa: E402
from board_store import append_event, fold, read_events  # noqa: E402
from board_control import peek_changes, read_control  # noqa: E402

S1, S2 = "11111111-aaaa", "22222222-bbbb"


def make_skills(root: Path) -> Path:
    skills = root / "skills"
    (skills / "plain").mkdir(parents=True)
    (skills / "plain" / "SKILL.md").write_text("x")
    plug = skills / "adyusuf-link"
    (plug / ".claude-plugin").mkdir(parents=True)
    (plug / ".claude-plugin" / "plugin.json").write_text(json.dumps({"name": "adyusuf"}))
    (plug / "skills" / "board-plan").mkdir(parents=True)
    (plug / "skills" / "board-plan" / "SKILL.md").write_text("x")
    (plug / "skills" / "no-skill-file").mkdir()
    (plug / "commands").mkdir()
    (plug / "commands" / "standards-check.md").write_text("x")
    (plug / "commands" / "notes.txt").write_text("x")
    unnamed = skills / "unnamed"
    (unnamed / ".claude-plugin").mkdir(parents=True)
    (unnamed / ".claude-plugin" / "plugin.json").write_text("{}")
    (unnamed / "skills" / "one").mkdir(parents=True)
    (unnamed / "skills" / "one" / "SKILL.md").write_text("x")
    (skills / "loose-folder").mkdir()
    (skills / "Bad Name").mkdir()
    (skills / "Bad Name" / "SKILL.md").write_text("x")
    return skills


class ControlCase(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        base = Path(self.tmp.name)
        self.root = base / "proj"
        self.bdir = self.root / ".claude" / "board"
        self.entry = {"id": "0123456789", "root": str(self.root), "dir": str(self.bdir)}
        self.skills = make_skills(base)
        for sid in (S1, S2):
            append_event(self.bdir, {"type": "turn_start", "session": sid})
        patcher = mock.patch.dict(os.environ, {"BOARD_DIR": str(self.bdir)})
        patcher.start()
        self.addCleanup(patcher.stop)

    def apply(self, **body):
        return board_api.apply(self.entry, body, self.skills)

    def hook(self, event, session, **extra):
        return board_hook.handle({"hook_event_name": event, "session_id": session, **extra})


class ValidationTests(ControlCase):
    def test_values_are_checked_per_action(self):
        ok = [{"action": "queue_task", "value": S1}, {"action": "run_skill", "value": "adyusuf:board-plan"},
              {"action": "run_skill", "value": "plain"}, {"action": "set_mode", "value": "E"}]
        for body in ok:
            self.assertEqual(board_api.validate_control(body), (body["action"], body["value"]))
        bad = [{"action": "queue_task", "value": "short"}, {"action": "queue_task", "value": "../../etc/x"},
               {"action": "run_skill", "value": "a:b:c"}, {"action": "run_skill", "value": "Up"},
               {"action": "set_mode", "value": "F"}, {"action": "set_mode", "value": "a"},
               {"action": "set_mode", "value": "AB"}]
        for body in bad:
            with self.assertRaises(ValueError):
                board_api.validate_control(body)

    def test_skills_are_listed_from_plain_dirs_and_plugins(self):
        broken = self.skills / "broken" / ".claude-plugin"
        broken.mkdir(parents=True)
        (broken / "plugin.json").write_text("{not json")
        err = io.StringIO()
        with redirect_stderr(err):
            names = board_api.list_skills(self.skills)
        self.assertEqual(names, ["adyusuf:board-plan", "adyusuf:standards-check", "plain", "unnamed:one"])
        self.assertIn("unreadable plugin manifest", err.getvalue())
        self.assertEqual(board_api.list_skills(Path(self.tmp.name) / "none"), [])


class QueueTests(ControlCase):
    def test_a_queued_task_is_recorded_for_its_session(self):
        ctl = self.apply(action="queue_task", value=S1, text="  write the release notes\nthen stop ")
        change = ctl["changes"][-1]
        self.assertEqual((change["action"], change["session"], change["text"]),
                         ("queue_task", S1, "write the release notes\nthen stop"))

    def test_bad_texts_and_unknown_sessions_are_refused(self):
        for text in ("", "   ", "x" * (QUEUE_TEXT_MAX + 1), "bell\x07", None, 7):
            with self.assertRaises(ValueError):
                self.apply(action="queue_task", value=S1, text=text)
        with self.assertRaisesRegex(ValueError, "unknown session"):
            self.apply(action="queue_task", value="99999999-zzzz", text="hi")
        self.assertEqual(read_control(self.bdir)["changes"], [])
        self.apply(action="queue_task", value=S1, text="x" * QUEUE_TEXT_MAX)  # the limit itself is fine

    def test_a_skill_must_be_listed_and_its_session_valid(self):
        ctl = self.apply(action="run_skill", value="adyusuf:board-plan", session=S2)
        change = ctl["changes"][-1]
        self.assertEqual((change["session"], change["text"]), (S2, "invoke the /adyusuf:board-plan skill"))
        with self.assertRaisesRegex(ValueError, "unknown skill"):
            self.apply(action="run_skill", value="adyusuf:nope", session=S2)
        for session in (None, "short", 5):
            with self.assertRaisesRegex(ValueError, "invalid session"):
                self.apply(action="run_skill", value="plain", session=session)
        with self.assertRaisesRegex(ValueError, "unknown session"):
            self.apply(action="run_skill", value="plain", session="99999999-zzzz")

    def test_the_old_actions_still_work_through_apply(self):
        self.apply(action="remove_task", value="T-4")
        self.assertEqual(read_control(self.bdir)["removed_tasks"], ["T-4"])
        self.apply(action="decide", value="T-4", choice=" yes ", note="n")
        self.assertEqual(read_control(self.bdir)["decisions"]["T-4"]["choice"], "yes")
        with self.assertRaises(ValueError):
            self.apply(action="decide", value="T-4", choice="")


class ModeTests(ControlCase):
    def test_the_mode_file_the_event_log_and_a_change_are_written(self):
        self.apply(action="set_mode", value="C")
        self.assertEqual((self.root / MODE_FILE).read_text(), "C\n")
        st = fold(read_events(self.bdir))
        self.assertEqual((st["mode"], st["mode_by"]), ("C", "board"))
        self.assertEqual(read_control(self.bdir)["changes"][-1]["action"], "set_mode")

    def test_every_session_is_told_and_asked_to_redeclare_the_plan(self):
        self.apply(action="set_mode", value="D")
        for sid in (S1, S2):
            text = self.hook("PostToolUse", sid, tool_name="Bash")["hookSpecificOutput"]["additionalContext"]
            self.assertIn("working mode changed to D by the user on the board", text)
            self.assertIn("board.py plan --mode D", text)


class DeliveryTests(ControlCase):
    def test_a_queued_task_reaches_only_its_own_session(self):
        self.apply(action="queue_task", value=S1, text="add a changelog")
        self.assertIsNone(self.hook("PostToolUse", S2, tool_name="Bash"))
        out = self.hook("PostToolUse", S1, tool_name="Bash")["hookSpecificOutput"]["additionalContext"]
        self.assertIn("NEW TASK from the user, queued on the board for this session: add a changelog", out)
        self.assertIsNone(self.hook("PostToolUse", S1, tool_name="Bash"))  # delivered once

    def test_a_subagent_call_does_not_consume_it(self):
        self.apply(action="queue_task", value=S1, text="later")
        self.assertIsNone(self.hook("PostToolUse", S1, tool_name="Bash", agent_id="sub1"))
        self.assertEqual(len(peek_changes(self.bdir, S1)), 1)

    def test_at_stop_a_queued_task_keeps_the_turn_going(self):
        self.apply(action="run_skill", value="plain", session=S1)
        self.assertIsNone(self.hook("Stop", S2))   # the other session's queue does not hold S2
        out = self.hook("Stop", S1, transcript_path="/t/s1.jsonl")
        self.assertEqual(out["decision"], "block")
        self.assertIn("invoke the /plain skill", out["reason"])
        s = fold(read_events(self.bdir))["sessions"][S1]
        self.assertEqual(s["state"], "busy")         # the turn goes on
        self.assertEqual(s["transcript"], "/t/s1.jsonl")

    def test_a_prompt_delivers_it_to_an_idle_session_and_records_the_transcript(self):
        self.hook("Stop", S1)
        self.apply(action="queue_task", value=S1, text="next thing")
        out = self.hook("UserPromptSubmit", S1, transcript_path="/t/s1.jsonl")
        self.assertIn("next thing", out["hookSpecificOutput"]["additionalContext"])
        self.assertEqual(fold(read_events(self.bdir))["sessions"][S1]["transcript"], "/t/s1.jsonl")

    def test_agent_ids_and_transcripts_are_recorded(self):
        board_hook.handle({"hook_event_name": "PostToolUse", "session_id": S1, "tool_name": "Agent",
                           "tool_use_id": "u1", "tool_response": {"status": "completed", "agentId": "fg1"}})
        board_hook.handle({"hook_event_name": "PostToolUse", "session_id": S1, "tool_name": "Agent",
                           "tool_use_id": "u2", "tool_response": "text only"})
        self.hook("SubagentStop", S1, agent_id="fg1", agent_transcript_path="/t/s1/subagents/agent-fg1.jsonl")
        events = read_events(self.bdir)
        self.assertEqual([e.get("agent_id") for e in events if e["type"] == "agent_post"], ["fg1", None])
        self.assertEqual(fold(events)["agent_transcripts"], {"fg1": "/t/s1/subagents/agent-fg1.jsonl"})


if __name__ == "__main__":
    unittest.main()
