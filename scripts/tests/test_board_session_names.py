"""A session's name in the sessions table (read from its transcript) and finding the transcript of a
session whose events never carried a path (T-33/T-34, docs/live-board.md §2c)."""
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "board"))

import board_sessions as bs  # noqa: E402
from board_cost import Cache, Transcript  # noqa: E402
from board_config import TITLE_MAX  # noqa: E402

SID = "b6ae971c-e277-46e4-a0d3-9139d9771b01"
NOW = 1_000_000.0


def write(path: Path, *entries) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("".join(json.dumps(e) + "\n" for e in entries))
    return path


def title(text, kind="custom-title"):
    return {"type": kind, {"custom-title": "customTitle", "agent-name": "agentName"}[kind]: text, "sessionId": SID}


class TitleTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.path = Path(self.tmp.name) / "t.jsonl"

    def title(self, *entries):
        write(self.path, *entries)
        return Transcript(self.path).update().title

    def test_the_last_custom_title_wins(self):
        self.assertEqual(self.title(title("first"), title("renamed")), "renamed")

    def test_a_custom_title_beats_an_agent_name_whatever_the_order(self):
        self.assertEqual(self.title(title("mine"), title("agent's", "agent-name")), "mine")
        self.assertEqual(self.title(title("agent's", "agent-name"), title("mine")), "mine")

    def test_the_agent_name_is_used_when_there_is_no_custom_title(self):
        self.assertEqual(self.title(title("Çalışma modu", "agent-name")), "Çalışma modu")

    def test_no_title_entry_means_none(self):
        self.assertIsNone(self.title({"type": "user"}))

    def test_junk_values_are_ignored_and_long_ones_cut(self):
        self.assertIsNone(self.title({"type": "custom-title", "customTitle": 5}, {"type": "custom-title"},
                                     {"type": "custom-title", "customTitle": "   "}))
        self.assertEqual(len(self.title(title("x" * 500))), TITLE_MAX)

    def test_a_rename_written_later_is_picked_up_incrementally(self):
        write(self.path, title("old"))
        tr = Transcript(self.path).update()
        with self.path.open("a") as f:
            f.write(json.dumps(title("new")) + "\n")
        self.assertEqual(tr.update().title, "new")


class FindTranscriptTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        bs._LOOKUPS.clear()

    def test_finds_the_file_under_any_project_directory(self):
        f = write(self.root / "-Users-me-proj" / f"{SID}.jsonl", {"type": "user"})
        self.assertEqual(bs.find_transcript(SID, self.root, NOW), str(f))

    def test_a_missing_session_is_none(self):
        self.assertIsNone(bs.find_transcript(SID, self.root, NOW))
        self.assertIsNone(bs.find_transcript(SID, self.root / "nope", NOW))

    def test_an_id_that_is_not_a_session_id_is_never_globbed(self):
        write(self.root / "p" / "secret.jsonl", {"type": "user"})
        for bad in ("../x", "*", "a/b", "", None, "short", "x" * 65):
            self.assertIsNone(bs.find_transcript(bad, self.root, NOW), bad)

    def test_a_miss_is_remembered_for_the_ttl_then_retried(self):
        self.assertIsNone(bs.find_transcript(SID, self.root, NOW))
        f = write(self.root / "p" / f"{SID}.jsonl", {"type": "user"})
        self.assertIsNone(bs.find_transcript(SID, self.root, NOW + 1))       # still inside the TTL
        self.assertEqual(bs.find_transcript(SID, self.root, NOW + 31), str(f))  # retried after it

    def test_a_hit_is_kept(self):
        f = write(self.root / "p" / f"{SID}.jsonl", {"type": "user"})
        self.assertEqual(bs.find_transcript(SID, self.root, NOW), str(f))
        f.unlink()
        self.assertEqual(bs.find_transcript(SID, self.root, NOW + 1000), str(f))  # the cache serves it


class SessionViewTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        bs._LOOKUPS.clear()
        self.basis = {"remaining_h": 0.0, "eta_tasks": 0, "no_eta_tasks": 0, "window_s": 3600}

    def test_a_session_without_a_recorded_path_is_measured_through_the_lookup(self):
        f = write(Path(self.tmp.name) / "p" / f"{SID}.jsonl", title("live board"))
        real = bs.TRANSCRIPTS_ROOT
        bs.TRANSCRIPTS_ROOT = Path(self.tmp.name)
        try:
            v = bs.session_view({"id": SID, "state": "idle", "since": None, "last": None, "transcript": None},
                                Cache(), self.basis, NOW)
        finally:
            bs.TRANSCRIPTS_ROOT = real
        self.assertTrue(v["has_transcript"])
        self.assertEqual(v["title"], "live board")
        self.assertTrue(f.exists())

    def test_a_recorded_path_wins_over_the_lookup(self):
        f = write(Path(self.tmp.name) / "rec.jsonl", title("recorded"))
        v = bs.session_view({"id": SID, "state": "idle", "since": None, "last": None, "transcript": str(f)},
                            Cache(), self.basis, NOW)
        self.assertEqual(v["title"], "recorded")

    def test_an_unmeasurable_session_has_no_title_and_says_so(self):
        real = bs.TRANSCRIPTS_ROOT
        bs.TRANSCRIPTS_ROOT = Path(self.tmp.name)  # empty: never the developer's real transcripts
        try:
            v = bs.session_view({"id": SID, "state": "idle", "since": None, "last": None, "transcript": None},
                                Cache(), self.basis, NOW)
        finally:
            bs.TRANSCRIPTS_ROOT = real
        self.assertFalse(v["has_transcript"])
        self.assertIsNone(v["title"])


if __name__ == "__main__":
    unittest.main()
