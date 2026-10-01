"""Agent activity for agents that never pass through the Agent tool's PreToolUse (T-2): a resumed
agent (SendMessage), a Workflow agent. SubagentStart opens their row, SubagentStop closes it."""
import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "board"))

import board_hook  # noqa: E402
from board_store import append_event, fold, read_events  # noqa: E402
from board_control import read_control  # noqa: E402


def start(agent_id, agent_type, session="s1"):
    return {"hook_event_name": "SubagentStart", "session_id": session, "agent_id": agent_id,
            "agent_type": agent_type}


def stop(agent_id, agent_type, session="s1", transcript=None):
    p = {"hook_event_name": "SubagentStop", "session_id": session, "agent_id": agent_id,
         "agent_type": agent_type, "last_assistant_message": "ok"}
    if transcript:
        p["agent_transcript_path"] = transcript
    return p


def pre(tuid, agent_type, desc, session="s1"):
    return {"hook_event_name": "PreToolUse", "session_id": session, "tool_name": "Agent",
            "tool_use_id": tuid, "tool_input": {"subagent_type": agent_type, "description": desc,
                                                "prompt": "x", "run_in_background": True}}


def post(tuid, agent_id, session="s1"):
    return {"hook_event_name": "PostToolUse", "session_id": session, "tool_name": "Agent",
            "tool_use_id": tuid, "tool_input": {}, "duration_ms": 5,
            "tool_response": {"status": "async_launched", "agentId": agent_id}}


class SubagentStart(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.bdir = Path(self.tmp.name)
        patcher = mock.patch.dict(os.environ, {"BOARD_DIR": self.tmp.name})
        patcher.start()
        self.addCleanup(patcher.stop)

    def send(self, *payloads):
        for p in payloads:
            board_hook.handle(p)

    def agents(self):
        return fold(read_events(self.bdir), read_control(self.bdir))["agents"]

    def test_an_agent_that_never_passed_the_agent_tool_gets_a_row_while_it_runs(self):
        self.send(start("a1", "test-writer"))
        (row,) = self.agents().values()
        self.assertEqual((row["agent_id"], row["type"], row["status"]), ("a1", "test-writer", "running"))
        self.assertIsNone(row["ended"])

    def test_stop_closes_that_row(self):
        self.send(start("a1", "test-writer"), stop("a1", "test-writer"))
        (row,) = self.agents().values()
        self.assertEqual(row["status"], "done")
        self.assertIsNotNone(row["ended"])

    def test_an_untyped_internal_subagent_is_ignored(self):
        self.send(start("x1", ""), start("x2", None))
        self.assertEqual(self.agents(), {})
        self.assertEqual([e for e in read_events(self.bdir) if e["type"] == "agent_start"], [])

    def test_the_fold_ignores_an_untyped_start_event_even_when_it_bypassed_the_hook(self):
        # the log can be written by other tools; the fold must not depend on the hook's filter
        append_event(self.bdir, {"type": "agent_start", "session": "s1", "agent_id": "x1", "agent_type": ""})
        append_event(self.bdir, {"type": "agent_start", "session": "s1", "agent_id": "", "agent_type": "analyst"})
        self.assertEqual(self.agents(), {})

    def test_a_resumed_agent_runs_again_in_the_same_row(self):
        self.send(start("a1", "analyst"), stop("a1", "analyst"), start("a1", "analyst"))
        (row,) = self.agents().values()
        self.assertEqual((row["status"], row["ended"]), ("running", None))

    def test_no_duplicate_when_the_agent_tool_call_also_created_a_row(self):
        # normal order: PreToolUse -> SubagentStart -> PostToolUse(async_launched, agentId)
        self.send(pre("t1", "analyst", "[T-3] measure"), start("a1", "analyst"), post("t1", "a1"))
        (row,) = self.agents().values()
        self.assertEqual((row["key"], row["task"], row["agent_id"]), ("t1", "T-3", "a1"))

    def test_no_duplicate_when_the_start_arrives_after_the_post(self):
        self.send(pre("t1", "analyst", "[T-3] measure"), post("t1", "a1"), start("a1", "analyst"))
        (row,) = self.agents().values()
        self.assertEqual((row["key"], row["status"]), ("t1", "running"))

    def test_two_different_agents_get_two_rows(self):
        self.send(start("a1", "analyst"), start("a2", "doc-writer"))
        self.assertEqual(sorted(a["type"] for a in self.agents().values()), ["analyst", "doc-writer"])

    def test_the_agent_keeps_its_session_and_the_transcript_reaches_the_cost_lookup(self):
        self.send(start("a1", "analyst", session="sX"), stop("a1", "analyst", session="sX", transcript="/t/a1.jsonl"))
        state = fold(read_events(self.bdir), read_control(self.bdir))
        (row,) = state["agents"].values()
        self.assertEqual(row["session"], "sX")
        self.assertEqual(state["agent_transcripts"]["a1"], "/t/a1.jsonl")

    def test_a_start_event_never_breaks_the_hook(self):
        self.assertIsNone(board_hook.handle({"hook_event_name": "SubagentStart", "session_id": "s1"}))


if __name__ == "__main__":
    unittest.main()
