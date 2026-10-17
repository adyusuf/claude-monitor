"""Shared fixtures for the tests of the gate tools (quick-check.sh, ci-status.sh, waived-gates.sh).

Not a test module (the name does not match test_*.py). Every tool runs in a throw-away git repository
with a PATH made of ONLY the base tools plus fake tools the test installs, so "the tool is missing" is
true on any machine — a runner that ships shellcheck or gh in /usr/bin cannot turn it into a pass.
"""
import os
import shutil
import stat
import subprocess
import tempfile
import unittest

SCRIPTS = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
BASE_TOOLS = ('bash', 'sh', 'env', 'git', 'python3', 'awk', 'sed', 'grep', 'sort', 'tr', 'mktemp', 'rm', 'dirname',
              'basename', 'find', 'head', 'tail', 'cat', 'ls', 'wc', 'uname', 'date', 'tee', 'cut', 'mkdir')
# The gate exports these to everything it runs (scripts/merge-gate.conf): a tool under test must not see them
# unless the test sets them, or a test that passes standalone fails inside the gate.
GATE_VARS = ('COVERAGE_DIR', 'COVERAGE_REUSE', 'COVERAGE_DIR_OWNED', 'COVERAGE_MIN', 'COVERAGE_CMD')
GIT_ENV = {'GIT_CONFIG_GLOBAL': os.devnull, 'GIT_CONFIG_SYSTEM': os.devnull, 'GIT_TERMINAL_PROMPT': '0'}


class ToolCase(unittest.TestCase):
    """A temporary repository plus a PATH of base tools and installed fakes."""

    def setUp(self):
        self.home = os.path.realpath(tempfile.mkdtemp())
        self.root = os.path.join(self.home, 'repo')
        self.base_bin = os.path.join(self.home, 'base-bin')
        self.fake_bin = os.path.join(self.home, 'fake-bin')
        self.log = os.path.join(self.home, 'calls.log')
        os.makedirs(self.root)
        os.makedirs(self.base_bin)
        os.makedirs(self.fake_bin)
        for tool in BASE_TOOLS:
            found = shutil.which(tool)
            if found:
                os.symlink(found, os.path.join(self.base_bin, tool))
        self.git('init', '-q', '-b', 'main')

    def tearDown(self):
        shutil.rmtree(self.home, ignore_errors=True)

    # -- git ---------------------------------------------------------------------------------------------------
    def git(self, *args, check=True):
        env = dict(os.environ, **GIT_ENV, GIT_AUTHOR_NAME='t', GIT_AUTHOR_EMAIL='t@t',
                   GIT_COMMITTER_NAME='t', GIT_COMMITTER_EMAIL='t@t')
        result = subprocess.run(['git', *args], cwd=self.root, capture_output=True, text=True, env=env, check=check)
        return result.stdout.strip()

    def write(self, relative, text='x\n'):
        path = os.path.join(self.root, relative)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, 'w', encoding='utf-8') as handle:
            handle.write(text)
        return path

    def commit(self, message, relative=None):
        self.write(relative or 'file-%d.txt' % len(self.git('log', '--oneline', check=False).splitlines()))
        self.git('add', '-A')
        self.git('commit', '-q', '-m', message)
        return self.git('rev-parse', 'HEAD')

    # -- fake tools --------------------------------------------------------------------------------------------
    def fake(self, name, body='', rc=0):
        """Install a fake tool: it appends '<name> <args>' to the call log, runs `body`, exits with `rc`."""
        path = os.path.join(self.fake_bin, name)
        with open(path, 'w', encoding='utf-8') as handle:
            handle.write('#!/bin/sh\necho "%s $*" >> "%s"\n%s\nexit %d\n' % (name, self.log, body, rc))
        os.chmod(path, os.stat(path).st_mode | stat.S_IEXEC)

    def calls(self):
        if not os.path.exists(self.log):
            return []
        with open(self.log, encoding='utf-8') as handle:
            return handle.read().splitlines()

    def clean_env(self, **extra):
        """The environment a tool runs in: this process's, minus the gate's own variables, plus the test's."""
        env = {k: v for k, v in os.environ.items() if k not in GATE_VARS}
        env.update(GIT_ENV, HOME=self.home)
        env.update(extra)
        return env

    def run_tool(self, script, *args, with_fakes=True):
        env = self.clean_env()
        env['PATH'] = (self.fake_bin + os.pathsep if with_fakes else '') + self.base_bin
        result = subprocess.run(['bash', os.path.join(SCRIPTS, script), *args], cwd=self.root,
                                capture_output=True, text=True, env=env)
        return result.stdout + result.stderr, result.returncode
