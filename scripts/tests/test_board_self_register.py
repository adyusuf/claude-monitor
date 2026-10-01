"""A board that is written is listed on the live page (T-36): the CLI and the hook register the project
themselves, so a repository whose SessionStart hook is missing is not invisible.

Seen live (30/09/2026): ryan had a board with ten tasks and no `.claude/settings.json`, so
board_ensure.py never ran, the registry never got the project, and the page did not show it."""
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

BOARD = Path(__file__).resolve().parent.parent / "board"
sys.path.insert(0, str(BOARD))

import board_hook  # noqa: E402
import board_registry as reg  # noqa: E402

CLI = str(BOARD / "board.py")


class SelfRegisterTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.repo = Path(self.tmp.name).resolve() / "proj"
        self.repo.mkdir()
        subprocess.run(["git", "init", "-q", str(self.repo)], check=True)
        self.registry = Path(self.tmp.name) / "projects.json"

    def cli(self, *argv, board_dir=None):
        env = {k: v for k, v in os.environ.items() if k != "BOARD_DIR"}
        env["BOARD_REGISTRY"] = str(self.registry)
        if board_dir:
            env["BOARD_DIR"] = board_dir
        return subprocess.run([sys.executable, CLI, *argv], cwd=self.repo, capture_output=True, text=True, env=env)

    def listed(self):
        return json.loads(self.registry.read_text()) if self.registry.exists() else {}

    def test_a_cli_write_lists_the_project(self):
        self.assertEqual(self.cli("--init", "add", "T-1", "first").returncode, 0)
        entries = list(self.listed().values())
        self.assertEqual([(e["name"], e["root"]) for e in entries], [("proj", str(self.repo))])
        self.assertEqual(entries[0]["dir"], str(self.repo / ".claude" / "board"))

    def test_registering_twice_keeps_one_entry_and_does_not_rewrite_it(self):
        self.cli("--init", "add", "T-1", "first")
        before = self.registry.read_text()
        self.assertEqual(self.cli("add", "T-2", "second").returncode, 0)
        self.assertEqual(self.registry.read_text(), before)  # same entry, same timestamp: not rewritten

    def test_a_refused_write_does_not_list_anything(self):
        self.assertEqual(self.cli("add", "T-1", "no board here").returncode, 2)  # no --init: refused
        self.assertEqual(self.listed(), {})

    def test_list_does_not_register(self):
        self.cli("list")
        self.assertEqual(self.listed(), {})

    def test_board_dir_override_is_not_a_project(self):
        target = Path(self.tmp.name) / "elsewhere"
        target.mkdir()
        self.assertEqual(self.cli("add", "T-1", "x", board_dir=str(target)).returncode, 0)
        self.assertEqual(self.listed(), {})

    def test_a_hook_event_lists_a_project_that_has_no_session_start_hook(self):
        with mock.patch.object(reg, "REGISTRY", self.registry), \
             mock.patch.dict(os.environ, {k: v for k, v in os.environ.items() if k != "BOARD_DIR"}, clear=True):
            board_hook.handle({"hook_event_name": "UserPromptSubmit", "session_id": "s1", "cwd": str(self.repo)})
        self.assertEqual([e["root"] for e in self.listed().values()], [str(self.repo)])

    def test_register_if_missing_reports_whether_it_added(self):
        bdir = self.repo / ".claude" / "board"
        self.assertTrue(reg.register_if_missing(self.repo, bdir, self.registry))
        self.assertFalse(reg.register_if_missing(self.repo, bdir, self.registry))


if __name__ == "__main__":
    unittest.main()
