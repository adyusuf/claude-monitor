"""The pre-commit hook's five steps (scripts/pre-commit.sh).

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
        # A feature branch: on dev/test/prod/main/master step 0 would stop every case before the step
        # it is about (and `git init` names the branch after the machine's init.defaultBranch).
        subprocess.run(['git', 'init', '-q', '-b', 'feature/x', self.root], check=True)
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
            # On Windows the tool is `gitleaks.exe`: PATHEXT names the suffixes to
            # look for too (unset elsewhere, so only the bare name is checked).
            exts = [''] + [e for e in os.environ.get('PATHEXT', '').split(os.pathsep) if e]
            entries = []
            for entry in env['PATH'].split(os.pathsep):
                if any(os.path.exists(os.path.join(entry, tool + ext))
                       for tool in without for ext in exts):
                    continue
                entries.append(entry)
            env['PATH'] = os.pathsep.join(entries)
        result = subprocess.run(['bash', HOOK, *args], cwd=self.root,
                                capture_output=True, text=True, env=env)
        return result.stdout + result.stderr, result.returncode


class ProtectedBranchStep(Hook):
    """Step 0: no commit directly on dev/test/prod/main/master (rule #26). Moved here from the one
    project that had added it (29/10/2026) so every copy carries it."""

    def on_branch(self, name):
        self.stage()
        subprocess.run(['git', 'commit', '-q', '--no-verify', '--allow-empty', '-m', 'first'], cwd=self.root, check=True)
        subprocess.run(['git', 'checkout', '-q', '-B', name], cwd=self.root, check=True)
        self.on_path('gitleaks', '#!/bin/sh\nexit 0\n')

    def test_a_commit_on_each_protected_branch_is_stopped(self):
        for name in ('dev', 'test', 'prod', 'main', 'master'):
            self.on_branch(name)
            out, code = self.run_hook()
            self.assertEqual(1, code, name)
            self.assertIn(f"'{name}' is a protected branch", out)
            self.assertIn('git branch --show-current', out)

    def test_it_stops_before_the_other_steps_run(self):
        self.script('scripts/md-size-gate.sh', '#!/usr/bin/env bash\necho SIZE_GATE_RAN\nexit 0\n')
        self.on_branch('dev')
        out, code = self.run_hook()
        self.assertEqual(1, code)
        self.assertNotIn('SIZE_GATE_RAN', out)

    def test_a_feature_branch_is_not_stopped(self):
        self.on_branch('feature/y')
        out, code = self.run_hook()
        self.assertEqual(0, code, out)

    def test_a_protected_name_inside_a_branch_name_is_not_stopped(self):
        for name in ('fix/dev-docs', 'devops', 'prod-hotfix'):
            self.on_branch(name)
            out, code = self.run_hook()
            self.assertEqual(0, code, f'{name}: {out}')

    def test_a_merge_in_progress_may_be_committed_on_dev(self):
        self.on_branch('dev')
        head = subprocess.run(['git', 'rev-parse', 'HEAD'], cwd=self.root, check=True,
                              capture_output=True, text=True).stdout.strip()
        with open(os.path.join(self.root, '.git', 'MERGE_HEAD'), 'w', encoding='utf-8') as handle:
            handle.write(head + '\n')
        out, code = self.run_hook()
        self.assertEqual(0, code, out)

    def test_the_deliberate_override_is_honoured_only_as_1(self):
        self.on_branch('dev')
        os.environ['ALLOW_PROTECTED_COMMIT'] = '1'
        try:
            out, code = self.run_hook()
            self.assertEqual(0, code, out)
            os.environ['ALLOW_PROTECTED_COMMIT'] = 'yes'
            out, code = self.run_hook()
            self.assertEqual(1, code, out)
        finally:
            os.environ.pop('ALLOW_PROTECTED_COMMIT', None)

    def test_a_detached_head_is_not_stopped(self):
        self.on_branch('dev')
        subprocess.run(['git', 'checkout', '-q', '--detach'], cwd=self.root, check=True)
        out, code = self.run_hook()
        self.assertEqual(0, code, out)


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

    def test_step_0_does_not_stop_its_own_commits_on_dev(self):
        # ~/.claude commits its memory and settings directly on dev; step 0 must not stop them there,
        # while the same branch in any other repository is still stopped (ProtectedBranchStep).
        self.on_path('gitleaks', '#!/bin/sh\nexit 0\n')
        self.stage()
        subprocess.run(['git', 'commit', '-q', '--no-verify', '-m', 'first'], cwd=self.root, check=True)
        subprocess.run(['git', 'checkout', '-q', '-B', 'dev'], cwd=self.root, check=True)
        out, code = self.run_hook()
        self.assertEqual(1, code, 'precondition: dev is stopped outside the live config')
        self.make_root_the_live_config()
        out, code = self.run_hook()
        self.assertEqual(0, code, out)

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
