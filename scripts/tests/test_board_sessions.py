"""Sessions, per-task cost and projections (scripts/board/board_store.py fold +
scripts/board/board_sessions.py, docs/live-board-sessions.md §2c)."""
import json
import sys
import tempfile
import unittest
from pathlib import Path

BOARD = Path(__file__).resolve().parent.parent / "board"
sys.path.insert(0, str(BOARD))

import board_sessions as bs  # noqa: E402
from board_cost import Cache, epoch  # noqa: E402
from board_store import fold  # noqa: E402

NOW = epoch("2026-09-30T12:00:00+00:00")
M = 1_000_000


def ev(kind, ts, **kw):
    return {"type": kind, "ts": ts, **kw}


def msg(mid, ts, model="claude-opus-5-5", side=False, **usage):
    return json.dumps({"type": "assistant", "timestamp": ts, "isSidechain": side,
                       "message": {"id": mid, "model": model, "usage": usage}}) + "\n"


class FoldSessionTests(unittest.TestCase):
    def test_busy_from_the_prompt_until_stop_and_since_marks_the_change(self):
        st = fold([ev("turn_start", "2026-09-30T10:00:00+00:00", session="s1", transcript="/t/s1.jsonl"),
                   ev("agent_pre", "2026-09-30T10:05:00+00:00", session="s1", tool_use_id="u"),
                   ev("turn_start", "2026-09-30T10:06:00+00:00", session="s1")])
        s = st["sessions"]["s1"]
        self.assertEqual((s["state"], s["since"]), ("busy", "2026-09-30T10:00:00+00:00"))
        self.assertEqual(s["transcript"], "/t/s1.jsonl")   # a later event without one keeps it
        self.assertEqual(s["last"], "2026-09-30T10:06:00+00:00")
        st = fold([ev("turn_start", "2026-09-30T10:00:00+00:00", session="s1"),
                   ev("turn_stop", "2026-09-30T10:09:00+00:00", session="s1"),
                   ev("turn_stop", "2026-09-30T10:10:00+00:00", session="s1")])
        self.assertEqual((st["sessions"]["s1"]["state"], st["sessions"]["s1"]["since"]),
                         ("idle", "2026-09-30T10:09:00+00:00"))

    def test_a_session_seen_only_through_an_agent_is_unknown(self):
        st = fold([ev("agent_stop", "2026-09-30T10:00:00+00:00", session="s2", agent_id="a1",
                      agent_transcript="/t/s2/subagents/agent-a1.jsonl")])
        self.assertEqual(st["sessions"]["s2"]["state"], "unknown")
        self.assertEqual(st["agent_transcripts"], {"a1": "/t/s2/subagents/agent-a1.jsonl"})

    def test_tasks_carry_eta_estimate_start_and_agent_links(self):
        st = fold([ev("task_add", "2026-09-30T09:00:00+00:00", id="T-1", title="x"),
                   ev("agent_pre", "2026-09-30T09:10:00+00:00", session="s", tool_use_id="u",
                      task="T-1", agent_type="analyst"),
                   ev("task_set", "2026-09-30T09:30:00+00:00", id="T-1", status="running",
                      eta_min=45, est_cost=3.5),
                   ev("task_set", "2026-09-30T09:40:00+00:00", id="T-1", status="running"),
                   ev("task_set", "2026-09-30T09:50:00+00:00", id="T-2", agent="abc123def")])
        t = st["tasks"]["T-1"]
        self.assertEqual((t["eta_min"], t["est_cost"]), (45, 3.5))
        self.assertEqual(t["started"], "2026-09-30T09:10:00+00:00")  # the earliest start wins
        self.assertEqual(st["agent_links"], {"abc123def": "T-2"})
        self.assertNotIn("agent", st["tasks"]["T-2"])

    def test_a_foreground_agent_keeps_its_id(self):
        st = fold([ev("agent_pre", "t1", session="s", tool_use_id="u", task="T-1"),
                   ev("agent_post", "t2", session="s", tool_use_id="u", launched=False, agent_id="fg1")])
        a = st["agents"]["u"]
        self.assertEqual((a["agent_id"], a["status"]), ("fg1", "done"))

    def test_the_board_can_set_the_mode_and_a_plan_takes_it_back(self):
        st = fold([ev("plan", "t1", mode="B", roles=["analyst"]),
                   ev("mode_set", "t2", mode="C", by="board")])
        self.assertEqual((st["mode"], st["mode_by"], st["roles"]), ("C", "board", ["analyst"]))
        st = fold([ev("mode_set", "t2", mode="C", by="board"), ev("plan", "t3", mode="C", roles=[])])
        self.assertIsNone(st["mode_by"])


class CostViewTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.main = Path(self.tmp.name) / "s1.jsonl"
        self.subdir = Path(self.tmp.name) / "s1" / "subagents"
        self.subdir.mkdir(parents=True)

    def write(self, path: Path, *lines: str) -> str:
        path.write_text("".join(lines))
        return str(path)

    def state(self, events):
        return fold(events)

    def test_subagent_files_and_agent_paths(self):
        a1 = self.write(self.subdir / "agent-a1.jsonl", "")
        (self.subdir / "notes.txt").write_text("")
        self.assertEqual(bs.subagent_files(str(self.main)), [Path(a1)])
        self.assertEqual(bs.subagent_files(None), [])
        self.assertEqual(bs.subagent_files(str(Path(self.tmp.name) / "none.jsonl")), [])
        st = self.state([ev("turn_start", "t", session="s1", transcript=str(self.main)),
                         ev("agent_stop", "t", session="s1", agent_id="a2",
                            agent_transcript="/elsewhere/a2.jsonl")])
        self.assertEqual(bs.agent_paths(st), {"a1": a1, "a2": "/elsewhere/a2.jsonl"})

    def test_an_explicit_link_overrides_the_description_tag(self):
        st = self.state([ev("agent_pre", "t", session="s", tool_use_id="u1", task="T-1"),
                         ev("agent_post", "t", session="s", tool_use_id="u1", launched=True, agent_id="a1"),
                         ev("agent_pre", "t", session="s", tool_use_id="u2", task="T-1"),
                         ev("agent_post", "t", session="s", tool_use_id="u2", launched=True, agent_id="a2"),
                         ev("agent_pre", "t", session="s", tool_use_id="u3"),
                         ev("task_set", "t", id="T-2", agent="a2")])
        owners = bs.task_agents(st)
        self.assertEqual(owners, {"T-1": ["a1"], "T-2": ["a2"]})

    def test_remaining_minutes_count_down_from_the_start(self):
        self.assertIsNone(bs.remaining_min({"eta_min": None}, NOW))
        self.assertEqual(bs.remaining_min({"eta_min": 30, "started": None}, NOW), 30)
        self.assertEqual(bs.remaining_min({"eta_min": 30, "started": "2026-09-30T11:50:00+00:00"}, NOW), 20)
        self.assertEqual(bs.remaining_min({"eta_min": 30, "started": "2026-09-30T10:00:00+00:00"}, NOW), 0)

    def test_task_cost_spent_estimate_and_projection(self):
        a1 = self.write(self.subdir / "agent-a1.jsonl",
                        msg("m1", "2026-09-30T11:00:00Z", input_tokens=M))  # $4 an hour ago
        task = {"eta_min": 90, "est_cost": 3.0, "started": "2026-09-30T11:30:00+00:00"}
        c = bs.task_cost(task, ["a1", "gone"], {"a1": a1}, Cache(), NOW)
        self.assertEqual((c["agents"], c["measured_agents"]), (2, 1))
        self.assertEqual(c["spent"]["cost"], 4)
        self.assertEqual(c["remaining_min"], 60)
        # The rate runs from the first spend (11:00), not the later start: $4/h x 1 h left.
        self.assertEqual(c["projected"], 8)
        self.assertEqual((c["est_left"], c["over_estimate"]), (0, True))
        c = bs.task_cost(dict(task, est_cost=10), ["a1"], {"a1": a1}, Cache(), NOW)
        self.assertEqual((c["est_left"], c["over_estimate"]), (6, False))

    def test_a_task_without_agents_or_eta_projects_nothing(self):
        c = bs.task_cost({"eta_min": None, "est_cost": 2, "started": None}, [], {}, Cache(), NOW)
        self.assertEqual((c["spent"], c["projected"], c["est_left"], c["over_estimate"]),
                         (None, None, None, False))
        a1 = self.write(self.subdir / "agent-a1.jsonl", msg("m1", "2026-09-30T11:00:00Z", input_tokens=M))
        c = bs.task_cost({"eta_min": None, "est_cost": None, "started": None}, ["a1"], {"a1": a1}, Cache(), NOW)
        self.assertEqual((c["spent"]["cost"], c["projected"]), (4, None))

    def test_an_unpriced_agent_leaves_the_task_cost_unmeasured(self):
        a1 = self.write(self.subdir / "agent-a1.jsonl",
                        msg("m1", "2026-09-30T11:00:00Z", model="claude-sonnet-5", input_tokens=5))
        c = bs.task_cost({"eta_min": 60, "est_cost": 1, "started": "2026-09-30T11:00:00+00:00"},
                         ["a1"], {"a1": a1}, Cache(), NOW)
        self.assertEqual((c["spent"]["cost"], c["projected"], c["est_left"]), (None, None, None))

    def test_open_eta_counts_only_open_tasks_and_names_those_without_eta(self):
        st = {"tasks": {"T-1": {"status": "running", "eta_min": 30, "started": None},
                        "T-2": {"status": "planned", "eta_min": 90, "started": None},
                        "T-3": {"status": "waiting", "eta_min": None},
                        "T-4": {"status": "done", "eta_min": 600, "started": None},
                        "T-5": {"status": "removed", "eta_min": 600, "started": None}}}
        self.assertEqual(bs.open_eta(st, NOW), {"remaining_h": 2.0, "eta_tasks": 2,
                                                "no_eta_tasks": 1, "window_s": 3600})

    def test_session_view_rate_projection_and_context_warning(self):
        self.write(self.main,
                   msg("m0", "2026-09-30T10:00:00Z", input_tokens=M),               # outside the hour
                   msg("m1", "2026-09-30T11:30:00Z", cache_read_input_tokens=800_000))  # $0.16, 80% context
        self.write(self.subdir / "agent-a1.jsonl", msg("x", "2026-09-30T11:40:00Z", output_tokens=M))
        sess = {"id": "s1", "state": "busy", "since": "t", "last": "t", "transcript": str(self.main)}
        v = bs.session_view(sess, Cache(), {"remaining_h": 2.0}, NOW)
        self.assertEqual(v["orchestration"]["cost"], 4.16)
        self.assertEqual(v["agents"]["cost"], 20)
        self.assertEqual(v["total"]["cost"], 24.16)
        self.assertEqual(v["subagents"], 1)
        self.assertEqual(v["rate_per_h"], 20.16)
        self.assertEqual(v["projected"], round(24.16 + 20.16 * 2, 4))
        self.assertEqual(v["context"]["ratio"], 0.8)
        self.assertTrue(v["context_warn"])       # at 80% exactly

    def test_below_eighty_percent_there_is_no_warning_and_no_transcript_means_unmeasured(self):
        self.write(self.main, msg("m1", "2026-09-30T11:30:00Z", cache_read_input_tokens=799_999))
        sess = {"id": "s1", "transcript": str(self.main)}
        self.assertFalse(bs.session_view(sess, Cache(), {"remaining_h": 0}, NOW)["context_warn"])
        bare = bs.session_view({"id": "s9", "transcript": None}, Cache(), {"remaining_h": 1}, NOW)
        self.assertEqual((bare["has_transcript"], bare["orchestration"], bare["context"],
                          bare["context_warn"]), (False, None, None, False))
        self.assertEqual((bare["total"]["cost"], bare["rate_per_h"]), (0, 0))

    def test_an_unpriced_session_has_no_rate_or_projection(self):
        self.write(self.main, msg("m1", "2026-09-30T11:30:00Z", model="claude-sonnet-5", input_tokens=5))
        v = bs.session_view({"id": "s1", "transcript": str(self.main)}, Cache(), {"remaining_h": 1}, NOW)
        self.assertEqual((v["rate_per_h"], v["projected"]), (None, None))

    def test_board_costs_hides_old_sessions_and_sorts_by_activity(self):
        st = fold([ev("turn_start", "2026-09-28T10:00:00+00:00", session="old"),
                   ev("turn_start", "2026-09-30T09:00:00+00:00", session="mid"),
                   ev("turn_start", "2026-09-30T11:00:00+00:00", session="new"),
                   ev("task_add", "2026-09-30T11:00:00+00:00", id="T-1", title="x")])
        out = bs.board_costs(st, Cache(), NOW)
        self.assertEqual([s["id"] for s in out["sessions"]], ["new", "mid"])
        self.assertEqual(set(out["tasks"]), {"T-1"})
        self.assertEqual(out["warn_ratio"], 0.8)
        self.assertEqual(out["basis"]["no_eta_tasks"], 1)


if __name__ == "__main__":
    unittest.main()
