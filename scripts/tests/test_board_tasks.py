"""Task ids across sessions (T-28): `board.py add` hands out the next free id under a lock,
refuses an id that already exists, and never creates a board in a repository that has none.

Seen live (30/09/2026): T-25 was typed by three sessions and T-26 by two, so a task silently
took over another's title; and `board.py set` run from another repository's working tree created a
stray .claude/board/ there."""
import atexit
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

BOARD = Path(__file__).resolve().parent.parent / "board"
sys.path.insert(0, str(BOARD))

from board_store import fold, read_events  # noqa: E402
from board_control import read_control  # noqa: E402

CLI = str(BOARD / "board.py")


# The CLI lists the project it writes to (T-36): never in the developer's real registry.
_REGISTRY_DIR = tempfile.mkdtemp(prefix="board-test-registry-")
atexit.register(shutil.rmtree, _REGISTRY_DIR, ignore_errors=True)


def run_cli(cwd, *argv, env_extra=None):
    env = {k: v for k, v in os.environ.items() if k != "BOARD_DIR"}
    env["BOARD_REGISTRY"] = os.path.join(_REGISTRY_DIR, "projects.json")
    env.update(env_extra or {})
    return subprocess.run([sys.executable, CLI, *argv], cwd=cwd, capture_output=True, text=True, env=env)


def tasks(bdir):
    return fold(read_events(bdir), read_control(bdir))["tasks"]


class TaskIds(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.bdir = Path(self.tmp.name)

    def add(self, *argv):
        return run_cli(self.tmp.name, *argv, env_extra={"BOARD_DIR": self.tmp.name})

    def test_auto_starts_at_t_1_and_prints_the_id(self):
        r = self.add("add", "auto", "first")
        self.assertEqual((r.returncode, r.stdout.strip()), (0, "T-1"))
        self.assertEqual(tasks(self.bdir)["T-1"]["title"], "first")

    def test_auto_takes_the_number_after_the_highest_one_seen(self):
        self.add("add", "T-5", "five")
        self.add("set", "T-9", "--status", "running")  # an id seen only through a task_set counts too
        self.assertEqual(self.add("add", "auto", "next").stdout.strip(), "T-10")

    def test_an_id_that_already_exists_is_refused_and_nothing_is_written(self):
        self.add("add", "T-25", "mine")
        before = len(read_events(self.bdir))

        r = self.add("add", "T-25", "someone else's task")

        self.assertEqual(r.returncode, 2)
        self.assertIn("T-25 already exists", r.stderr)
        self.assertIn("auto", r.stderr)  # says how to get a free id
        self.assertEqual(len(read_events(self.bdir)), before)
        self.assertEqual(tasks(self.bdir)["T-25"]["title"], "mine")  # not taken over

    def test_parallel_sessions_get_distinct_ids(self):
        procs = [subprocess.Popen([sys.executable, CLI, "add", "auto", f"task {i}"], cwd=self.tmp.name,
                                  stdout=subprocess.PIPE, text=True,
                                  env={**os.environ, "BOARD_DIR": self.tmp.name}) for i in range(12)]
        ids = [p.communicate()[0].strip() for p in procs]
        self.assertEqual(len(set(ids)), 12, ids)  # no two sessions share an id
        self.assertEqual(len(tasks(self.bdir)), 12)

    def test_a_malformed_id_is_still_rejected(self):
        self.assertEqual(self.add("add", "T-x", "bad").returncode, 2)


class ForeignRepository(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        subprocess.run(["git", "init", "-q", self.tmp.name], check=True)
        self.board = Path(self.tmp.name) / ".claude" / "board"

    def test_a_repository_without_a_board_is_refused_and_nothing_is_created(self):
        for argv in (("set", "T-1", "--status", "done"), ("add", "auto", "x"), ("plan", "--mode", "B")):
            r = run_cli(self.tmp.name, *argv)
            self.assertEqual(r.returncode, 2, argv)
            self.assertIn("no live board", r.stderr)
            self.assertIn("--init", r.stderr)
        self.assertFalse(self.board.exists())  # the stray-directory bug

    def test_init_creates_the_board_on_purpose(self):
        r = run_cli(self.tmp.name, "--init", "add", "auto", "first task")
        self.assertEqual((r.returncode, r.stdout.strip()), (0, "T-1"))
        self.assertTrue((self.board / "events.jsonl").exists())

    def test_an_existing_board_needs_no_flag(self):
        run_cli(self.tmp.name, "--init", "add", "auto", "first")
        self.assertEqual(run_cli(self.tmp.name, "add", "auto", "second").stdout.strip(), "T-2")

    def test_list_on_a_repository_without_a_board_is_empty_and_creates_nothing(self):
        r = run_cli(self.tmp.name, "list")
        self.assertEqual((r.returncode, r.stdout), (0, ""))
        self.assertFalse(self.board.exists())


if __name__ == "__main__":
    unittest.main()
