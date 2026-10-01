"""The Agent-activity cost figures (scripts/board/board_sessions.py agent_costs and its use in
board_costs()["agent_rows"], docs/live-board.md §2c): per row, total, unmeasured, by type."""
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


def msg(mid, ts="2026-09-30T11:00:00Z", model="claude-opus-5-5", **usage):
    return json.dumps({"type": "assistant", "timestamp": ts, "isSidechain": True,
                       "message": {"id": mid, "model": model, "usage": usage}}) + "\n"


def spawn(key, agent_type="analyst", agent_id=None, **kw):
    """Events of one Agent call; with agent_id the call has reported its id (launched)."""
    out = [ev("agent_pre", "2026-09-30T10:00:00+00:00", session="s1", tool_use_id=key,
              agent_type=agent_type, **kw)]
    if agent_id:
        out.append(ev("agent_post", "2026-09-30T10:00:01+00:00", session="s1", tool_use_id=key,
                      launched=True, agent_id=agent_id))
    return out


class AgentCostTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.main = self.root / "s1.jsonl"
        self.subdir = self.root / "s1" / "subagents"
        self.subdir.mkdir(parents=True)

    def write(self, path: Path, *lines: str) -> str:
        path.write_text("".join(lines))
        return str(path)

    def sub(self, agent_id, *lines):
        return self.write(self.subdir / f"agent-{agent_id}.jsonl", *lines)

    def session(self):
        return [ev("turn_start", "2026-09-30T09:59:00+00:00", session="s1", transcript=str(self.main))]

    def test_an_agent_reported_by_the_stop_hook_is_measured_with_its_tokens_and_dollars(self):
        path = self.write(self.root / "elsewhere.jsonl",
                          msg("m1", input_tokens=M, output_tokens=M))  # opus: $4 in + $20 out
        st = fold(spawn("u1", agent_id="a1") + [
            ev("agent_stop", "2026-09-30T10:05:00+00:00", session="s1", agent_id="a1", agent_transcript=path)])
        out = bs.agent_costs(st, Cache())
        row = out["rows"]["u1"]
        self.assertTrue(row["measured"])
        self.assertEqual(row["summary"]["cost"], 24)
        self.assertEqual((row["summary"]["tokens"]["input"], row["summary"]["tokens"]["output"]), (M, M))
        self.assertEqual(out["unmeasured"], 0)

    def test_an_agent_found_in_the_session_subagent_folder_is_measured(self):
        self.sub("a1", msg("m1", input_tokens=M))
        st = fold(self.session() + spawn("u1", agent_id="a1"))
        row = bs.agent_costs(st, Cache())["rows"]["u1"]
        self.assertTrue(row["measured"])
        self.assertEqual((row["summary"]["cost"], row["summary"]["tokens"]["input"]), (4, M))

    def test_a_row_without_an_agent_id_is_not_measured(self):
        st = fold(spawn("u1"))  # still starting: no agent_post yet
        out = bs.agent_costs(st, Cache())
        self.assertEqual(out["rows"]["u1"], {"measured": False, "summary": None})
        self.assertEqual(out["unmeasured"], 1)
        self.assertEqual(out["by_type"], {})

    def test_a_row_whose_transcript_file_is_missing_is_not_measured(self):
        st = fold(self.session() + spawn("u1", agent_id="ghost"))  # no agent-ghost.jsonl on disk
        out = bs.agent_costs(st, Cache())
        self.assertEqual(out["rows"]["u1"], {"measured": False, "summary": None})
        self.assertEqual(out["unmeasured"], 1)
        st = fold(spawn("u2", agent_id="a2") + [ev("agent_stop", "t", session="s1", agent_id="a2",
                                                    agent_transcript=str(self.root / "gone.jsonl"))])
        self.assertFalse(bs.agent_costs(st, Cache())["rows"]["u2"]["measured"])

    def test_a_running_agents_cost_grows_with_its_transcript_on_the_same_cache(self):
        path = Path(self.sub("a1", msg("m1", input_tokens=M)))
        st = fold(self.session() + spawn("u1", agent_id="a1"))
        cache = Cache()
        first = bs.agent_costs(st, cache)
        self.assertEqual(st["agents"]["u1"]["status"], "running")
        self.assertEqual(first["rows"]["u1"]["summary"]["cost"], 4)
        with path.open("a") as f:
            f.write(msg("m2", output_tokens=M))
        second = bs.agent_costs(st, cache)
        self.assertEqual(second["rows"]["u1"]["summary"]["cost"], 24)
        self.assertEqual(second["total"]["cost"], 24)
        self.assertEqual(second["by_type"]["analyst"]["cost"], 24)

    def test_an_unpriced_model_leaves_the_cost_unmeasured_and_is_named(self):
        self.sub("a1", msg("m1", model="claude-mystery-9", input_tokens=5))
        st = fold(self.session() + spawn("u1", agent_id="a1"))
        out = bs.agent_costs(st, Cache())
        s = out["rows"]["u1"]["summary"]
        self.assertTrue(out["rows"]["u1"]["measured"])
        self.assertEqual((s["cost"], s["unpriced"], s["tokens"]["input"]), (None, ["claude-mystery-9"], 5))
        self.assertIsNone(out["total"]["cost"])
        self.assertEqual(out["total"]["unpriced"], ["claude-mystery-9"])

    def test_the_total_covers_only_the_measured_rows(self):
        self.sub("a1", msg("m1", input_tokens=M))
        self.sub("a2", msg("m2", output_tokens=M))
        st = fold(self.session() + spawn("u1", "analyst", agent_id="a1") + spawn("u2", "qa", agent_id="a2")
                  + spawn("u3") + spawn("u4", agent_id="ghost"))
        out = bs.agent_costs(st, Cache())
        self.assertEqual(out["total"]["cost"], 24)
        self.assertEqual((out["total"]["tokens"]["input"], out["total"]["tokens"]["output"]), (M, M))
        self.assertEqual(out["total"]["messages"], 2)
        self.assertEqual(out["unmeasured"], 2)
        self.assertEqual({k for k, r in out["rows"].items() if r["measured"]}, {"u1", "u2"})

    def test_nothing_measured_gives_a_zero_total_and_every_row_unmeasured(self):
        out = bs.agent_costs(fold(spawn("u1") + spawn("u2")), Cache())
        self.assertEqual((out["total"]["cost"], out["total"]["messages"], out["unmeasured"]), (0, 0, 2))
        empty = bs.agent_costs(fold([]), Cache())
        self.assertEqual((empty["rows"], empty["unmeasured"], empty["by_type"]), ({}, 0, {}))

    def test_by_type_sums_agents_of_one_type_and_keeps_other_types_apart(self):
        self.sub("a1", msg("m1", input_tokens=M))
        self.sub("a2", msg("m2", input_tokens=2 * M))
        self.sub("a3", msg("m3", output_tokens=M))
        st = fold(self.session() + spawn("u1", "analyst", agent_id="a1") + spawn("u2", "analyst", agent_id="a2")
                  + spawn("u3", "qa", agent_id="a3") + spawn("u4", "developer"))
        by = bs.agent_costs(st, Cache())["by_type"]
        self.assertEqual(set(by), {"analyst", "qa"})  # an unmeasured type has no entry
        self.assertEqual(by["analyst"]["cost"], 12)
        self.assertEqual(by["analyst"]["tokens"]["input"], 3 * M)
        self.assertEqual(by["qa"]["cost"], 20)

    def test_denied_rows_are_not_measured(self):
        self.sub("a1", msg("m1", input_tokens=M))
        st = fold(self.session() + spawn("u1", agent_id="a1")
                  + [ev("agent_denied", "2026-09-30T10:00:00+00:00", session="s1", tool_use_id="d1",
                        agent_type="security", reason="outside mode")])
        out = bs.agent_costs(st, Cache())
        self.assertEqual(out["rows"]["d1"], {"measured": False, "summary": None})
        self.assertEqual(out["total"]["cost"], 4)
        self.assertNotIn("security", out["by_type"])
        self.assertEqual(out["unmeasured"], 0)  # it never ran: nothing is "not measured yet"

    def test_board_costs_carries_the_agent_rows(self):
        self.sub("a1", msg("m1", input_tokens=M))
        st = fold(self.session() + spawn("u1", agent_id="a1") + spawn("u2"))
        out = bs.board_costs(st, Cache(), NOW)
        rows = out["agent_rows"]
        self.assertEqual(set(rows), {"rows", "total", "unmeasured", "by_type"})
        self.assertEqual((rows["rows"]["u1"]["summary"]["cost"], rows["unmeasured"], rows["total"]["cost"]),
                         (4, 1, 4))
        self.assertEqual(rows["by_type"]["analyst"]["cost"], 4)


if __name__ == "__main__":
    unittest.main()
