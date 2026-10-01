"""The home directory and the filesystem root are never a board (board_config.boardable): a session
started in ~ would otherwise put its board in ~/.claude, the live configuration."""
import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

BOARD = Path(__file__).resolve().parent.parent / "board"
sys.path.insert(0, str(BOARD))

import board_config  # noqa: E402
import board_ensure  # noqa: E402
import board_hook  # noqa: E402
import board_registry  # noqa: E402


class BoardableTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.home = Path(self.tmp.name).resolve() / "home"
        self.home.mkdir()
        env = {k: v for k, v in os.environ.items() if k not in ("BOARD_DIR", "CLAUDE_PROJECT_DIR")}
        env["HOME"] = str(self.home)
        self.registry = Path(self.tmp.name) / "projects.json"
        # REGISTRY is read from the environment at import time, so the module constants are patched:
        # a test that forgot this once wrote a temporary project into the real registry.
        for target in (board_registry, board_ensure):
            patcher = mock.patch.object(target, "REGISTRY", self.registry)
            patcher.start()
            self.addCleanup(patcher.stop)
        patcher = mock.patch.dict(os.environ, env, clear=True)
        patcher.start()
        self.addCleanup(patcher.stop)

    def test_home_and_root_are_not_boardable(self):
        self.assertFalse(board_config.boardable(self.home))
        self.assertFalse(board_config.boardable(Path("/")))

    def test_a_project_folder_is_boardable(self):
        project = self.home / "work" / "app"
        project.mkdir(parents=True)
        self.assertTrue(board_config.boardable(project))

    def test_a_hook_fired_in_home_writes_nothing_and_lists_nothing(self):
        payload = {"hook_event_name": "UserPromptSubmit", "session_id": "s1", "cwd": str(self.home),
                   "prompt": "hello"}
        self.assertIsNone(board_hook.handle(payload))
        self.assertFalse((self.home / ".claude").exists())
        self.assertFalse(self.registry.exists())

    def test_the_session_start_hook_in_home_starts_no_server_and_says_nothing(self):
        with mock.patch.object(board_ensure, "probe", side_effect=AssertionError("must not probe")):
            line, proc = board_ensure.ensure(str(self.home))
        self.assertEqual((line, proc), ("", None))
        self.assertFalse(self.registry.exists())

    def test_a_hook_fired_in_a_project_folder_still_lists_it(self):
        project = self.home / "work" / "app"
        project.mkdir(parents=True)
        payload = {"hook_event_name": "UserPromptSubmit", "session_id": "s1", "cwd": str(project),
                   "prompt": "hello"}
        board_hook.handle(payload)
        self.assertTrue(self.registry.exists())


if __name__ == "__main__":
    unittest.main()
