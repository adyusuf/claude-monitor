"""The machine-wide board registry and the SessionStart auto-start (docs/live-board.md §2).

The registry and the port are redirected to temporary ones in every test: a test must never
write to the user's real ~/.cache/claude-board/projects.json or start a server on 8765.
"""
import io
import json
import os
import socket
import sys
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "board"))

import board_ensure  # noqa: E402
import board_registry  # noqa: E402
from board_store import append_event  # noqa: E402


def free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


class Isolated(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.base = Path(self.tmp.name)
        self.registry = self.base / "registry" / "projects.json"
        self.port = free_port()
        env = {k: v for k, v in os.environ.items() if k not in ("BOARD_DIR", "CLAUDE_PROJECT_DIR")}
        env["BOARD_REGISTRY"] = str(self.registry)
        for patcher in (mock.patch.dict(os.environ, env, clear=True),
                        mock.patch.object(board_registry, "REGISTRY", self.registry),
                        mock.patch.object(board_ensure, "REGISTRY", self.registry),
                        mock.patch.object(board_ensure, "PORT", self.port)):
            patcher.start()
            self.addCleanup(patcher.stop)

    def project(self, name: str) -> Path:
        root = self.base / name
        root.mkdir()
        return root


class RegistryTests(Isolated):
    def test_register_is_idempotent_and_private(self):
        root = self.project("alpha")
        first = board_registry.register(root, root / ".claude" / "board")
        again = board_registry.register(root, root / ".claude" / "board")
        self.assertEqual(first["id"], again["id"])
        self.assertRegex(first["id"], r"^[0-9a-f]{10}$")
        self.assertEqual(list(board_registry.load()), [first["id"]])
        self.assertEqual(board_registry.load()[first["id"]]["name"], "alpha")
        self.assertEqual(self.registry.stat().st_mode & 0o777, 0o600)

    def test_parallel_sessions_do_not_drop_each_other(self):
        roots = [self.project(f"p{i}") for i in range(12)]
        threads = [threading.Thread(target=board_registry.register, args=(r, r / ".claude" / "board"))
                   for r in roots]
        for t in threads:
            t.start()
        for t in threads:
            t.join()
        self.assertEqual(len(board_registry.load()), 12)

    def test_missing_corrupt_or_foreign_registry_reads_as_empty(self):
        self.assertEqual(board_registry.load(), {})
        self.registry.parent.mkdir(parents=True)
        self.registry.write_text("{nope")
        with mock.patch.object(sys, "stderr", io.StringIO()) as err:
            self.assertEqual(board_registry.load(), {})
        self.assertIn("unreadable registry", err.getvalue())
        self.registry.write_text("[1, 2]")
        self.assertEqual(board_registry.load(), {})

    def test_summary_counts_the_projects_own_board(self):
        root = self.project("alpha")
        bdir = root / ".claude" / "board"
        entry = board_registry.register(root, bdir)
        for tid, status in (("T-1", "running"), ("T-2", "agent_done"), ("T-3", "done")):
            append_event(bdir, {"type": "task_set", "id": tid, "status": status, "session": "s"})
        s = board_registry.summary(entry)
        self.assertEqual((s["running"], s["waiting"], s["done"], s["total"]), (1, 1, 1, 3))
        self.assertTrue(s["turn_open"])


def fake_server(test: unittest.TestCase, port: int, routes: dict):
    """Something on the board's port that answers `routes` ({path: (status, json)}) and 404s the rest."""
    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            status, body = routes.get(self.path, (404, {"error": "no"}))
            data = json.dumps(body).encode()
            self.send_response(status)
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def log_message(self, *args):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", port), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    test.addCleanup(server.server_close)
    test.addCleanup(server.shutdown)


class EnsureTests(Isolated):
    def ensure(self, name="alpha", **kw):
        line, proc = board_ensure.ensure(str(self.project(name)), **kw)
        if proc is not None:
            self.addCleanup(proc.wait)
            self.addCleanup(proc.terminate)
        return line

    def test_nothing_running_starts_one_server_for_all_projects(self):
        line = self.ensure("alpha")
        self.assertIn("(started; project 'alpha'", line)
        self.assertEqual(board_ensure.probe(), board_ensure.Found.BOARD)
        self.assertIn("(running; project 'beta'", self.ensure("beta"))
        self.assertEqual(len(board_registry.load()), 2)

    def test_an_older_single_project_server_is_reported_not_replaced(self):
        fake_server(self, self.port, {"/api/state": (200, {"tasks": {}})})
        self.assertIn("OLDER board server", self.ensure())

    def test_an_info_endpoint_with_an_old_version_is_an_older_server(self):
        fake_server(self, self.port, {"/api/info": (200, {"version": 1})})
        self.assertIn("OLDER board server", self.ensure())

    def test_a_foreign_service_on_the_port_is_reported(self):
        fake_server(self, self.port, {})
        self.assertIn("something else holds", self.ensure())

    def test_a_server_that_never_comes_up_is_reported_with_its_log(self):
        with mock.patch.object(board_ensure, "start", return_value=None):
            line = self.ensure(wait=0.3)
        self.assertIn("did NOT come up", line)
        self.assertIn("server.log", line)

    def test_main_prints_one_line_and_never_fails_the_session(self):
        payload = json.dumps({"hook_event_name": "SessionStart", "cwd": str(self.project("alpha"))})
        with mock.patch.object(board_ensure, "ensure", return_value=("Live board: x", None)), \
                mock.patch.object(sys, "stdin", io.StringIO(payload)), \
                mock.patch.object(sys, "stdout", io.StringIO()) as out:
            self.assertEqual(board_ensure.main(), 0)
        self.assertEqual(out.getvalue().strip(), "Live board: x")
        with mock.patch.object(sys, "stdin", io.StringIO("not json")), \
                mock.patch.object(sys, "stderr", io.StringIO()) as err:
            self.assertEqual(board_ensure.main(), 0)
        self.assertIn("board ensure error", err.getvalue())


if __name__ == "__main__":
    unittest.main()
