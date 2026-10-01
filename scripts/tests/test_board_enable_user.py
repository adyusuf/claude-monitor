"""`board.py enable --user` (hooks in ~/.claude/settings.json, so every repository and folder is listed
without a per-project step) and the local git exclude that keeps a hook-created board out of `git status`."""
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

BOARD = Path(__file__).resolve().parent.parent / "board"
sys.path.insert(0, str(BOARD))

import board_enable as be  # noqa: E402
import board_store  # noqa: E402

CLI = str(BOARD / "board.py")


def git(repo, *argv):
    return subprocess.run(["git", "-C", str(repo), *argv], capture_output=True, text=True, check=True).stdout


class EnableUserTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.home = Path(self.tmp.name).resolve() / "home"
        (self.home / ".claude").mkdir(parents=True)
        self.settings = self.home / ".claude" / "settings.json"

    def cli(self, *argv):
        env = {k: v for k, v in os.environ.items() if k != "BOARD_DIR"}
        env["HOME"] = str(self.home)
        env["BOARD_REGISTRY"] = str(self.home / "projects.json")
        return subprocess.run([sys.executable, CLI, *argv], cwd=self.home, capture_output=True, text=True, env=env)

    def test_a_missing_settings_file_is_created_with_every_hook(self):
        changes = be.enable_user(self.home)
        self.assertEqual(len(changes), 1)
        self.assertEqual(sorted(json.loads(self.settings.read_text())["hooks"]), sorted(be.HOOKS))
        self.assertEqual(list((self.home / ".claude").glob("backups/*")), [])  # nothing to back up

    def test_other_settings_and_other_hooks_are_kept_and_the_old_file_is_backed_up(self):
        other = {"permissions": {"allow": ["Bash(ls)"]},
                 "hooks": {"Stop": [{"hooks": [{"type": "command", "command": "bash other.sh"}]}]}}
        self.settings.write_text(json.dumps(other))
        be.enable_user(self.home)
        now = json.loads(self.settings.read_text())
        self.assertEqual(now["permissions"], other["permissions"])
        commands = [h["command"] for e in now["hooks"]["Stop"] for h in e["hooks"]]
        self.assertIn("bash other.sh", commands)
        self.assertEqual(len(commands), 2)
        backups = list((self.home / ".claude" / "backups").glob("settings.json.*.bak"))
        self.assertEqual(len(backups), 1)
        self.assertEqual(json.loads(backups[0].read_text()), other)

    def test_running_it_twice_changes_nothing(self):
        be.enable_user(self.home)
        before = self.settings.read_text()
        self.assertEqual(be.enable_user(self.home), [])
        self.assertEqual(self.settings.read_text(), before)
        self.assertEqual(list((self.home / ".claude").glob("backups/*")), [])

    def test_an_unparsable_file_is_never_touched(self):
        self.settings.write_text("{ not json")
        with self.assertRaises(ValueError):
            be.enable_user(self.home)
        self.assertEqual(self.settings.read_text(), "{ not json")

    def test_a_top_level_that_is_not_an_object_is_never_touched(self):
        self.settings.write_text("[1, 2]")
        with self.assertRaises(ValueError):
            be.enable_user(self.home)
        self.assertEqual(self.settings.read_text(), "[1, 2]")

    def test_the_command_writes_the_user_settings_and_says_so(self):
        out = self.cli("enable", "--user")
        self.assertEqual(out.returncode, 0, out.stderr)
        self.assertIn(str(self.settings), out.stdout)
        self.assertEqual(sorted(json.loads(self.settings.read_text())["hooks"]), sorted(be.HOOKS))
        again = self.cli("enable", "--user")
        self.assertIn("already", again.stdout)

    def test_the_command_refuses_a_broken_file_with_exit_2(self):
        self.settings.write_text("nope")
        out = self.cli("enable", "--user")
        self.assertEqual(out.returncode, 2)
        self.assertIn("not valid JSON", out.stderr)

    def test_the_project_block_and_the_user_block_use_identical_commands(self):
        # identical command strings are what makes Claude Code fire a hook once, not twice
        be.enable_user(self.home)
        user = json.loads(self.settings.read_text())["hooks"]
        self.assertEqual(user, be.hook_block()["hooks"])


class HideFromGitTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.repo = Path(self.tmp.name).resolve() / "proj"
        self.repo.mkdir()
        git(self.repo, "init", "-q")

    def test_a_board_created_by_a_hook_does_not_show_in_git_status(self):
        board_store.append_event(self.repo / ".claude" / "board", {"type": "plan", "mode": "B", "roles": []})
        self.assertEqual(git(self.repo, "status", "--porcelain"), "")
        self.assertIn(".claude/board/", (self.repo / ".git" / "info" / "exclude").read_text())

    def test_a_project_that_ignores_the_folder_already_gets_no_second_entry(self):
        (self.repo / ".gitignore").write_text(".claude/board/\n")
        board_store.append_event(self.repo / ".claude" / "board", {"type": "plan", "mode": "B", "roles": []})
        self.assertNotIn(".claude/board/", (self.repo / ".git" / "info" / "exclude").read_text())

    def test_an_existing_board_is_not_touched_again(self):
        bdir = self.repo / ".claude" / "board"
        board_store.append_event(bdir, {"type": "plan", "mode": "B", "roles": []})
        board_store.append_event(bdir, {"type": "plan", "mode": "B", "roles": []})
        exclude = (self.repo / ".git" / "info" / "exclude").read_text()
        self.assertEqual(exclude.count(".claude/board/"), 1)

    def test_a_folder_that_is_not_a_repository_is_fine(self):
        plain = Path(self.tmp.name) / "plain"
        plain.mkdir()
        board_store.append_event(plain / ".claude" / "board", {"type": "plan", "mode": "B", "roles": []})
        self.assertTrue((plain / ".claude" / "board" / "events.jsonl").exists())

    def test_a_board_dir_override_outside_our_layout_is_left_alone(self):
        board_store.append_event(self.repo / "elsewhere", {"type": "plan", "mode": "B", "roles": []})
        self.assertEqual(git(self.repo, "status", "--porcelain").strip(), "?? elsewhere/")

    def test_a_linked_worktree_writes_the_exclude_of_the_common_git_dir(self):
        git(self.repo, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "--allow-empty", "-m", "x")
        wt = Path(self.tmp.name) / "wt"
        git(self.repo, "worktree", "add", "-q", str(wt), "-b", "side")
        board_store.append_event(wt / ".claude" / "board", {"type": "plan", "mode": "B", "roles": []})
        self.assertEqual(git(wt, "status", "--porcelain"), "")


if __name__ == "__main__":
    unittest.main()
