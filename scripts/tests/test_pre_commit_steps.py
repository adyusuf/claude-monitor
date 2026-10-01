"""The pre-commit hook's four steps (scripts/pre-commit.sh).

The hook is the last thing between a mistake and the history, and three of its
four steps were untested. What matters here is not that a clean commit passes —
it is that each step STOPS the commit, and that a missing tool is announced
rather than silently treated as a pass.

One behaviour is easy to get wrong in both directions and is pinned twice: the
LIVE configuration repository (~/.claude) is exempt from steps 3 and 4, because
its scripts/ is a symlink into this repository so it "has" the checkers, while
its memory notes legitimately refer to other projects' files and names. Steps 1
and 2 still apply there — an exemption that swallowed the size gate or the secret
scan would be a hole, not an exemption.
"""
import os
import shutil
import stat
import subprocess
import tempfile
import unittest

SCRIPTS = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
HOOK = os.path.join(SCRIPTS, 'pre-commit.sh')


class Hook(unittest.TestCase):
    def setUp(self):
        self.home = tempfile.mkdtemp()
        self.root = os.path.join(self.home, 'project')
        os.makedirs(os.path.join(self.root, 'scripts'))
        subprocess.run(['git', 'init', '-q', self.root], check=True)
        subprocess.run(['git', 'config', 'user.email', 't@t'], cwd=self.root, check=True)
        subprocess.run(['git', 'config', 'user.name', 't'], cwd=self.root, check=True)
        self.bin = os.path.join(self.home, 'bin')
        os.makedirs(self.bin)

    def tearDown(self):
        shutil.rmtree(self.home, ignore_errors=True)

    def script(self, relative, body):
        path = os.path.join(self.root, relative)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, 'w', encoding='utf-8') as handle:
            handle.write(body)
        os.chmod(path, os.stat(path).st_mode | stat.S_IEXEC)
        return path

    def on_path(self, name, body):
        path = os.path.join(self.bin, name)
        with open(path, 'w', encoding='utf-8') as handle:
            handle.write(body)
        os.chmod(path, os.stat(path).st_mode | stat.S_IEXEC)

    def stage(self, name='file.txt', text='content\n'):
        path = os.path.join(self.root, name)
        with open(path, 'w', encoding='utf-8') as handle:
            handle.write(text)
        subprocess.run(['git', 'add', name], cwd=self.root, check=True)

    def run_hook(self, *args, home=None, without=None):
        env = dict(os.environ, HOME=home or self.home)
        env['PATH'] = self.bin + os.pathsep + env['PATH']
        if without:
            # Drop only the directories that hold these tools, keeping the rest of
            # PATH. Replacing PATH wholesale would also drop the coverage shim
            # (scripts/coverage-shell.sh), so the run would stop being measured —
            # the trap that made another test file report 0% while passing.
            entries = []
            for entry in env['PATH'].split(os.pathsep):
                if any(os.path.exists(os.path.join(entry, tool)) for tool in without):
                    continue
                entries.append(entry)
            env['PATH'] = os.pathsep.join(entries)
        result = subprocess.run(['bash', HOOK, *args], cwd=self.root,
                                capture_output=True, text=True, env=env)
        return result.stdout + result.stderr, result.returncode


class SizeBudgetStep(Hook):
    def test_an_exceeded_ceiling_stops_the_commit(self):
        self.script('scripts/md-size-gate.sh',
                    '#!/usr/bin/env bash\necho "CLAUDE.md  ✗ CEILING EXCEEDED (+3 KB)"\nexit 1\n')
        self.stage()
        out, code = self.run_hook()
        self.assertIn('commit STOPPED', out)
        self.assertIn('CLAUDE.md budget', out)
        self.assertEqual(1, code)

    def test_a_file_with_no_budget_stops_the_commit(self):
        self.script('scripts/md-size-gate.sh',
                    '#!/usr/bin/env bash\necho "web/CLAUDE.md  ✗ NO BUDGET"\nexit 1\n')
        self.stage()
        out, code = self.run_hook()
        self.assertIn('commit STOPPED', out)
        self.assertEqual(1, code)

    def test_a_passing_gate_does_not_stop_the_commit(self):
        self.script('scripts/md-size-gate.sh',
                    '#!/usr/bin/env bash\necho "✓ Every CLAUDE.md is within its budget."\nexit 0\n')
        self.on_path('gitleaks', '#!/bin/sh\nexit 0\n')
        self.stage()
        out, code = self.run_hook()
        self.assertEqual(0, code, out)


class SecretScanStep(Hook):
    def test_a_found_secret_stops_the_commit(self):
        self.on_path('gitleaks', '#!/bin/sh\nexit 1\n')
        self.stage()
        out, code = self.run_hook()
        self.assertIn('gitleaks found a secret', out)
        self.assertIn('ROTATE it first', out)
        self.assertEqual(1, code)

    def test_a_missing_gitleaks_is_ANNOUNCED_and_not_silently_passed(self):
        # "A gate that did not run is not a gate that passed" — the commit is
        # allowed through, but never quietly.
        out, code = self.run_hook(without=['gitleaks'])
        self.assertIn('DID NOT RUN', out)


class DocumentationStep(Hook):
    def test_drift_stops_the_commit(self):
        self.on_path('gitleaks', '#!/bin/sh\nexit 0\n')
        self.script('scripts/doc-check.py',
                    '#!/usr/bin/env python3\nimport sys\nprint("✗ README.md: stale count")\nsys.exit(1)\n')
        self.stage()
        out, code = self.run_hook()
        self.assertIn('documentation drift', out)
        self.assertEqual(1, code)


class RealNameStep(Hook):
    def test_a_real_project_name_stops_the_commit(self):
        self.on_path('gitleaks', '#!/bin/sh\nexit 0\n')
        self.script('scripts/real-name-check.sh',
                    '#!/usr/bin/env bash\necho "leak.md:1: a real name"\nexit 1\n')
        self.stage()
        out, code = self.run_hook()
        self.assertIn('a real project name', out)
        self.assertEqual(1, code)


class TheLiveConfigExemption(Hook):
    """~/.claude IS this repository through a symlink, so it "has" the checkers —
    but its notes legitimately name other projects' files."""

    def make_root_the_live_config(self):
        link = os.path.join(self.home, '.claude')
        os.symlink(self.root, link)

    def test_steps_3_and_4_are_skipped_in_the_live_config(self):
        self.on_path('gitleaks', '#!/bin/sh\nexit 0\n')
        self.script('scripts/doc-check.py', '#!/usr/bin/env python3\nimport sys\nsys.exit(1)\n')
        self.script('scripts/real-name-check.sh', '#!/usr/bin/env bash\nexit 1\n')
        self.make_root_the_live_config()
        self.stage()
        out, code = self.run_hook()
        self.assertEqual(0, code, f'the exemption did not apply:\n{out}')

    def test_but_the_size_gate_STILL_applies_there(self):
        # An exemption that swallowed step 1 would be a hole, not an exemption.
        self.script('scripts/md-size-gate.sh',
                    '#!/usr/bin/env bash\necho "✗ CEILING EXCEEDED (+1 KB)"\nexit 1\n')
        self.make_root_the_live_config()
        self.stage()
        out, code = self.run_hook()
        self.assertIn('commit STOPPED', out)
        self.assertEqual(1, code)

    def test_and_the_secret_scan_STILL_applies_there(self):
        self.on_path('gitleaks', '#!/bin/sh\nexit 1\n')
        self.make_root_the_live_config()
        self.stage()
        out, code = self.run_hook()
        self.assertIn('gitleaks found a secret', out)
        self.assertEqual(1, code)


class Install(Hook):
    def test_install_links_the_hooks_into_git(self):
        self.script('scripts/pre-commit.sh', '#!/usr/bin/env bash\nexit 0\n')
        self.script('scripts/commit-msg.sh', '#!/usr/bin/env bash\nexit 0\n')
        out, code = self.run_hook('--install')
        self.assertEqual(0, code, out)
        for hook in ('pre-commit', 'commit-msg'):
            path = os.path.join(self.root, '.git', 'hooks', hook)
            self.assertTrue(os.path.islink(path), f'{hook} was not linked')

    def test_install_without_a_commit_msg_script_still_links_pre_commit(self):
        self.script('scripts/pre-commit.sh', '#!/usr/bin/env bash\nexit 0\n')
        out, code = self.run_hook('--install')
        self.assertEqual(0, code, out)
        self.assertTrue(os.path.islink(os.path.join(self.root, '.git', 'hooks', 'pre-commit')))


class OutsideARepository(Hook):
    def test_it_passes_quietly(self):
        loose = tempfile.mkdtemp()
        try:
            result = subprocess.run(['bash', HOOK], cwd=loose, capture_output=True, text=True,
                                    env=dict(os.environ, HOME=self.home))
            self.assertEqual(0, result.returncode)
        finally:
            shutil.rmtree(loose, ignore_errors=True)


if __name__ == '__main__':
    unittest.main()
