"""A task's orchestrator cost when no agent is linked (T-35): the hook links the session that runs
`board.py add|set T-n`, the fold keeps its first/last touch, and the cost is that session's MAIN transcript
inside the task's time window — an estimate that says how many other tasks share the window."""
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "board"))

import board_hook  # noqa: E402
import board_sessions as bs  # noqa: E402
import board_task_cost as tc  # noqa: E402
from board_config import PRICING_USD_PER_MTOK  # noqa: E402
from board_cost import Cache, epoch, summarize, Transcript  # noqa: E402
from board_store import fold, read_events  # noqa: E402

SID = "aaaaaaaa-1111-4222-8333-444444444444"
SID2 = "bbbbbbbb-1111-4222-8333-444444444444"
T0 = "2026-09-30T10:00:00+00:00"
M = 1_000_000
OUT_PER_M = PRICING_USD_PER_MTOK["claude-opus-5-5"][1]  # what one million output tokens cost, from the one price table


def bash(command, session=SID, **extra):
    return {"hook_event_name": "PostToolUse", "session_id": session, "tool_name": "Bash",
            "tool_input": {"command": command}, "tool_response": {}, **extra}


def msg(mid, ts, **usage):
    return json.dumps({"type": "assistant", "timestamp": ts, "isSidechain": False,
                       "message": {"id": mid, "model": "claude-opus-5-5", "usage": usage}}) + "\n"


class HookLinksSessions(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        env = mock.patch.dict(os.environ, {"BOARD_DIR": self.tmp.name})
        env.start()
        self.addCleanup(env.stop)
        self.bdir = Path(self.tmp.name)

    def linked(self):
        return [(e["id"], e["session"]) for e in read_events(self.bdir) if e["type"] == "task_session"]

    def test_set_and_add_commands_link_the_session(self):
        board_hook.handle(bash("python3 ~/.claude/scripts/board/board.py set T-3 --status running"))
        board_hook.handle(bash("B=scripts/board/board.py; python3 scripts/board/board.py --init add T-4 'x'"))
        self.assertEqual(self.linked(), [("T-3", SID), ("T-4", SID)])

    def test_several_ids_in_one_command_and_duplicates(self):
        board_hook.handle(bash("board.py set T-1 --status done && board.py set T-1 --note x && board.py set T-2 --status done"))
        self.assertEqual(self.linked(), [("T-1", SID), ("T-2", SID)])

    def test_nothing_to_link(self):
        for cmd in ("ls", "board.py list", "board.py add auto 'no id yet'", "echo board.py set nothing", ""):
            board_hook.handle(bash(cmd))
        self.assertEqual(self.linked(), [])

    def test_a_subagents_command_and_a_missing_session_are_not_the_orchestrator(self):
        board_hook.handle(bash("board.py set T-1 --status done", agent_id="ag123456"))
        board_hook.handle(bash("board.py set T-1 --status done", session=""))
        self.assertEqual(self.linked(), [])

    def test_the_fold_keeps_each_sessions_first_and_last_touch(self):
        for ts, sid in (("2026-09-30T10:00:00+00:00", SID), ("2026-09-30T10:30:00+00:00", SID2),
                        ("2026-09-30T11:00:00+00:00", SID)):
            board_hook.handle(bash("board.py set T-1 --note x", session=sid))
        raw = read_events(self.bdir)
        for e, ts in zip((e for e in raw if e["type"] == "task_session"),
                         ("2026-09-30T10:00:00+00:00", "2026-09-30T10:30:00+00:00", "2026-09-30T11:00:00+00:00")):
            e["ts"] = ts
        spans = fold(raw)["tasks"]["T-1"]["sessions"]
        self.assertEqual(spans[SID], {"first": "2026-09-30T10:00:00+00:00", "last": "2026-09-30T11:00:00+00:00"})
        self.assertEqual(spans[SID2]["first"], "2026-09-30T10:30:00+00:00")


class WindowedSummary(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.path = Path(self.tmp.name) / "t.jsonl"
        self.path.write_text(msg("a", "2026-09-30T10:00:00+00:00", input_tokens=M)
                             + msg("b", "2026-09-30T10:30:00+00:00", input_tokens=M)
                             + msg("c", "2026-09-30T11:00:00+00:00", input_tokens=M)
                             + json.dumps({"type": "assistant", "message": {"id": "nots", "model": "claude-opus-5-5",
                                                                           "usage": {"input_tokens": M}}}) + "\n")
        self.tr = Transcript(self.path).update()

    def test_no_window_counts_everything(self):
        self.assertEqual(summarize([self.tr])["tokens"]["input"], 4 * M)

    def test_the_window_is_inclusive_at_both_ends_and_drops_timestampless_messages(self):
        s = summarize([self.tr], start=epoch("2026-09-30T10:00:00+00:00"), end=epoch("2026-09-30T10:30:00+00:00"))
        self.assertEqual((s["messages"], s["tokens"]["input"]), (2, 2 * M))

    def test_an_open_ended_window(self):
        self.assertEqual(summarize([self.tr], start=epoch("2026-09-30T10:45:00+00:00"))["messages"], 1)
        self.assertEqual(summarize([self.tr], end=epoch("2026-09-30T10:15:00+00:00"))["messages"], 1)

    def test_an_empty_window_is_zero_not_none(self):
        s = summarize([self.tr], start=epoch("2030-01-01T00:00:00+00:00"))
        self.assertEqual((s["messages"], s["cost"]), (0, 0.0))


class Windows(unittest.TestCase):
    NOW = epoch("2026-09-30T12:00:00+00:00")

    def task(self, status, spans):
        return {"status": status, "sessions": spans}

    def test_a_running_task_is_open_until_now_and_a_finished_one_ends_at_the_last_touch(self):
        spans = {SID: {"first": "2026-09-30T10:00:00+00:00", "last": "2026-09-30T11:00:00+00:00"}}
        self.assertEqual(tc.task_windows(self.task("running", spans), self.NOW)[SID],
                         (epoch("2026-09-30T10:00:00+00:00"), self.NOW))
        self.assertEqual(tc.task_windows(self.task("done", spans), self.NOW)[SID],
                         (epoch("2026-09-30T10:00:00+00:00"), epoch("2026-09-30T11:00:00+00:00")))

    def test_a_task_without_sessions_has_no_window_and_the_list_is_capped(self):
        self.assertEqual(tc.task_windows(self.task("done", {}), self.NOW), {})
        many = {f"s{i}-aaaaaaaa": {"first": T0, "last": T0} for i in range(20)}
        self.assertLessEqual(len(tc.task_windows(self.task("done", many), self.NOW)), 5)

    def test_overlap_counts_other_tasks_in_the_same_session_only(self):
        def span(a, b, sid=SID):
            return {sid: {"first": a, "last": b}}
        state = {"tasks": {
            "T-1": self.task("done", span("2026-09-30T10:00:00+00:00", "2026-09-30T11:00:00+00:00")),
            "T-2": self.task("done", span("2026-09-30T10:30:00+00:00", "2026-09-30T11:30:00+00:00")),
            "T-3": self.task("done", span("2026-09-30T11:45:00+00:00", "2026-09-30T11:50:00+00:00")),
            "T-4": self.task("done", span("2026-09-30T10:00:00+00:00", "2026-09-30T11:00:00+00:00", SID2)),
        }}
        self.assertEqual(tc.shared_counts(state, self.NOW), {"T-1": 1, "T-2": 1, "T-3": 0, "T-4": 0})


class Orchestration(unittest.TestCase):
    NOW = epoch("2026-09-30T12:00:00+00:00")

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.main = self.root / f"{SID}.jsonl"
        self.main.write_text(msg("a", "2026-09-30T09:00:00+00:00", output_tokens=M)   # before the task
                             + msg("b", "2026-09-30T10:10:00+00:00", output_tokens=M)  # inside
                             + msg("c", "2026-09-30T13:00:00+00:00", output_tokens=M))  # after it ended
        sub = self.root / SID / "subagents"
        sub.mkdir(parents=True)
        (sub / "agent-abc123.jsonl").write_text(msg("s", "2026-09-30T10:20:00+00:00", output_tokens=M))
        self.sessions = {SID: {"id": SID, "transcript": str(self.main)}}
        self.task = {"status": "done", "sessions": {SID: {"first": "2026-09-30T10:00:00+00:00",
                                                         "last": "2026-09-30T11:00:00+00:00"}}}

    def test_only_the_main_transcript_inside_the_window_counts_not_the_subagents(self):
        o = tc.orchestration(self.task, self.sessions, Cache(), self.NOW, shared=2)
        self.assertEqual(o["summary"]["tokens"]["output"], M)  # one main message; the subagent's is excluded
        self.assertEqual(o["summary"]["cost"], OUT_PER_M)
        self.assertEqual((o["sessions"], o["shared"]), ([SID], 2))

    def test_none_without_a_linked_session_or_a_readable_transcript(self):
        self.assertIsNone(tc.orchestration({"status": "done", "sessions": {}}, self.sessions, Cache(), self.NOW))
        gone = {SID: {"id": SID, "transcript": str(self.root / "missing.jsonl")}}
        with mock.patch.object(bs, "TRANSCRIPTS_ROOT", self.root / "empty"):
            self.assertIsNone(tc.orchestration(self.task, gone, Cache(), self.NOW))

    def test_an_unpriced_model_makes_the_estimate_unmeasurable_not_zero(self):
        self.main.write_text(json.dumps({"type": "assistant", "timestamp": "2026-09-30T10:10:00+00:00",
                                         "message": {"id": "x", "model": "mystery-model", "usage": {"output_tokens": 5}}}) + "\n")
        o = tc.orchestration(self.task, self.sessions, Cache(), self.NOW)
        self.assertIsNone(o["summary"]["cost"])
        self.assertEqual(o["summary"]["unpriced"], ["mystery-model"])

    def test_task_cost_carries_it_even_with_no_agent_linked(self):
        c = bs.task_cost(self.task, [], {}, Cache(), self.NOW, self.sessions, 0)
        self.assertIsNone(c["spent"])
        self.assertEqual(c["orchestration"]["summary"]["cost"], OUT_PER_M)

    def test_board_costs_wires_sessions_and_sharing(self):
        state = {"mode": "B", "roles": None, "tasks": {"T-1": {**self.task, "id": "T-1", "title": "t", "agents": [],
                                                                  "eta_min": None, "est_cost": None, "started": None,
                                                                  "updated": None}},
                 "agents": {}, "sessions": self.sessions, "agent_links": {}, "agent_transcripts": {}}
        out = bs.board_costs(state, Cache(), self.NOW)
        self.assertEqual(out["tasks"]["T-1"]["orchestration"]["summary"]["cost"], OUT_PER_M)


if __name__ == "__main__":
    unittest.main()
