"""scripts/run.sh of the Claude Monitor plugin (ADR-0007): silent without a binary, the environment it gives the agent."""
import subprocess
import unittest

import importlib.util
import os

_spec = importlib.util.spec_from_file_location('plugin_support', os.path.join(os.path.dirname(os.path.abspath(__file__)), 'plugin_support.py'))
support = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(support)
PluginScriptsCase, OS, ARCH, FILE, RUN, ENSURE, Server, zip_of = (support.PluginScriptsCase, support.OS, support.ARCH, support.FILE, support.RUN,
                                                                   support.ENSURE, support.Server, support.zip_of)


@unittest.skipUnless(OS and ARCH, 'the scripts run on macOS and Linux here')
class RunScript(PluginScriptsCase):
    def test_run_is_silent_and_exits_0_without_a_data_directory_or_a_binary(self):
        for extra in ({'CLAUDE_PLUGIN_DATA': ''}, {}):
            result = self.run_script(RUN, 'hook', 'Stop', **extra)
            self.assertEqual((result.returncode, result.stdout, result.stderr), (0, '', ''))

    def test_run_executes_the_installed_binary_with_the_plugin_environment(self):
        self.install_fake('0.3.0')
        result = self.run_script(RUN, 'hook', 'PreToolUse', server='https://monitor.invalid')
        self.assertEqual(result.returncode, 0)
        self.assertIn('args: hook PreToolUse', result.stdout)
        self.assertIn(f'BIN={self.bin} ID=monitor-agent@monitor-agent-releases SERVER=https://monitor.invalid', result.stdout)

    def test_run_finds_the_server_in_the_option_the_mcp_environment_or_the_kept_file_in_that_order(self):
        self.install_fake('0.3.0')
        os.makedirs(self.data, exist_ok=True)
        with open(os.path.join(self.data, 'server.txt'), 'w') as f:
            f.write('https://kept.invalid\n')
        self.assertIn('SERVER=https://kept.invalid', self.run_script(RUN, 'status').stdout)  # a Bash tool has neither of the others
        self.assertIn('SERVER=https://mcp.invalid', self.run_script(RUN, 'mcp', CM_SERVER='https://mcp.invalid').stdout)
        self.assertIn('SERVER=https://option.invalid', self.run_script(RUN, 'hook', 'Stop', server='https://option.invalid', CM_SERVER='https://mcp.invalid').stdout)

    def test_run_turns_push_on_only_for_the_inbox_option(self):
        self.install_fake('0.3.0')
        for option, expected in (('inbox', 'PUSH=inbox'), ('', 'PUSH=')):
            with open(self.bin, 'w') as f:
                f.write('#!/bin/sh\necho "PUSH=$CM_PUSH"\n')
            result = self.run_script(RUN, 'x', CLAUDE_PLUGIN_OPTION_PUSH=option)
            self.assertEqual(result.stdout.strip(), expected)
        self.assertEqual(self.run_script(RUN, 'x', CLAUDE_PLUGIN_OPTION_PUSH='channel').stdout.strip(), 'PUSH=')  # an option cannot ask for anything else


if __name__ == '__main__':
    unittest.main()
