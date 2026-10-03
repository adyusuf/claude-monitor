"""scripts/cargo-audit.sh with a stubbed `cargo audit`.

What matters: a transient advisory-database failure is retried, and a run that never gets a
verdict is NOT RUN (exit 3), never a pass. A vulnerability blocks (exit 1).
"""
import os
import stat
import subprocess
import tempfile
import unittest

SCRIPT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'cargo-audit.sh'))
SCANNING = 'Scanning Cargo.lock for vulnerabilities (500 crate dependencies)'


class CargoAudit(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.repo = os.path.join(self.tmp, 'repo')
        os.makedirs(os.path.join(self.repo, 'desktop'))
        open(os.path.join(self.repo, 'desktop', 'Cargo.lock'), 'w').close()
        subprocess.run(['git', 'init', '-q', self.repo], check=True)
        self.bin = os.path.join(self.tmp, 'cargo', 'bin')
        os.makedirs(self.bin)
        self.counter = os.path.join(self.tmp, 'calls')

    def stub(self, name, body):
        path = os.path.join(self.bin, name)
        with open(path, 'w') as f:
            f.write('#!/bin/sh\n' + body)
        os.chmod(path, os.stat(path).st_mode | stat.S_IXUSR)

    def cargo(self, fail_first, then_exit=0, then_out=SCANNING):
        """A `cargo` whose first `fail_first` calls cannot fetch the database."""
        self.stub('cargo-audit', 'exit 0\n')
        self.stub('cargo', (
            'n=$(cat "%(c)s" 2>/dev/null || echo 0); n=$((n+1)); echo $n > "%(c)s"\n'
            'if [ $n -le %(f)d ]; then echo "error: couldn\'t fetch advisory database"; exit 1; fi\n'
            'echo "%(o)s"; exit %(x)d\n') % {'c': self.counter, 'f': fail_first, 'o': then_out, 'x': then_exit})

    def run_script(self, tries='3'):
        env = dict(os.environ, CARGO_HOME=os.path.join(self.tmp, 'cargo'),
                   AUDIT_FETCH_TRIES=tries, AUDIT_FETCH_PAUSE='0')
        return subprocess.run(['bash', SCRIPT], cwd=self.repo, env=env, capture_output=True, text=True)

    def calls(self):
        with open(self.counter) as f:
            return int(f.read())

    def test_clean_first_time(self):
        self.cargo(0)
        r = self.run_script()
        self.assertEqual((r.returncode, self.calls()), (0, 1), r.stdout)

    def test_transient_failure_is_retried(self):
        self.cargo(2)
        r = self.run_script()
        self.assertEqual((r.returncode, self.calls()), (0, 3), r.stdout)
        self.assertIn('retrying', r.stdout)

    def test_no_verdict_after_all_tries_is_not_run(self):
        self.cargo(99)
        r = self.run_script(tries='2')
        self.assertEqual((r.returncode, self.calls()), (3, 2), r.stdout)
        self.assertIn('NOT RUN', r.stdout)

    def test_a_vulnerability_blocks_and_is_not_retried(self):
        self.cargo(0, then_exit=1, then_out=SCANNING + '\nCrate:     x\nID:        RUSTSEC-0000-0000')
        r = self.run_script()
        self.assertEqual((r.returncode, self.calls()), (1, 1), r.stdout)

    def test_missing_tool_is_not_run(self):
        env = dict(os.environ, CARGO_HOME=os.path.join(self.tmp, 'cargo'), PATH='/usr/bin:/bin')
        r = subprocess.run(['bash', SCRIPT], cwd=self.repo, env=env, capture_output=True, text=True)
        self.assertEqual(r.returncode, 3, r.stdout)

    def test_no_rust_is_not_applicable(self):
        os.remove(os.path.join(self.repo, 'desktop', 'Cargo.lock'))
        self.assertEqual(self.run_script().returncode, 0)


if __name__ == '__main__':
    unittest.main()
