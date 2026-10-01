"""Tokens and dollar cost read from transcripts (scripts/board/board_cost.py,
docs/live-board.md §2c): pricing, deduplication, incremental reading, context use."""
import json
import sys
import tempfile
import unittest
from pathlib import Path

BOARD = Path(__file__).resolve().parent.parent / "board"
sys.path.insert(0, str(BOARD))

import board_cost  # noqa: E402
from board_config import CONTEXT_WINDOW, PRICING_USD_PER_MTOK  # noqa: E402

M = 1_000_000


def entry(mid, model="claude-opus-5-5", ts="2026-09-30T10:00:00.000Z", side=False, **usage):
    u = {"input_tokens": 0, "output_tokens": 0, "cache_read_input_tokens": 0}
    u.update(usage)
    return {"type": "assistant", "isSidechain": side, "timestamp": ts,
            "message": {"id": mid, "model": model, "usage": u}}


def line(obj) -> str:
    return json.dumps(obj) + "\n"


class PricingTests(unittest.TestCase):
    def test_model_ids_match_exactly_or_with_a_suffix_only(self):
        key = board_cost.model_key
        self.assertEqual(key("claude-opus-5-5", PRICING_USD_PER_MTOK), "claude-opus-5-5")
        self.assertEqual(key("claude-haiku-4-5-20251001", PRICING_USD_PER_MTOK), "claude-haiku-4-5")
        self.assertEqual(key("claude-opus-5-5[1m]", PRICING_USD_PER_MTOK), "claude-opus-5-5")
        self.assertIsNone(key("claude-sonnet-5", PRICING_USD_PER_MTOK))       # an older model
        self.assertIsNone(key("claude-opus-5-50", PRICING_USD_PER_MTOK))      # not a suffix
        self.assertIsNone(key("<synthetic>", PRICING_USD_PER_MTOK))
        self.assertIsNone(key(None, PRICING_USD_PER_MTOK))
        self.assertIsNone(key("", PRICING_USD_PER_MTOK))

    def test_one_million_of_each_token_kind_costs_the_table_price(self):
        one_each = (M, M, M, M, M)
        # in + out + cache read + 5m write (in x 1.25) + 1h write (in x 2)
        self.assertAlmostEqual(board_cost.cost_of("claude-opus-5-5", one_each), 4 + 20 + 0.2 + 5 + 8)
        self.assertAlmostEqual(board_cost.cost_of("claude-sonnet-5-5", one_each), 2 + 10 + 0.2 + 2.5 + 4)
        self.assertAlmostEqual(board_cost.cost_of("claude-haiku-4-5", one_each), 1 + 5 + 0.1 + 1.25 + 2)

    def test_each_token_kind_has_its_own_price(self):
        cost = board_cost.cost_of
        self.assertAlmostEqual(cost("claude-opus-5-5", (M, 0, 0, 0, 0)), 4)
        self.assertAlmostEqual(cost("claude-opus-5-5", (0, M, 0, 0, 0)), 20)
        self.assertAlmostEqual(cost("claude-opus-5-5", (0, 0, M, 0, 0)), 0.2)
        self.assertAlmostEqual(cost("claude-opus-5-5", (0, 0, 0, M, 0)), 5)
        self.assertAlmostEqual(cost("claude-opus-5-5", (0, 0, 0, 0, M)), 8)

    def test_an_unknown_model_is_not_priced(self):
        self.assertIsNone(board_cost.cost_of("claude-sonnet-5", (M, M, 0, 0, 0)))

    def test_cache_writes_split_by_ttl_and_fall_back_to_the_undivided_figure(self):
        split = {"input_tokens": 1, "output_tokens": 2, "cache_read_input_tokens": 3,
                 "cache_creation_input_tokens": 99,
                 "cache_creation": {"ephemeral_5m_input_tokens": 4, "ephemeral_1h_input_tokens": 5}}
        self.assertEqual(board_cost.tokens_of(split), (1, 2, 3, 4, 5))
        old = {"input_tokens": 1, "cache_creation_input_tokens": 7}
        self.assertEqual(board_cost.tokens_of(old), (1, 0, 0, 7, 0))
        only_1h = {"cache_creation": {"ephemeral_1h_input_tokens": 6}}
        self.assertEqual(board_cost.tokens_of(only_1h), (0, 0, 0, 0, 6))
        self.assertEqual(board_cost.tokens_of({}), (0, 0, 0, 0, 0))


class TranscriptTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.path = Path(self.tmp.name) / "s.jsonl"
        self.path.write_text("")

    def append(self, text: str) -> None:
        with self.path.open("a") as f:
            f.write(text)

    def test_streamed_duplicates_count_once_and_the_last_write_wins(self):
        self.append(line(entry("m1", output_tokens=5)) + line(entry("m1", output_tokens=500)))
        tr = board_cost.Transcript(self.path).update()
        self.assertEqual(len(tr.messages), 1)
        self.assertEqual(board_cost.summarize([tr])["tokens"]["output"], 500)

    def test_only_new_bytes_are_read_and_a_half_line_waits(self):
        tr = board_cost.Transcript(self.path)
        self.append(line(entry("m1", input_tokens=10)))
        tr.update()
        first = tr.offset
        self.assertEqual(first, self.path.stat().st_size)
        half = line(entry("m2", input_tokens=20))
        self.append(half[:15])
        tr.update()
        self.assertEqual(tr.offset, first)      # the unfinished line is not consumed
        self.assertNotIn("m2", tr.messages)
        self.append(half[15:])
        tr.update()
        self.assertIn("m2", tr.messages)
        self.assertEqual(board_cost.summarize([tr])["tokens"]["input"], 30)
        tr.update()                              # nothing new: nothing changes
        self.assertEqual(board_cost.summarize([tr])["tokens"]["input"], 30)

    def test_a_truncated_file_is_read_again_from_the_start(self):
        self.append(line(entry("m1", input_tokens=10)) + line(entry("m2", input_tokens=10)))
        tr = board_cost.Transcript(self.path).update()
        self.path.write_text(line(entry("m9", input_tokens=1)))
        tr.update()
        self.assertEqual(set(tr.messages), {"m9"})

    def test_other_entries_and_broken_lines_are_skipped(self):
        self.append("not json\n" + line({"type": "user", "message": {"usage": {"input_tokens": 9}}})
                    + line(["a", "list"]) + line({"type": "assistant", "message": {"id": "x"}})
                    + line(entry("m1", input_tokens=3)))
        tr = board_cost.Transcript(self.path).update()
        self.assertEqual(list(tr.messages), ["m1"])

    def test_a_missing_file_is_left_alone(self):
        tr = board_cost.Transcript(Path(self.tmp.name) / "gone.jsonl").update()
        self.assertEqual(tr.messages, {})

    def test_context_is_the_last_main_thread_call(self):
        self.append(line(entry("m1", input_tokens=1, cache_read_input_tokens=100,
                               cache_creation={"ephemeral_5m_input_tokens": 10,
                                               "ephemeral_1h_input_tokens": 5}))
                    + line(entry("side", side=True, input_tokens=999_999)))
        tr = board_cost.Transcript(self.path).update()
        self.assertEqual(tr.context, ("claude-opus-5-5", 116))  # output tokens are not context
        use = board_cost.context_use(tr)
        self.assertEqual(use["window"], CONTEXT_WINDOW["claude-opus-5-5"])
        self.assertAlmostEqual(use["ratio"], 116 / 1_000_000, places=4)

    def test_context_of_an_unknown_model_has_no_ratio(self):
        self.append(line(entry("m1", model="claude-sonnet-5", input_tokens=50)))
        use = board_cost.context_use(board_cost.Transcript(self.path).update())
        self.assertEqual((use["tokens"], use["window"], use["ratio"]), (50, None, None))
        self.assertIsNone(board_cost.context_use(None))
        self.assertIsNone(board_cost.context_use(board_cost.Transcript(self.path)))

    def test_haiku_has_the_smaller_window(self):
        self.append(line(entry("m1", model="claude-haiku-4-5-20251001", input_tokens=100_000)))
        use = board_cost.context_use(board_cost.Transcript(self.path).update())
        self.assertEqual(use["ratio"], 0.5)


class SummaryTests(unittest.TestCase):
    def tr(self, *entries):
        t = board_cost.Transcript(Path("unused"))
        for e in entries:
            t._take(line(e).encode())
        return t

    def test_totals_add_up_across_transcripts(self):
        a = self.tr(entry("a", input_tokens=M))
        b = self.tr(entry("b", model="claude-haiku-4-5", output_tokens=M))
        s = board_cost.summarize([a, b])
        self.assertEqual(s["cost"], 4 + 5)
        self.assertEqual(s["messages"], 2)
        self.assertEqual(s["tokens"]["input"], M)
        self.assertEqual(s["unpriced"], [])

    def test_one_unpriced_model_makes_the_whole_cost_unmeasured(self):
        s = board_cost.summarize([self.tr(entry("a", input_tokens=M),
                                          entry("b", model="claude-sonnet-5", input_tokens=1))])
        self.assertIsNone(s["cost"])
        self.assertIsNone(s["window_cost"])
        self.assertEqual(s["unpriced"], ["claude-sonnet-5"])
        self.assertEqual(s["tokens"]["input"], M + 1)  # tokens are still counted

    def test_an_empty_unpriced_entry_costs_nothing(self):
        s = board_cost.summarize([self.tr(entry("a", input_tokens=M), entry("b", model="<synthetic>"))])
        self.assertEqual(s["cost"], 4)

    def test_the_window_counts_only_recent_messages_and_first_is_the_earliest(self):
        t = self.tr(entry("old", ts="2026-09-30T08:00:00Z", input_tokens=M),
                    entry("new", ts="2026-09-30T10:00:00Z", input_tokens=M),
                    entry("nots", ts=None, input_tokens=M))
        since = board_cost.epoch("2026-09-30T09:00:00Z")
        s = board_cost.summarize([t], since)
        self.assertEqual(s["cost"], 12)
        self.assertEqual(s["window_cost"], 4)
        self.assertEqual(s["first"], board_cost.epoch("2026-09-30T08:00:00Z"))
        self.assertEqual(board_cost.summarize([t])["window_cost"], 0)
        self.assertIsNone(board_cost.summarize([])["first"])

    def test_bad_stamps_have_no_epoch(self):
        self.assertIsNone(board_cost.epoch("yesterday"))
        self.assertIsNone(board_cost.epoch(None))


class CacheTests(unittest.TestCase):
    def test_one_transcript_per_path_updated_on_every_get(self):
        with tempfile.TemporaryDirectory() as tmp:
            p = Path(tmp) / "s.jsonl"
            p.write_text(line(entry("m1", input_tokens=1)))
            cache = board_cost.Cache()
            first = cache.get(str(p))
            with p.open("a") as f:
                f.write(line(entry("m2", input_tokens=1)))
            again = cache.get(p)
            self.assertIs(first, again)
            self.assertEqual(len(again.messages), 2)
            self.assertIsNone(cache.get(Path(tmp) / "missing.jsonl"))
            self.assertIsNone(cache.get(None))
            self.assertIsNone(cache.get(""))


if __name__ == "__main__":
    unittest.main()
