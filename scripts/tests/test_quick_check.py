"""scripts/quick-check.sh: the local first look. It runs the gate's own commands, and a step that did not run never counts.

Each case pins one way the script could quietly become useless: a red step reported as green, a missing tool treated
as a pass, a command that drifts from the gate's, or a changed file that no step looks at.
"""
import os
import unittest

from gate_tool_support import ToolCase

SCRIPT = 'quick-check.sh'
OK_TEST = 'import unittest\n\nclass T(unittest.TestCase):\n    def test_ok(self):\n        self.assertTrue(True)\n'
RED_TEST = 'import unittest\n\nclass T(unittest.TestCase):\n    def test_red(self):\n        self.fail("red")\n'


class QuickCheck(ToolCase):
    def setUp(self):
        super().setUp()
        self.write('App.slnx', '<Solution/>\n')
        self.write('scripts/tests/test_ok.py', OK_TEST)
        self.commit('base')
        self.write('scripts/tool.sh', '#!/bin/sh\necho hi\n')          # an uncommitted shell script: "changed"

    def all_tools(self):
        for name in ('dotnet', 'shellcheck', 'gitleaks'):
            self.fake(name)

    def test_every_step_green_runs_the_gates_own_commands(self):
        self.all_tools()
        out, code = self.run_tool(SCRIPT)
        self.assertEqual(0, code, out)
        self.assertIn('GREEN', out)
        calls = self.calls()
        self.assertIn('dotnet format ./App.slnx --verify-no-changes', calls)
        self.assertIn('dotnet build ./App.slnx -warnaserror', calls)
        self.assertIn('shellcheck --severity=style scripts/tool.sh', calls)
        self.assertIn('gitleaks detect --no-banner --redact', calls)

    def test_a_red_step_makes_the_run_red_but_the_others_still_run(self):
        self.all_tools()
        self.fake('shellcheck', rc=1)
        out, code = self.run_tool(SCRIPT)
        self.assertEqual(1, code, out)
        self.assertIn('✗ shellcheck', out)
        self.assertIn('RED', out)
        self.assertNotIn('GREEN', out)
        self.assertIn('gitleaks detect --no-banner --redact', self.calls())

    def test_a_red_build_is_red(self):
        self.all_tools()
        self.fake('dotnet', body='[ "$1" = build ] && exit 1', rc=0)
        out, code = self.run_tool(SCRIPT)
        self.assertEqual(1, code, out)
        self.assertIn('✗ dotnet build', out)

    def test_a_missing_tool_is_skipped_and_the_run_is_not_green(self):
        self.fake('dotnet')
        self.fake('shellcheck')                                         # gitleaks is not installed
        out, code = self.run_tool(SCRIPT)
        self.assertNotEqual(0, code, out)
        self.assertIn('SKIPPED: gitleaks detect (gitleaks is not installed)', out)
        self.assertIn('INCOMPLETE', out)
        self.assertNotIn('GREEN', out)

    def test_a_failure_outranks_a_skipped_step(self):
        self.fake('dotnet', rc=1)
        self.fake('shellcheck')                                         # gitleaks missing as well
        out, code = self.run_tool(SCRIPT)
        self.assertEqual(1, code, out)

    def test_the_script_tests_run_when_a_script_changed_and_red_blocks(self):
        self.all_tools()
        self.write('scripts/tests/test_red.py', RED_TEST)
        out, code = self.run_tool(SCRIPT)
        self.assertEqual(1, code, out)
        self.assertIn('✗ scripts/tests', out)

    def test_the_script_tests_are_left_alone_when_no_script_changed(self):
        self.all_tools()
        os.remove(os.path.join(self.root, 'scripts/tool.sh'))           # setUp's uncommitted script is not part of this case
        self.write('scripts/tests/test_red.py', RED_TEST)               # a red test already in history: not "changed"
        self.git('add', '-A')
        self.git('commit', '-q', '-m', 'a red test already in history')
        self.write('README.md', 'docs only\n')
        out, code = self.run_tool(SCRIPT)
        self.assertEqual(0, code, out)
        self.assertIn('n/a: no .py or .sh file changed', out)

    def test_list_prints_the_commands_and_runs_nothing(self):
        self.all_tools()
        out, code = self.run_tool(SCRIPT, '--list')
        self.assertEqual(0, code, out)
        self.assertIn('dotnet format ./App.slnx --verify-no-changes', out)
        self.assertEqual([], self.calls())

    def test_a_bad_option_is_a_usage_error(self):
        out, code = self.run_tool(SCRIPT, '--nope')
        self.assertEqual(2, code, out)


class AffectedTests(ToolCase):
    """--tests: a test project runs when a changed file is inside it or inside a project it references."""

    def setUp(self):
        super().setUp()
        self.write('App.slnx', '<Solution/>\n')
        self.write('src/Api/Api.csproj', '<Project><ItemGroup><ProjectReference Include="..\\Core\\Core.csproj"/></ItemGroup></Project>\n')
        self.write('src/Core/Core.csproj', '<Project/>\n')
        self.write('src/Other/Other.csproj', '<Project/>\n')
        self.write('tests/Api.Tests/Api.Tests.csproj', '<Project><ItemGroup><ProjectReference Include="..\\..\\src\\Api\\Api.csproj"/></ItemGroup></Project>\n')
        self.write('tests/Other.Tests/Other.Tests.csproj', '<Project><ItemGroup><ProjectReference Include="..\\..\\src\\Other\\Other.csproj"/></ItemGroup></Project>\n')
        self.write('web/package.json', '{}\n')
        self.commit('base')
        for name in ('dotnet', 'shellcheck', 'gitleaks', 'npm'):
            self.fake(name)

    def tests_run(self):
        return [c for c in self.calls() if c.startswith('dotnet test') or c.startswith('npm')]

    def test_a_change_in_a_referenced_project_runs_the_tests_that_reach_it(self):
        self.write('src/Core/Thing.cs', 'class Thing {}\n')
        out, code = self.run_tool(SCRIPT, '--tests')
        self.assertEqual(0, code, out)
        self.assertEqual(['dotnet test tests/Api.Tests/Api.Tests.csproj --nologo'], self.tests_run())

    def test_a_change_in_an_unrelated_project_does_not_run_the_others(self):
        self.write('src/Other/Thing.cs', 'class Thing {}\n')
        out, code = self.run_tool(SCRIPT, '--tests')
        self.assertEqual(0, code, out)
        self.assertEqual(['dotnet test tests/Other.Tests/Other.Tests.csproj --nologo'], self.tests_run())

    def test_a_web_change_runs_the_web_tests(self):
        self.write('web/src/a.ts', 'export {}\n')
        out, code = self.run_tool(SCRIPT, '--tests')
        self.assertEqual(0, code, out)
        self.assertEqual(['npm --prefix web test -- --run'], self.tests_run())

    def test_nothing_affected_runs_no_tests_and_says_so(self):
        self.write('docs/a.md', 'x\n')
        out, code = self.run_tool(SCRIPT, '--tests')
        self.assertEqual(0, code, out)
        self.assertEqual([], self.tests_run())
        self.assertIn('no test project is affected', out)

    def test_without_the_flag_no_test_project_runs(self):
        self.write('src/Core/Thing.cs', 'class Thing {}\n')
        out, code = self.run_tool(SCRIPT)
        self.assertEqual(0, code, out)
        self.assertEqual([], self.tests_run())


if __name__ == '__main__':
    unittest.main()
