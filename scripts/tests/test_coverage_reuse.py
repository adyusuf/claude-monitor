"""scripts/coverage.sh in REUSE mode (COVERAGE_REUSE=1, COVERAGE_DIR): it judges the results the unit-test step left and
runs NO test. The danger is the quiet one: a result that is missing, unreadable or empty must be "NOT MEASURED" (exit 3,
which blocks), the threshold must be exact, and nothing may run a test a second time.
"""
import os
import shutil
import subprocess
import unittest

from gate_tool_support import ToolCase

SCRIPT = 'coverage.sh'
COBERTURA = '<?xml version="1.0"?><coverage lines-covered="%d" lines-valid="%d" line-rate="0"><packages/></coverage>'
API, AGENT, TRAY = 'ClaudeMonitor.Api.Tests', 'ClaudeMonitor.Agent.Tests', 'Aipod.Tray.Tests'


class CoverageReuse(ToolCase):
    def setUp(self):
        super().setUp()
        self.write('scripts/tests/test_ok.py',
                   'import unittest\n\nclass T(unittest.TestCase):\n    def test_ok(self):\n        pass\n')
        for tests in (API, AGENT, TRAY):
            self.write('tests/%s/%s.csproj' % (tests, tests), '<Project/>\n')
        self.write('web/package.json', '{}\n')
        self.git('add', '-A')
        self.git('commit', '-q', '-m', 'base')
        self.results = os.path.join(self.home, 'results')
        os.makedirs(self.results)
        for name in ('dotnet', 'npm', 'docker'):
            self.fake(name)                                              # any call to these is a test run: see no_test_ran()

    def put_dotnet(self, project, covered, total):
        folder = os.path.join(self.results, project)
        os.makedirs(folder, exist_ok=True)
        with open(os.path.join(folder, 'coverage.cobertura.xml'), 'w', encoding='utf-8') as handle:
            handle.write(COBERTURA % (covered, total))

    def put_web(self, covered, total):
        folder = os.path.join(self.results, 'web')
        os.makedirs(folder, exist_ok=True)
        with open(os.path.join(folder, 'coverage-summary.json'), 'w', encoding='utf-8') as handle:
            handle.write('{"total": {"lines": {"total": %d, "covered": %d, "pct": 0}}}' % (total, covered))

    def put_all(self, covered=90, total=100):
        self.put_dotnet(API, covered, total)
        self.put_dotnet(AGENT, covered, total)
        self.put_dotnet(TRAY, covered, total)
        self.put_web(covered, total)

    def reuse(self, minimum=None):
        env = {'COVERAGE_REUSE': '1', 'COVERAGE_DIR': self.results}
        if minimum:
            env['COVERAGE_MIN'] = str(minimum)
        return self.run_tool_env(env)

    def run_tool_env(self, env):
        run_env = self.clean_env(**env)
        run_env['PATH'] = self.fake_bin + os.pathsep + self.base_bin
        result = subprocess.run(['bash', os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', SCRIPT)],
                                cwd=self.root, capture_output=True, text=True, env=run_env)
        return result.stdout + result.stderr, result.returncode

    def no_test_ran(self):
        self.assertEqual([], self.calls(), 'reuse mode ran a test or a tool: %s' % self.calls())

    def test_results_at_or_above_the_threshold_pass_and_nothing_is_run(self):
        self.put_all(90, 100)
        out, code = self.reuse()
        self.assertEqual(0, code, out)
        self.assertEqual(4, out.count('✓ at or above 80%'), out)
        self.assertIn('Lines: 90.00% (90/100)', out)
        self.no_test_ran()

    def test_exactly_the_threshold_passes_and_just_below_does_not(self):
        self.put_all(80, 100)
        self.assertEqual(0, self.reuse()[1])
        self.put_dotnet(API, 7999, 10000)                                # 79.99%: a rounded 80.0 must not pass
        out, code = self.reuse()
        self.assertEqual(1, code, out)
        self.assertIn('✗ below 80%', out)

    def test_each_codebase_is_judged_on_its_own_never_averaged(self):
        self.put_all(100, 100)
        self.put_web(50, 100)
        out, code = self.reuse()
        self.assertEqual(1, code, out)
        self.assertEqual(3, out.count('✓ at or above 80%'), out)
        self.assertEqual(1, out.count('✗ below 80%'), out)

    def test_the_threshold_is_the_minimum_given(self):
        self.put_all(90, 100)
        out, code = self.reuse(minimum=95)
        self.assertEqual(1, code, out)
        self.assertIn('✗ below 95%', out)

    def test_a_missing_result_is_not_measured_and_blocks(self):
        self.put_all()
        os.remove(os.path.join(self.results, API, 'coverage.cobertura.xml'))
        out, code = self.reuse()
        self.assertEqual(3, code, out)
        self.assertIn('NOT MEASURED', out)
        self.no_test_ran()

    def test_a_missing_tray_result_is_not_measured_and_blocks(self):
        self.put_all()
        os.remove(os.path.join(self.results, TRAY, 'coverage.cobertura.xml'))
        out, code = self.reuse()
        self.assertEqual(3, code, out)
        self.assertIn('NOT MEASURED', out)

    def test_a_missing_web_result_is_not_measured(self):
        self.put_all()
        shutil.rmtree(os.path.join(self.results, 'web'))
        out, code = self.reuse()
        self.assertEqual(3, code, out)

    def test_an_unreadable_or_empty_result_is_not_measured(self):
        for body in ('this is not xml', '<coverage lines-covered="1"/>', COBERTURA % (0, 0), COBERTURA % (5, 3)):
            self.put_all()
            with open(os.path.join(self.results, AGENT, 'coverage.cobertura.xml'), 'w', encoding='utf-8') as handle:
                handle.write(body)
            out, code = self.reuse()
            self.assertEqual(3, code, '%r: %s' % (body, out))

    def test_a_red_script_test_still_blocks_in_reuse_mode(self):
        self.put_all()
        self.write('scripts/tests/test_red.py',
                   'import unittest\n\nclass T(unittest.TestCase):\n    def test_red(self):\n        self.fail("red")\n')
        out, code = self.reuse()
        self.assertEqual(1, code, out)
        self.assertIn('the tests are red', out)

    def test_without_reuse_the_script_runs_the_tests_itself(self):
        # Standalone: the fakes stand for dotnet and npm. dotnet writes no coverage file, so it must be NOT MEASURED
        # (never a pass), and the call proves the script ran the tests itself.
        self.put_all()
        out, code = self.run_tool_env({'COVERAGE_DIR': self.results})   # COVERAGE_DIR alone, without COVERAGE_REUSE
        self.assertTrue(any(c.startswith('dotnet test') for c in self.calls()), self.calls())
        self.assertNotEqual(0, code, out)

    def test_reuse_removes_the_directory_a_gate_run_made(self):
        self.put_all()
        out, code = self.run_tool_env({'COVERAGE_REUSE': '1', 'COVERAGE_DIR': self.results, 'COVERAGE_DIR_OWNED': '1'})
        self.assertEqual(0, code, out)
        self.assertFalse(os.path.exists(self.results))


class GateConf(unittest.TestCase):
    """scripts/merge-gate.conf, sourced the way gate-core.sh does: what a child process (coverage.sh) will see."""

    ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))

    def sourced(self, **env):
        run_env = {k: v for k, v in os.environ.items() if not k.startswith('COVERAGE_')}
        run_env.update(env)
        script = ('. scripts/merge-gate.conf; '
                  'printf "MIN=%s\\nREUSE=%s\\nOWNED=%s\\nDIR=%s\\n" "${COVERAGE_MIN:-}" "${COVERAGE_REUSE:-}" '
                  '"${COVERAGE_DIR_OWNED:-}" "${COVERAGE_DIR:-}"; env | grep "^COVERAGE_" | sort')
        result = subprocess.run(['bash', '-c', script], cwd=self.ROOT, capture_output=True, text=True, env=run_env)
        self.assertEqual(0, result.returncode, result.stderr)
        values = dict(line.split('=', 1) for line in result.stdout.splitlines() if '=' in line)
        return values, result.stdout

    def test_the_threshold_is_exported_so_coverage_sh_sees_it(self):
        values, out = self.sourced(TARGET='prod')
        self.assertEqual('80', values['MIN'])
        self.assertIn('COVERAGE_MIN=80', out.split('DIR=', 1)[1])        # in the environment, not only a shell variable

    def test_a_dev_gate_run_gets_a_fresh_coverage_directory_to_reuse(self):
        values, _ = self.sourced(TARGET='dev')
        try:
            self.assertEqual('1', values['REUSE'])
            self.assertEqual('1', values['OWNED'])
            self.assertTrue(os.path.isdir(values['DIR']), values['DIR'])
            self.assertEqual([], os.listdir(values['DIR']))                # fresh: nothing a stale run could have left
        finally:
            shutil.rmtree(values['DIR'], ignore_errors=True)

    def test_the_prod_gate_and_a_listing_make_no_directory(self):
        for env in ({'TARGET': 'prod'}, {'TARGET': 'dev', 'LIST_ONLY': '1'}):
            values, _ = self.sourced(**env)
            self.assertEqual('', values['DIR'], env)
            self.assertEqual('', values['REUSE'], env)


if __name__ == '__main__':
    unittest.main()
