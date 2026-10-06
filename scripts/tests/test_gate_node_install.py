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
GATE_FILES = ('gate-core.sh', 'gate-lib.sh', 'gate-lib-node.sh', 'gate-lib-prod.sh')

FAKE_NPM = """#!/bin/sh
echo "npm $*" >> "$FAKE_LOG"
case "$*" in
  *" ci"*)
    if [ -n "$FAKE_CI_FAIL" ]; then echo "npm error code ENOTFOUND" >&2; exit 1; fi
    mkdir -p web/node_modules/.bin && : > web/node_modules/.bin/eslint ;;
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
        with open(os.path.join(self.root, 'web', 'package.json'), 'w') as f:
            f.write('{"scripts": {"lint": "eslint .", "build": "vite build", "test": "vitest"}}')
        subprocess.run(['git', 'init', '-q', self.root], check=True)
        self.bin = os.path.join(self.home, 'bin')
        os.makedirs(self.bin)
        for name, body in (('npm', FAKE_NPM), ('npx', FAKE_NPX)):
            path = os.path.join(self.bin, name)
            with open(path, 'w') as f:
                f.write(body)
            os.chmod(path, 0o700)
        self.log = os.path.join(self.home, 'calls.log')

    def tearDown(self):
        shutil.rmtree(self.home, ignore_errors=True)

    def gate(self, *args, ci_fail=False):
        env = {'PATH': self.bin + ':/usr/bin:/bin', 'FAKE_LOG': self.log, 'HOME': self.home}
        if ci_fail:
            env['FAKE_CI_FAIL'] = '1'
        done = subprocess.run(['bash', 'scripts/gate-core.sh', 'dev', *args], cwd=self.root, env=env,
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


if __name__ == '__main__':
    unittest.main()
