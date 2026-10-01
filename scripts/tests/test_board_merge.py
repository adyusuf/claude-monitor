"""The board's Merge column (docs/live-board.md): where a task's commits have landed,
read from git. Built on a real repository whose origin/* refs are set by hand, so the test
controls exactly which commit each branch holds."""
import io
import json
import os
import subprocess
import sys
import tempfile
import threading
import unittest
import urllib.request
from http.server import ThreadingHTTPServer
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "board"))

import board  # noqa: E402
import board_merge  # noqa: E402
import board_server  # noqa: E402
from board_registry import register  # noqa: E402
from board_store import fold, read_control, read_events  # noqa: E402

GIT = ["git", "-c", "user.email=t@t", "-c", "user.name=t"]


class Repo(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = str(Path(self.tmp.name) / "proj")
        subprocess.run(["git", "init", "-q", self.root], check=True)
        self.c1, self.c2 = self.commit("one"), self.commit("two")
        self.ref("dev", self.c2)
        self.ref("test", self.c1)   # test is one commit behind; prod does not exist
        board_merge._tips.clear()
        board_merge._contains.cache_clear()

    def commit(self, msg):
        subprocess.run(GIT + ["-C", self.root, "commit", "-q", "--allow-empty", "-m", msg], check=True)
        return subprocess.run(["git", "-C", self.root, "rev-parse", "HEAD"], capture_output=True,
                              text=True, check=True).stdout.strip()

    def ref(self, branch, sha):
        subprocess.run(["git", "-C", self.root, "update-ref", f"refs/remotes/origin/{branch}", sha], check=True)


class MergeState(Repo):
    def test_each_branch_is_reported_and_a_missing_branch_is_none(self):
        self.assertEqual(board_merge.merged(self.root, [self.c1]), {"dev": True, "test": True, "prod": None})
        self.assertEqual(board_merge.merged(self.root, [self.c2]), {"dev": True, "test": False, "prod": None})

    def test_every_commit_of_a_task_must_be_in_the_branch(self):
        self.assertEqual(board_merge.merged(self.root, [self.c1, self.c2])["test"], False)
        self.assertEqual(board_merge.merged(self.root, [self.c1, self.c2])["dev"], True)

    def test_an_unknown_commit_counts_as_not_merged_and_no_commits_is_empty(self):
        self.assertEqual(board_merge.merged(self.root, ["deadbeef"])["dev"], False)
        self.assertEqual(board_merge.merged(self.root, []), {})

    def test_outside_a_repository_every_branch_is_none(self):
        loose = str(Path(self.tmp.name) / "loose")
        os.mkdir(loose)
        self.assertEqual(board_merge.merged(loose, [self.c1]), {"dev": None, "test": None, "prod": None})

    def test_a_moved_branch_is_seen_once_the_tip_cache_expires(self):
        with mock.patch.object(board_merge.time, "monotonic", return_value=100.0):
            self.assertFalse(board_merge.merged(self.root, [self.c2])["test"])
        self.ref("test", self.c2)
        with mock.patch.object(board_merge.time, "monotonic", return_value=105.0):
            self.assertFalse(board_merge.merged(self.root, [self.c2])["test"])   # still cached
        with mock.patch.object(board_merge.time, "monotonic", return_value=111.0):
            self.assertTrue(board_merge.merged(self.root, [self.c2])["test"])    # re-read after the TTL


class CliAndServer(Repo):
    def setUp(self):
        super().setUp()
        self.bdir = Path(self.root) / ".claude" / "board"
        patcher = mock.patch.dict(os.environ, {"BOARD_DIR": str(self.bdir)})
        patcher.start()
        self.addCleanup(patcher.stop)

    def test_the_cli_stores_commits_and_refuses_non_hex(self):
        board.run(["add", "T-1", "a", "--commit", f"{self.c1[:7]},{self.c2}"])
        board.run(["set", "T-2", "--commit", self.c1.upper()])
        tasks = fold(read_events(self.bdir), read_control(self.bdir))["tasks"]
        self.assertEqual(tasks["T-1"]["commits"], [self.c1[:7], self.c2])
        self.assertEqual(tasks["T-2"]["commits"], [self.c1])
        with mock.patch.object(sys, "stderr", io.StringIO()):
            for bad in ("xyz1234", "abc", ","):
                with self.assertRaises(SystemExit):
                    board.run(["set", "T-3", "--commit", bad])

    def test_the_server_puts_the_merge_state_on_each_task(self):
        board.run(["add", "T-1", "a", "--commit", self.c2])
        board.run(["add", "T-2", "b"])
        registry = Path(self.tmp.name) / "projects.json"
        pid = register(Path(self.root), self.bdir, registry)["id"]
        server = ThreadingHTTPServer(("127.0.0.1", 0), board_server.make_handler(registry))
        threading.Thread(target=server.serve_forever, daemon=True).start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        url = f"http://127.0.0.1:{server.server_address[1]}/api/state?p={pid}"
        tasks = json.load(urllib.request.urlopen(url))["tasks"]
        self.assertEqual(tasks["T-1"]["merged"], {"dev": True, "test": False, "prod": None})
        self.assertEqual(tasks["T-2"]["merged"], {})


if __name__ == "__main__":
    unittest.main()
