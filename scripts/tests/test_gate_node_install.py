"""The gate installs a Node project's dependencies once, before its first Node step (gate-lib-node.sh).

A fresh worktree has no node_modules; lint / tsc / build / test then failed with "command not found", a
false red, because the install used to live in the coverage step that runs after them. What matters:
the install runs BEFORE the first step and only when node_modules is missing, a failed install is a
failed step (never a skip or a pass), and `--list` runs nothing.

npm and npx are fakes on PATH that log their arguments; no real package is installed.
"""
import os
import re
import shutil
import subprocess
import tempfile
import unittest

SCRIPTS = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
GATE_FILES = ('gate-core.sh', 'gate-lib.sh', 'gate-lib-node.sh', 'gate-lib-prod.sh', 'merge-gate.sh')
PACKAGE = '{"scripts": {"lint": "eslint .", "build": "vite build", "test": "vitest"}}'

FAKE_NPM = """#!/bin/sh
echo "npm $*" >> "$FAKE_LOG"
case "$*" in
  *" ci"*)
    if [ -n "$FAKE_CI_FAIL" ]; then echo "npm error code ENOTFOUND" >&2; exit 1; fi
    dir="$2"; mkdir -p "$dir/node_modules/.bin" && : > "$dir/node_modules/.bin/eslint" ;;
esac
exit 0
"""
FAKE_PNPM = """#!/bin/sh
echo "pnpm $*" >> "$FAKE_LOG"
case "$*" in
  *install*) dir="$2"; mkdir -p "$dir/node_modules/.bin" && : > "$dir/node_modules/.bin/eslint" ;;
esac
exit 0
"""
FAKE_NPX = """#!/bin/sh
echo "npx $*" >> "$FAKE_LOG"
exit 0
"""


class NodeInstall(unittest.TestCase):
    def setUp(self):
        self.home = tempfile.mkdtemp()
        self.root = os.path.join(self.home, 'project')
        os.makedirs(os.path.join(self.root, 'scripts'))
        os.makedirs(os.path.join(self.root, 'web'))
        for name in GATE_FILES:
            shutil.copy(os.path.join(SCRIPTS, name), os.path.join(self.root, 'scripts', name))
        self.write('web/package.json', PACKAGE)
        subprocess.run(['git', 'init', '-q', self.root], check=True)
        self.bin = os.path.join(self.home, 'bin')
        os.makedirs(self.bin)
        self.fake('npm', FAKE_NPM)
        self.fake('npx', FAKE_NPX)
        self.log = os.path.join(self.home, 'calls.log')

    def tearDown(self):
        shutil.rmtree(self.home, ignore_errors=True)

    def write(self, relative, body):
        path = os.path.join(self.root, relative)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, 'w') as f:
            f.write(body)

    def fake(self, name, body):
        path = os.path.join(self.bin, name)
        with open(path, 'w') as f:
            f.write(body)
        os.chmod(path, 0o700)

    def gate(self, *args, ci_fail=False, script='gate-core.sh', env_extra=None):
        env = {'PATH': self.bin + ':/usr/bin:/bin', 'FAKE_LOG': self.log, 'HOME': self.home, **(env_extra or {})}
        if ci_fail:
            env['FAKE_CI_FAIL'] = '1'
        done = subprocess.run(['bash', 'scripts/' + script, 'dev', *args], cwd=self.root, env=env,
                              capture_output=True, text=True, timeout=120)
        calls = []
        if os.path.exists(self.log):
            with open(self.log) as f:
                calls = f.read().splitlines()
        done.stdout = re.sub(r'\x1b\[[0-9;]*m', '', done.stdout)   # the gate colours its marks
        return done, calls

    def test_installs_once_before_the_first_node_step(self):
        done, calls = self.gate()
        self.assertEqual([c for c in calls if ' ci' in c], ['npm --prefix web ci'], done.stdout)
        self.assertEqual(calls[0], 'npm --prefix web ci')
        self.assertIn('npm --prefix web run lint', calls)
        self.assertIn('✓ install dependencies (web)', done.stdout)
        self.assertNotIn('✗ lint (web)', done.stdout)

    def test_no_install_when_node_modules_exists(self):
        os.makedirs(os.path.join(self.root, 'web', 'node_modules', '.bin'))
        done, calls = self.gate()
        self.assertEqual([c for c in calls if ' ci' in c], [], done.stdout)
        self.assertNotIn('install dependencies', done.stdout)

    def test_failed_install_is_a_failed_step_with_the_npm_log(self):
        done, calls = self.gate(ci_fail=True)
        self.assertIn('✗ install dependencies (web)', done.stdout)
        self.assertIn('npm error code ENOTFOUND', done.stdout)
        self.assertNotIn('SKIPPED: install dependencies', done.stdout)
        self.assertNotEqual(done.returncode, 0)
        self.assertEqual([c for c in calls if ' ci' in c], ['npm --prefix web ci'])

    def test_list_runs_nothing(self):
        done, calls = self.gate('--list')
        self.assertEqual(calls, [])
        self.assertIn('install dependencies (web)', done.stdout)
        self.assertIn('npm --prefix web ci', done.stdout)
        self.assertFalse(os.path.exists(os.path.join(self.root, 'web', 'node_modules')))

    def test_mobile_project_is_installed_too(self):
        self.write('mobile/package.json', PACKAGE)
        done, calls = self.gate()
        self.assertEqual([c for c in calls if ' ci' in c], ['npm --prefix web ci', 'npm --prefix mobile ci'], done.stdout)
        self.assertLess(calls.index('npm --prefix mobile ci'), calls.index('npm --prefix web run lint'))

    def test_pnpm_project_installs_with_the_frozen_lockfile(self):
        self.fake('pnpm', FAKE_PNPM)
        self.write('web/pnpm-lock.yaml', 'lockfileVersion: 9\n')
        done, calls = self.gate()
        self.assertEqual(calls[0], 'pnpm --dir web install --frozen-lockfile', done.stdout)
        self.assertNotIn('npm --prefix web ci', calls)

    def test_pnpm_project_without_pnpm_is_a_failed_step(self):
        self.write('web/pnpm-lock.yaml', 'lockfileVersion: 9\n')
        done, calls = self.gate()
        self.assertIn('✗ install dependencies (web): pnpm is missing', done.stdout)
        self.assertNotEqual(done.returncode, 0)

    def test_parallel_node_track_installs_before_its_first_step(self):
        self.write('Api/Api.csproj', '<Project/>')   # the track only runs beside .NET steps
        done, calls = self.gate(env_extra={'GATE_PARALLEL_NODE': '1'})
        self.assertEqual([c for c in calls if ' ci' in c], ['npm --prefix web ci'], done.stdout)
        self.assertEqual(calls[0], 'npm --prefix web ci')
        self.assertIn('✓ install dependencies (web)', done.stdout)

    def test_root_pnpm_lock_selects_pnpm(self):
        self.fake('pnpm', FAKE_PNPM)
        self.write('pnpm-lock.yaml', 'lockfileVersion: 9\n')   # a workspace lock at the root, web/ has none
        done, calls = self.gate()
        self.assertEqual(calls[0], 'pnpm --dir web install --frozen-lockfile', done.stdout)

    def test_missing_npm_is_a_failed_step_not_a_skip(self):
        if shutil.which('npm', path='/usr/bin:/bin'):
            self.skipTest('a system npm in /usr/bin hides the "npm is missing" case')
        os.remove(os.path.join(self.bin, 'npm'))
        done, _ = self.gate()
        self.assertIn('✗ install dependencies (web): npm is missing', done.stdout)
        self.assertNotIn('SKIPPED: install dependencies', done.stdout)
        self.assertNotEqual(done.returncode, 0)

    def make_listing_green(self):
        """Give the fixture every non-Node step, so that a listing ends GATE GREEN (exit 0) like the real repository."""
        self.write('scripts/merge-gate.conf', 'SECRET_CMD=true\nCOVERAGE_CMD=true\nSAST_CMD=true\nBACKCOMPAT_CMD=true\n')
        self.write('scripts/md-size-gate.sh', '#!/bin/sh\n')
        os.chmod(os.path.join(self.root, 'scripts', 'md-size-gate.sh'), 0o700)
        self.write('scripts/md-rule-gate.py', '')
        self.write('SETUP.md', '## Secret inventory\n')
        self.write('.env.example', '')
        self.write('web/tsconfig.json', '{}')   # else the typecheck step is skipped
        self.write('web/e2e/.keep', '')         # else the e2e spec check is skipped
        # The project's own step: if a listing reaches it, it leaves a mark in the call log.
        self.write('scripts/agent_smoke.py', 'import os\nopen(os.environ["FAKE_LOG"], "a").write("agent_smoke ran\\n")\n')

    def test_merge_gate_list_runs_only_the_listing(self):
        self.make_listing_green()
        done, calls = self.gate('--list', script='merge-gate.sh')
        self.assertEqual(done.returncode, 0, done.stdout)   # green listing: the smoke test would run if it were not for --list
        self.assertEqual(calls, [])
        self.assertIn('install dependencies (web)', done.stdout)

    def test_merge_gate_without_list_still_runs_its_own_step(self):
        self.make_listing_green()
        done, calls = self.gate(script='merge-gate.sh')
        self.assertIn('GATE GREEN', done.stdout)
        self.assertIn('agent_smoke ran', calls)   # proves the fixture reaches the step the listing must not run

if __name__ == '__main__':
    unittest.main()
