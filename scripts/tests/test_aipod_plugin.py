"""The aipod plugin's own wrapper and bootstrap (ADR-0007): the id the agent gets, the server option and the server of an already connected machine."""
import importlib.util
import json
import os
import unittest

_spec = importlib.util.spec_from_file_location('plugin_support', os.path.join(os.path.dirname(os.path.abspath(__file__)), 'plugin_support.py'))
support = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(support)
PluginScriptsCase, OS, ARCH, PLUGIN, RUN, ENSURE = support.PluginScriptsCase, support.OS, support.ARCH, support.PLUGIN, support.RUN, support.ENSURE
PLUGIN_ID = 'aipod@aipod-releases'


@unittest.skipUnless(OS and ARCH, 'the scripts run on macOS and Linux here')
class AipodPlugin(PluginScriptsCase):
    def test_the_manifest_names_the_plugin_aipod_and_the_wrapper_gives_the_agent_its_id(self):
        manifest = json.load(open(os.path.join(PLUGIN, '.claude-plugin', 'plugin.json')))
        self.assertEqual(manifest['name'], 'aipod')
        self.install_fake('0.6.0')
        result = self.run_script(RUN, 'hook', 'PreToolUse', server='https://aipod.invalid')
        self.assertIn(f'BIN={self.bin} ID={PLUGIN_ID} SERVER=https://aipod.invalid', result.stdout)

    def test_an_option_nobody_set_is_not_taken_for_an_address(self):
        self.install_fake('0.6.0')
        result = self.run_script(RUN, 'mcp', AP_SERVER='${user_config.server}')
        self.assertIn('SERVER=\n', result.stdout + '\n')  # unset, not the literal text
        self.assertNotIn('user_config', result.stdout)

    def test_the_bootstrap_takes_the_server_of_a_machine_that_is_already_connected_so_it_is_not_asked_again(self):
        home = os.path.join(self.tmp, 'agent-home')
        os.makedirs(home)
        with open(os.path.join(home, 'agent.json'), 'w') as f:
            f.write('{\n  "machineKey": "k",\n  "server": "https://aipod.invalid/",\n  "agentId": "x"\n}\n')
        self.pins('0.6.0')
        self.run_script(ENSURE, server='', AP_AGENT_HOME=home)
        with open(os.path.join(self.data, 'server.txt')) as f:
            self.assertEqual(f.read().strip(), 'https://aipod.invalid')


if __name__ == '__main__':
    unittest.main()
