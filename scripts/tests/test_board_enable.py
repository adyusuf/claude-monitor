"""`board.py enable` (T-36): hooks, .gitignore and listing in one idempotent command, and the hook block it
writes is the one docs/live-board.md documents."""
import json
import os
import re
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

BOARD = Path(__file__).resolve().parent.parent / "board"
DOC = Path(__file__).resolve().parents[2] / "docs" / "live-board.md"
sys.path.insert(0, str(BOARD))

import board_enable as be  # noqa: E402

CLI = str(BOARD / "board.py")


class EnableTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.repo = Path(self.tmp.name).resolve() / "proj"
        self.repo.mkdir()
        subprocess.run(["git", "init", "-q", str(self.repo)], check=True)
        self.registry = Path(self.tmp.name) / "projects.json"

    def cli(self, *argv):
        env = {k: v for k, v in os.environ.items() if k != "BOARD_DIR"}
        env["BOARD_REGISTRY"] = str(self.registry)
        return subprocess.run([sys.executable, CLI, *argv], cwd=self.repo, capture_output=True, text=True, env=env)

    def settings(self):
        return json.loads((self.repo / ".claude" / "settings.json").read_text())

    def test_the_block_matches_the_one_documented_in_the_design_doc(self):
        doc = DOC.read_text()
        block = re.search(r"```json\n(\{\n  \"hooks\": \{.*?\n\})\n```", doc, re.S).group(1)
        self.assertEqual(json.loads(block), be.hook_block())

    def test_a_fresh_repository_gets_settings_gitignore_and_a_listing(self):
        out = self.cli("enable")
        self.assertEqual(out.returncode, 0, out.stderr)
        self.assertEqual(sorted(self.settings()["hooks"]), sorted(be.HOOKS))
        self.assertIn(".claude/board/", (self.repo / ".gitignore").read_text().splitlines())
        self.assertEqual([e["root"] for e in json.loads(self.registry.read_text()).values()], [str(self.repo)])
        stop = self.settings()["hooks"]["Stop"][0]["hooks"][0]
        self.assertEqual(stop["timeout"], 900)

    def test_running_it_twice_changes_nothing_and_says_so(self):
        self.cli("enable")
        before = ((self.repo / ".claude" / "settings.json").read_text(), (self.repo / ".gitignore").read_text())
        out = self.cli("enable")
        self.assertEqual(out.returncode, 0)
        self.assertIn("already", out.stdout)
        self.assertEqual(((self.repo / ".claude" / "settings.json").read_text(), (self.repo / ".gitignore").read_text()), before)

    def test_existing_settings_and_hooks_are_kept(self):
        (self.repo / ".claude").mkdir()
        (self.repo / ".claude" / "settings.json").write_text(json.dumps({
            "permissions": {"allow": ["Bash(ls)"]},
            "hooks": {"Stop": [{"hooks": [{"type": "command", "command": "echo mine"}]}]},
        }))
        (self.repo / ".gitignore").write_text("node_modules")  # no trailing newline
        self.assertEqual(self.cli("enable").returncode, 0)
        s = self.settings()
        self.assertEqual(s["permissions"], {"allow": ["Bash(ls)"]})
        stop = s["hooks"]["Stop"]
        self.assertEqual(stop[0]["hooks"][0]["command"], "echo mine")
        self.assertIn("board_hook.py", stop[1]["hooks"][0]["command"])
        self.assertEqual((self.repo / ".gitignore").read_text(), "node_modules\n.claude/board/\n")

    def test_a_partly_wired_repository_gets_only_the_missing_hooks(self):
        (self.repo / ".claude").mkdir()
        (self.repo / ".claude" / "settings.json").write_text(json.dumps(
            {"hooks": {"Stop": be.hook_block()["hooks"]["Stop"]}}))
        out = self.cli("enable")
        added = [e.strip() for e in out.stdout.split("added hooks")[1].split("\n")[0].split(",")]
        self.assertNotIn("Stop", added)  # exact names: "SubagentStop" is another hook
        self.assertIn("SubagentStop", added)
        self.assertEqual(len(self.settings()["hooks"]["Stop"]), 1)  # not doubled
        self.assertEqual(len(self.settings()["hooks"]["SessionStart"]), 1)

    def test_a_settings_file_that_is_not_json_is_left_alone(self):
        (self.repo / ".claude").mkdir()
        bad = self.repo / ".claude" / "settings.json"
        bad.write_text("{ not json")
        out = self.cli("enable")
        self.assertEqual(out.returncode, 2)
        self.assertEqual(bad.read_text(), "{ not json")
        self.assertFalse(self.registry.exists())

    def test_wrong_shapes_are_refused_not_overwritten(self):
        for text in ("[]", '{"hooks": []}', '{"hooks": {"Stop": {}}}'):
            (self.repo / ".claude").mkdir(exist_ok=True)
            path = self.repo / ".claude" / "settings.json"
            path.write_text(text)
            self.assertEqual(self.cli("enable").returncode, 2, text)
            self.assertEqual(path.read_text(), text)


class LinkedWorktreeTests(unittest.TestCase):
    """Run from a linked worktree, `enable` writes THAT worktree's files (they get committed there) and
    lists the main checkout's board; it must not touch the main checkout's tree."""

    def test_files_go_to_the_worktree_and_the_listing_to_the_main_checkout(self):
        with tempfile.TemporaryDirectory() as tmp:
            tmp = Path(tmp).resolve()
            main, wt, registry = tmp / "main", tmp / "wt", tmp / "projects.json"
            main.mkdir()
            git = ["git", "-c", "user.name=t", "-c", "user.email=t@t"]
            subprocess.run([*git, "init", "-q", str(main)], check=True)
            subprocess.run([*git, "-C", str(main), "commit", "-q", "--allow-empty", "-m", "i"], check=True)
            subprocess.run([*git, "-C", str(main), "worktree", "add", "-q", "-b", "b", str(wt)], check=True)
            env = {k: v for k, v in os.environ.items() if k != "BOARD_DIR"}
            env["BOARD_REGISTRY"] = str(registry)
            out = subprocess.run([sys.executable, CLI, "enable"], cwd=wt, capture_output=True, text=True, env=env)
            self.assertEqual(out.returncode, 0, out.stderr)
            self.assertTrue((wt / ".claude" / "settings.json").exists())
            self.assertTrue((wt / ".gitignore").exists())
            self.assertFalse((main / ".claude" / "settings.json").exists())
            self.assertFalse((main / ".gitignore").exists())
            self.assertEqual([e["root"] for e in json.loads(registry.read_text()).values()], [str(main)])


if __name__ == "__main__":
    unittest.main()
