"""The live board server and where boards live (docs/live-board.md): one server
for every registered project, the --dir fallback, controls per project, and that every
worktree of a project resolves to one board. Split out of test_board.py (#9)."""
import json
import os
import socket
import subprocess
import sys
import tempfile
import threading
import unittest
import urllib.error
import urllib.request
from http.server import ThreadingHTTPServer
from pathlib import Path
from unittest import mock

BOARD = Path(__file__).resolve().parent.parent / "board"
sys.path.insert(0, str(BOARD))

import board_server  # noqa: E402
from board_config import board_dir, project_root  # noqa: E402
from board_registry import project_id, register  # noqa: E402
from board_store import append_event, read_control  # noqa: E402


def free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


class BoardLocationTests(unittest.TestCase):
    """Every worktree of a project must write to ONE board, or a session in a
    worktree and its agents would each see half of the work."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.main = Path(self.tmp.name) / "main"
        git = ["git", "-c", "user.email=t@t", "-c", "user.name=t"]
        subprocess.run(["git", "init", "-q", str(self.main)], check=True)
        subprocess.run(git + ["-C", str(self.main), "commit", "-q", "--allow-empty", "-m", "x"], check=True)
        self.wt = Path(self.tmp.name) / "wt"
        subprocess.run(["git", "-C", str(self.main), "worktree", "add", "-q", str(self.wt)], check=True)

    def test_a_worktree_resolves_to_the_main_checkout(self):
        self.assertEqual(project_root(str(self.wt)).resolve(), self.main.resolve())
        self.assertEqual(project_root(str(self.main / ".")).resolve(), self.main.resolve())

    def test_outside_a_repository_the_start_directory_is_used(self):
        loose = Path(self.tmp.name) / "loose"
        loose.mkdir()
        self.assertEqual(project_root(str(loose)), loose)

    def test_board_dir_prefers_BOARD_DIR_then_the_project_dir(self):
        with mock.patch.dict(os.environ, {"BOARD_DIR": "/x/y"}):
            self.assertEqual(board_dir(), Path("/x/y"))
        env = {k: v for k, v in os.environ.items() if k != "BOARD_DIR"}
        env["CLAUDE_PROJECT_DIR"] = str(self.wt)
        with mock.patch.dict(os.environ, env, clear=True):
            self.assertEqual(board_dir().resolve(), (self.main / ".claude" / "board").resolve())


class ValidationTests(unittest.TestCase):
    def test_control_bodies_are_validated(self):
        self.assertEqual(board_server.validate_control({"action": "remove_task", "value": "T-3"}),
                         ("remove_task", "T-3"))
        for bad in ({"action": "remove_task", "value": "T-3; rm"},
                    {"action": "disable_role", "value": "../x"},
                    {"action": "wipe", "value": "T-1"},
                    {"action": "enable_role", "value": 7},
                    {"action": "remove_task", "value": "T-1", "project": "../../etc"},
                    {"action": "remove_task", "value": "T-1", "project": 5}):
            with self.assertRaises(ValueError):
                board_server.validate_control(bad)


class ServerTests(unittest.TestCase):
    """Two registered projects on one server: each tab shows its own board."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        base = Path(self.tmp.name)
        self.registry = base / "registry" / "projects.json"
        self.a_root, self.b_root = base / "alpha", base / "beta"
        self.a, self.b = (r / ".claude" / "board" for r in (self.a_root, self.b_root))
        self.a_id = register(self.a_root, self.a, self.registry)["id"]
        self.b_id = register(self.b_root, self.b, self.registry)["id"]
        # An explicit older timestamp, so "most recent" is decided without sleeping.
        append_event(self.b, {"type": "task_add", "id": "T-1", "title": "old",
                              "ts": "2026-01-01T00:00:00+00:00"})
        append_event(self.a, {"type": "task_add", "id": "T-1", "title": "first"})
        append_event(self.a, {"type": "task_set", "id": "T-1", "status": "running"})
        self.base = self.serve(board_server.make_handler(self.registry))

    def serve(self, handler) -> str:
        server = ThreadingHTTPServer(("127.0.0.1", 0), handler)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        return f"http://127.0.0.1:{server.server_address[1]}"

    def request(self, path, body=None, headers=None, base=None):
        data = json.dumps(body).encode() if isinstance(body, dict) else body
        req = urllib.request.Request((base or self.base) + path, data=data, headers=headers or {})
        try:
            with urllib.request.urlopen(req) as res:
                return res.status, json.loads(res.read()) if path.startswith("/api") else res.read()
        except urllib.error.HTTPError as exc:
            return exc.code, exc.read()

    def test_serves_page_script_info_and_404(self):
        self.assertEqual(self.request("/")[0], 200)
        status, body = self.request("/board_ui.js")
        self.assertIn(b"projectsHtml", body)
        self.assertEqual(self.request("/api/info"), (200, {"version": 2}))
        self.assertEqual(self.request("/etc/passwd")[0], 404)

    def test_projects_lists_every_board_most_recent_first(self):
        status, items = self.request("/api/projects")
        self.assertEqual(status, 200)
        self.assertEqual([i["name"] for i in items], ["alpha", "beta"])
        self.assertEqual((items[0]["running"], items[0]["total"]), (1, 1))

    def test_state_is_per_project_and_defaults_to_the_most_recent(self):
        _, beta = self.request(f"/api/state?p={self.b_id}")
        self.assertEqual((beta["project"], beta["tasks"]["T-1"]["title"]), (self.b_id, "old"))
        _, default = self.request("/api/state")
        self.assertEqual(default["project"], self.a_id)
        self.assertEqual(self.request("/api/state?p=0123456789")[0], 404)

    def test_control_goes_to_the_named_project_only(self):
        json_hdr = {"Content-Type": "application/json"}
        status, _ = self.request("/api/control", {"action": "remove_task", "value": "T-1",
                                                  "project": self.b_id}, json_hdr)
        self.assertEqual(status, 200)
        self.assertEqual(read_control(self.b)["removed_tasks"], ["T-1"])
        self.assertEqual(read_control(self.a)["removed_tasks"], [])
        self.assertEqual(self.request("/api/control", {"action": "remove_task", "value": "T-1",
                                                       "project": "0123456789"}, json_hdr)[0], 404)

    def test_a_decision_is_posted_and_the_defaults_come_from_the_server(self):
        json_hdr = {"Content-Type": "application/json"}
        status, _ = self.request("/api/control", {"action": "decide", "value": "T-1", "choice": "continue",
                                                  "note": "go", "project": self.a_id}, json_hdr)
        self.assertEqual(status, 200)
        self.assertEqual(read_control(self.a)["decisions"]["T-1"]["choice"], "continue")
        self.assertEqual(self.request("/api/control", {"action": "decide", "value": "T-1",
                                                       "project": self.a_id}, json_hdr)[0], 400)
        _, state = self.request(f"/api/state?p={self.a_id}")
        self.assertEqual(state["decision_defaults"], ["continue", "reject"])

    def test_post_rejections(self):
        json_hdr = {"Content-Type": "application/json"}
        cases = [
            ("/api/other", {"action": "remove_task", "value": "T-1"}, json_hdr, 404),
            ("/api/control", {"action": "remove_task", "value": "T-1"},
             {**json_hdr, "Origin": "http://evil.example"}, 403),
            ("/api/control", {"action": "remove_task", "value": "T-1"}, {"Content-Type": "text/plain"}, 415),
            ("/api/control", {"action": "wipe", "value": "T-1"}, json_hdr, 400),
            ("/api/control", b"x" * 5000, json_hdr, 400),
        ]
        for path, body, headers, code in cases:
            with self.subTest(code=code):
                self.assertEqual(self.request(path, body, headers)[0], code)
        self.assertEqual(read_control(self.a)["version"], 0)

    def test_dir_flag_still_works_without_a_registry(self):
        # Backward compatibility (#4): a pre-v2 `--dir` start shows that one board.
        empty = Path(self.tmp.name) / "none.json"
        base = self.serve(board_server.make_handler(empty, self.a))
        _, state = self.request("/api/state", base=base)
        self.assertEqual(state["project"], project_id(self.a_root.resolve()))
        _, items = self.request("/api/projects", base=base)
        self.assertEqual([i["name"] for i in items], ["alpha"])

    def test_no_board_at_all_is_a_404_not_a_crash(self):
        base = self.serve(board_server.make_handler(Path(self.tmp.name) / "none.json"))
        self.assertEqual(self.request("/api/state", base=base)[0], 404)
        self.assertEqual(self.request("/api/control", {"action": "remove_task", "value": "T-1"},
                                      {"Content-Type": "application/json"}, base=base)[0], 404)

    def test_main_serves_on_the_given_port(self):
        port = free_port()
        env = dict(os.environ, BOARD_REGISTRY=str(self.registry))
        proc = subprocess.Popen([sys.executable, str(BOARD / "board_server.py"), "--port", str(port)],
                                env=env, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True)
        self.addCleanup(proc.wait)
        self.addCleanup(proc.terminate)
        self.assertIn(f"http://127.0.0.1:{port}", proc.stdout.readline())
        status, items = self.request("/api/projects", base=f"http://127.0.0.1:{port}")
        self.assertEqual((status, len(items)), (200, 2))


if __name__ == "__main__":
    unittest.main()
