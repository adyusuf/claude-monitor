"""Real processes, not threads: the two places where parallel sessions share a file or a lock.

  · the event log: every hook appends with ONE O_APPEND write, so concurrent sessions never interleave
    a line (board_store.append_event);
  · the restart lock: two sessions starting together must not stop each other's fresh server
    (board_ensure._restart_lock) - one at a time, and released even when the holder fails.

Task-id allocation under parallel sessions is covered in test_board_tasks.py.
"""
import json
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path

BOARD = Path(__file__).resolve().parent.parent / "board"
sys.path.insert(0, str(BOARD))

from board_config import EVENTS_FILE  # noqa: E402

PROCS, EACH, PAD = 8, 120, 3000

APPENDER = """
import sys
sys.path.insert(0, {board!r})
from pathlib import Path
from board_store import append_event
who, n, pad = int(sys.argv[2]), int(sys.argv[3]), int(sys.argv[4])
for i in range(n):
    append_event(Path(sys.argv[1]), {{"type": "probe", "who": who, "i": i, "pad": "x" * pad}})
"""

HOLDER = """
import sys, time
sys.path.insert(0, {board!r})
from pathlib import Path
from board_ensure import _restart_lock
log, tag = sys.argv[2], sys.argv[3]
with _restart_lock(Path(sys.argv[1])):
    with open(log, "a") as f: f.write("in " + tag + "\\n")
    time.sleep(0.4)
    with open(log, "a") as f: f.write("out " + tag + "\\n")
"""


def spawn(code, *args):
    return subprocess.Popen([sys.executable, "-c", code.format(board=str(BOARD)), *map(str, args)],
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)


class ParallelAppends(unittest.TestCase):
    def test_no_line_is_torn_lost_or_duplicated(self):
        with tempfile.TemporaryDirectory() as tmp:
            bdir = Path(tmp) / "board"
            bdir.mkdir()
            procs = [spawn(APPENDER, bdir, who, EACH, PAD) for who in range(PROCS)]
            for p in procs:
                _, err = p.communicate(timeout=120)
                self.assertEqual(p.returncode, 0, err)
            raw = (bdir / EVENTS_FILE).read_text(encoding="utf-8").split("\n")
            self.assertEqual(raw[-1], "")                       # the last line ends with a newline
            events = [json.loads(line) for line in raw[:-1]]    # strict: a torn line raises here
            self.assertEqual(len(events), PROCS * EACH)
            for who in range(PROCS):                            # nothing lost, in order per writer
                self.assertEqual([e["i"] for e in events if e["who"] == who], list(range(EACH)))
            self.assertTrue(all(len(e["pad"]) == PAD for e in events))

    def test_the_log_is_private(self):
        with tempfile.TemporaryDirectory() as tmp:
            bdir = Path(tmp) / "board"
            p = spawn(APPENDER, bdir, 0, 1, 1)
            p.communicate(timeout=30)
            self.assertEqual((bdir / EVENTS_FILE).stat().st_mode & 0o777, 0o600)


class RestartLock(unittest.TestCase):
    def test_holders_never_overlap(self):
        with tempfile.TemporaryDirectory() as tmp:
            log = Path(tmp) / "order.log"
            procs = [spawn(HOLDER, tmp, log, tag) for tag in "ABC"]
            for p in procs:
                _, err = p.communicate(timeout=60)
                self.assertEqual(p.returncode, 0, err)
            lines = log.read_text().split()
            kinds = lines[0::2]
            self.assertEqual(kinds, ["in", "out"] * 3, lines)       # in/out strictly alternate: no overlap
            self.assertEqual(lines[1::2][0::2], lines[1::2][1::2])  # each holder leaves before the next enters

    def test_the_lock_is_released_when_the_holder_fails(self):
        from board_ensure import _restart_lock
        with tempfile.TemporaryDirectory() as tmp:
            with self.assertRaises(RuntimeError):
                with _restart_lock(Path(tmp)):
                    raise RuntimeError("boom")
            started = time.monotonic()
            p = spawn(HOLDER, tmp, Path(tmp) / "after.log", "Z")     # would hang on a leaked lock
            _, err = p.communicate(timeout=30)
            self.assertEqual(p.returncode, 0, err)
            self.assertLess(time.monotonic() - started, 5)


if __name__ == "__main__":
    unittest.main()
