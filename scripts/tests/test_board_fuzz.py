"""Hostile input to the three places that read what a hook, a crash or a newer version wrote.

The event log is append-only and shared by every session; one bad line must never take the board
down. A seeded generator (same seeds every run, so a failure reproduces) feeds:

  · read_events: truncated lines, blank lines, bytes that are not UTF-8, JSON that is not an object;
  · fold: events of every known type with fields of the wrong type, missing ids, huge values;
  · /api/state on a real server over such a log: still 200, still JSON;
  · the hook as a process: garbage on stdin and random payloads - exit 0, stdout empty or valid JSON,
    and the log stays parseable.
"""
import io
import json
import os
import random
import subprocess
import sys
import tempfile
import threading
import unittest
import urllib.request
from contextlib import redirect_stderr
from http.server import ThreadingHTTPServer
from pathlib import Path
from unittest import mock

BOARD = Path(__file__).resolve().parent.parent / "board"
sys.path.insert(0, str(BOARD))

import board_hook  # noqa: E402
import board_server  # noqa: E402
from board_config import EVENTS_FILE  # noqa: E402
from board_registry import register  # noqa: E402
from board_store import fold, read_events  # noqa: E402

SEEDS = range(60)
TYPES = ["plan", "mode_set", "task_add", "task_session", "task_set", "agent_pre", "agent_denied",
         "agent_start", "agent_post", "agent_stop", "turn_start", "turn_stop", "todo_sync", "todo_add",
         "todo_update", "from_a_newer_version", None, 7, ["plan"]]
FIELDS = ["ts", "session", "id", "title", "branch", "role", "note", "status", "mode", "by", "roles", "commits",
          "options", "eta_min", "est_cost", "agent", "task", "tool_use_id", "agent_id", "agent_type",
          "description", "reason", "background", "launched", "transcript", "agent_transcript", "todos", "item", "change"]
VALUES = [None, "", "T-1", "T-2", "u1", "a1", "s1", "running", "done", "B", "2026-10-02T10:00:00Z", 0, 1, -1, 3.5,
          10 ** 30, True, False, [], ["T-1"], ["a", 1], {}, {"id": "T-1"}, [{"content": "x", "status": "pending"}],
          [{"id": "T-1"}], {"id": "T-1", "content": "c"}, {"id": "T-1", "status": "deleted"},
          {"id": "T-1", "status": "in_progress", "content": "c"}, [{"id": "T-1", "content": "c", "status": "pending"}] * 80,
          "x" * 5000, "\x00", "é", "<script>alert(1)</script>"]


def random_todo_event(rng: random.Random) -> dict:
    """Todo events on ONE session and two ids, so that an update really meets an item to change."""
    kind = rng.choice(["todo_sync", "todo_add", "todo_update"])
    item = {"id": rng.choice(["T-1", "T-2"]), **{k: rng.choice(["c", "", None, 3, "pending", "in_progress"])
                                                  for k in rng.sample(["content", "status", "active"], rng.randint(0, 3))}}
    ev = {"type": kind, "session": "s1"}
    if kind == "todo_sync":
        ev["todos"] = [dict(item, id=i) for i in rng.sample(["T-1", "T-2", "T-3"], rng.randint(0, 3))]
    elif kind == "todo_add":
        ev["item"] = item
    else:
        ev["change"] = item
    return ev


def random_event(rng: random.Random) -> dict:
    if rng.random() < 0.25:
        return random_todo_event(rng)
    ev = {"type": rng.choice(TYPES)}
    for name in rng.sample(FIELDS, rng.randint(0, 9)):
        ev[name] = rng.choice(VALUES)
    return ev


def random_lines(rng: random.Random, count: int) -> bytes:
    """A log with every kind of damage in it."""
    out = []
    for _ in range(count):
        roll = rng.random()
        good = json.dumps(random_event(rng)).encode()
        if roll < 0.55:
            out.append(good)
        elif roll < 0.65:
            out.append(good[: rng.randint(0, len(good))])                    # cut off mid-line
        elif roll < 0.75:
            out.append(bytes(rng.randrange(256) for _ in range(rng.randint(1, 60))))   # not even text
        elif roll < 0.85:
            out.append(rng.choice([b"5", b"null", b"true", b'"text"', b"[1, 2]", b"[]", b"{}", b"1e999"]))
        elif roll < 0.9:
            out.append(b"")
        else:
            out.append(b"\xff\xfe" + good)                                   # a bad byte, then a good line
    return b"\n".join(out) + b"\n"


class ReadingTheLog(unittest.TestCase):
    def test_damage_is_skipped_and_only_objects_come_back(self):
        for seed in SEEDS:
            with tempfile.TemporaryDirectory() as tmp:
                (Path(tmp) / EVENTS_FILE).write_bytes(random_lines(random.Random(seed), 200))
                with redirect_stderr(io.StringIO()):
                    events = read_events(Path(tmp))
                self.assertTrue(all(isinstance(e, dict) for e in events), f"seed {seed}")

    def test_a_valid_line_after_damage_still_counts(self):
        with tempfile.TemporaryDirectory() as tmp:
            good = json.dumps({"type": "plan", "mode": "B"}).encode()
            (Path(tmp) / EVENTS_FILE).write_bytes(b"\xff\xfe\x00junk\n5\nnull\n" + good + b"\n" + good[:7] + b"\n")
            with redirect_stderr(io.StringIO()):
                self.assertEqual(read_events(Path(tmp)), [{"type": "plan", "mode": "B"}])


class Folding(unittest.TestCase):
    def test_no_event_can_break_the_fold_or_its_json(self):
        for seed in SEEDS:
            rng = random.Random(seed)
            events = [random_event(rng) for _ in range(300)]
            with redirect_stderr(io.StringIO()):
                state = fold(events)
            json.dumps(state)                       # the server serialises exactly this
            self.assertIsInstance(state["tasks"], dict, f"seed {seed}")

    def test_an_update_of_an_item_that_has_no_status_does_not_raise(self):
        events = [{"type": "todo_sync", "session": "s", "todos": [{"id": "T-1"}]},
                  {"type": "todo_update", "session": "s", "change": {"id": "T-1", "content": "c"}}]
        todos = fold(events)["sessions"]["s"]["todos"]
        self.assertEqual((todos[0]["content"], todos[0]["active"]), ("c", "c"))

    def test_a_good_event_after_bad_ones_still_applies(self):
        bad = [{"type": "task_add"}, {"type": "task_set", "id": ["T-1"]}, {"type": "agent_pre"},
               {"type": "agent_post", "tool_use_id": {}}, {"type": "task_add", "id": 5}]
        good = [{"type": "task_add", "id": "T-9", "title": "kept"}]
        with redirect_stderr(io.StringIO()):
            state = fold(bad + good)
        self.assertEqual(state["tasks"]["T-9"]["title"], "kept")

    def test_a_malformed_event_changes_nothing(self):
        before = fold([{"type": "task_add", "id": "T-1", "title": "a"}])
        with redirect_stderr(io.StringIO()):
            after = fold([{"type": "task_add", "id": "T-1", "title": "a"}, {"type": "agent_pre", "task": ["T-1"]}])
        self.assertEqual(before["agents"], after["agents"])
        self.assertEqual(before["tasks"], after["tasks"])


class TheEventCheck(unittest.TestCase):
    def test_what_it_accepts_and_refuses(self):
        import board_event_check as check
        ok = [{"type": "plan", "roles": None}, {"type": "task_add", "id": "T-1", "commits": ["a"], "eta_min": 5},
              {"type": "agent_pre", "tool_use_id": "u"}, {"type": "todo_sync", "todos": []}, {"type": "unknown_new"}]
        for ev in ok:
            self.assertIsNone(check.problem(ev), ev)
        bad = [5, None, [], {"type": "task_add"}, {"type": "task_set", "id": ["T-1"]}, {"type": "agent_post"},
               {"type": "plan", "roles": "B"}, {"type": "task_set", "id": "T-1", "eta_min": True},
               {"type": "task_set", "id": "T-1", "est_cost": "3"}, {"type": "todo_sync", "todos": "x"},
               {"type": "todo_sync", "todos": [1]}, {"type": "todo_add", "item": []},
               {"type": "todo_update", "change": {"content": "c"}}]
        for ev in bad:
            self.assertIsNotNone(check.problem(ev), ev)

    def test_a_problem_is_reported_once_per_kind_not_on_every_poll(self):
        import board_event_check as check
        check._reported.clear()
        err = io.StringIO()
        with redirect_stderr(err):
            for _ in range(5):
                fold([{"type": "task_add"}])
            fold([{"type": "task_set"}])
        self.assertEqual(err.getvalue().count("skipping a malformed"), 2)      # task_add once, task_set once


class TheServerOverADamagedLog(unittest.TestCase):
    def test_state_stays_200_and_json(self):
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            root = base / "alpha"
            bdir = root / ".claude" / "board"
            pid = register(root, bdir, base / "reg.json")["id"]
            bdir.mkdir(parents=True, exist_ok=True)
            (bdir / EVENTS_FILE).write_bytes(random_lines(random.Random(3), 400))
            server = ThreadingHTTPServer(("127.0.0.1", 0), board_server.make_handler(base / "reg.json"))
            threading.Thread(target=server.serve_forever, daemon=True).start()
            self.addCleanup(server.server_close)
            self.addCleanup(server.shutdown)
            url = f"http://127.0.0.1:{server.server_address[1]}/api/state?p={pid}"
            with redirect_stderr(io.StringIO()), urllib.request.urlopen(url) as res:
                self.assertEqual(res.status, 200)
                self.assertIn("tasks", json.loads(res.read()))


HOOK_EVENTS = ["SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "Stop", "SubagentStart",
               "SubagentStop", "PreCompact", "Notification", None, 5]
TOOLS = ["Agent", "Bash", "TodoWrite", "TaskCreate", "TaskUpdate", "Read", None, 3]
PAYLOAD_VALUES = [None, "", "x", "s1-aaaa", 0, 7, True, [], ["a"], {}, {"command": "board.py set T-1 --status done"},
                  {"subagent_type": "dev", "description": "T-3 work"}, {"todos": [{"content": "c", "status": "pending"}]},
                  {"todos": "nope"}, {"task": {"id": "9"}}, "Task #4 created successfully", "x" * 3000]


def random_payload(rng: random.Random) -> dict:
    p = {"hook_event_name": rng.choice(HOOK_EVENTS), "tool_name": rng.choice(TOOLS)}
    for name in rng.sample(["session_id", "cwd", "tool_input", "tool_response", "tool_use_id", "agent_id",
                            "agent_type", "transcript_path", "prompt"], rng.randint(0, 8)):
        p[name] = rng.choice(PAYLOAD_VALUES)
    return p


class TheHook(unittest.TestCase):
    def run_main(self, text: str, bdir: Path):
        out, err = io.StringIO(), io.StringIO()
        with mock.patch.dict(os.environ, {"BOARD_DIR": str(bdir)}), mock.patch("sys.stdin", io.StringIO(text)), \
                mock.patch("sys.stdout", out), mock.patch("sys.stderr", err):
            code = board_hook.main()
        return code, out.getvalue(), err.getvalue()

    def test_random_payloads_never_break_a_session_or_the_log(self):
        for seed in SEEDS:
            rng = random.Random(seed)
            with tempfile.TemporaryDirectory() as tmp:
                bdir = Path(tmp) / "board"
                for _ in range(60):
                    code, out, _ = self.run_main(json.dumps(random_payload(rng)), bdir)
                    self.assertEqual(code, 0, f"seed {seed}")
                    if out.strip():
                        json.loads(out)               # what Claude Code reads must be JSON
                path = bdir / EVENTS_FILE
                if path.exists():
                    for line in path.read_text(encoding="utf-8").splitlines():
                        json.loads(line)              # nothing the hook wrote is torn

    def test_garbage_on_stdin_exits_zero_in_a_real_process(self):
        script = str(BOARD / "board_hook.py")
        for data in (b"", b"not json", b"\xff\xfe\x00", b"[1,2,3]", b"null", b'{"hook_event_name": ', b"5"):
            done = subprocess.run([sys.executable, script], input=data, capture_output=True,
                                  env={**os.environ, "BOARD_DIR": tempfile.mkdtemp()}, timeout=30)
            self.assertEqual(done.returncode, 0, data)
            self.assertEqual(done.stdout.strip(), b"", data)


if __name__ == "__main__":
    unittest.main()
