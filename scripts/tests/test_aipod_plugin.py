"""The aipod plugin and the bridge version of the old plugin (ADR-0013 section 1): who reports when both are installed, and the ids each one gives the agent."""
import importlib.util
import json
import os
import subprocess
import unittest

_spec = importlib.util.spec_from_file_location('plugin_support', os.path.join(os.path.dirname(os.path.abspath(__file__)), 'plugin_support.py'))
support = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(support)
PluginScriptsCase, OS, ARCH, AIPOD, PLUGIN = support.PluginScriptsCase, support.OS, support.ARCH, support.AIPOD, support.PLUGIN
NEW_RUN, NEW_ENSURE = os.path.join(AIPOD, 'scripts', 'run.sh'), os.path.join(AIPOD, 'scripts', 'ensure.sh')
OLD_ID, NEW_ID = 'monitor-agent@monitor-agent-releases', 'aipod@aipod-releases'


class AipodCase(PluginScriptsCase):
    plugin_folder = AIPOD

    def claude_dir(self):
        path = os.path.join(self.tmp, 'claude')
        os.makedirs(os.path.join(path, 'plugins'), exist_ok=True)
        return path

    def enable(self, plugin_id, version=None):
        """Records a plugin as enabled in settings.json and as installed (with its version) the way Claude Code does."""
        path = self.claude_dir()
        settings = os.path.join(path, 'settings.json')
        enabled = json.load(open(settings)).get('enabledPlugins', {}) if os.path.exists(settings) else {}
        enabled[plugin_id] = True
        json.dump({'enabledPlugins': enabled}, open(settings, 'w'), indent=2)
        installed = os.path.join(path, 'plugins', 'installed_plugins.json')
        plugins = json.load(open(installed))['plugins'] if os.path.exists(installed) else {}
        if version:
            plugins[plugin_id] = [{'scope': 'user', 'installPath': '/x', 'version': version}]
        json.dump({'version': 2, 'plugins': plugins}, open(installed, 'w'), indent=2)


@unittest.skipUnless(OS and ARCH, 'the scripts run on macOS and Linux here')
class AipodPlugin(AipodCase):
    def test_the_manifest_names_the_plugin_aipod_and_the_wrapper_gives_the_agent_its_id(self):
        manifest = json.load(open(os.path.join(AIPOD, '.claude-plugin', 'plugin.json')))
        self.assertEqual(manifest['name'], 'aipod')
        self.install_fake('0.6.0')
        result = self.run_script(NEW_RUN, 'hook', 'PreToolUse', server='https://monitor.invalid')
        self.assertIn(f'BIN={self.bin} ID={NEW_ID} SERVER=https://monitor.invalid', result.stdout)

    def test_the_id_is_the_one_of_the_marketplace_the_plugin_is_enabled_from(self):
        self.install_fake('0.6.0')
        self.enable('aipod@monitor-agent-releases')  # installed from the repository catalog
        result = self.run_script(NEW_RUN, 'hook', 'PreToolUse', server='https://monitor.invalid')
        self.assertIn('ID=aipod@monitor-agent-releases', result.stdout)
        self.enable(NEW_ID)  # both: the URL catalog wins
        self.assertIn(f'ID={NEW_ID}', self.run_script(NEW_RUN, 'hook', 'PreToolUse', server='https://monitor.invalid').stdout)

    def test_an_option_nobody_set_is_not_taken_for_an_address(self):
        self.install_fake('0.6.0')
        result = self.run_script(NEW_RUN, 'mcp', CM_SERVER='${user_config.server}')
        self.assertIn('SERVER=\n', result.stdout + '\n')  # unset, not the literal text
        self.assertNotIn('user_config', result.stdout)

    def test_the_bootstrap_takes_the_server_of_a_machine_that_is_already_connected_so_it_is_not_asked_again(self):
        home = os.path.join(self.tmp, 'agent-home')
        os.makedirs(home)
        with open(os.path.join(home, 'agent.json'), 'w') as f:
            f.write('{\n  "machineKey": "k",\n  "server": "https://monitor.invalid/",\n  "agentId": "x"\n}\n')
        self.pins('0.6.0')
        self.run_script(NEW_ENSURE, server='', AIPOD_AGENT_HOME=home)
        with open(os.path.join(self.data, 'server.txt')) as f:
            self.assertEqual(f.read().strip(), 'https://monitor.invalid')

    def test_a_pre_bridge_old_plugin_keeps_reporting_and_aipod_stands_down_for_hooks_and_mcp_only(self):
        self.install_fake('0.6.0')
        self.enable(OLD_ID, '0.5.0')
        for args in (('hook', 'Stop'), ('mcp',)):
            quiet = self.run_script(NEW_RUN, *args)
            self.assertEqual((quiet.returncode, quiet.stdout, quiet.stderr), (0, '', ''), args)
        self.assertIn('args: status', self.run_script(NEW_RUN, 'status').stdout)  # a command someone types is never held back
        self.enable(OLD_ID, '0.4.3')
        self.assertEqual(self.run_script(NEW_RUN, 'hook', 'Stop').stdout, '')  # every version up to 0.5.0 is a pre-bridge one

    def test_aipod_reports_when_the_old_plugin_is_the_bridge_is_off_or_cannot_be_read(self):
        self.install_fake('0.6.0')
        self.enable(OLD_ID, '0.5.1')
        self.assertIn('args: hook Stop', self.run_script(NEW_RUN, 'hook', 'Stop').stdout)  # the bridge stands down for aipod
        self.enable(OLD_ID, '0.10.0')
        self.assertIn('args: hook Stop', self.run_script(NEW_RUN, 'hook', 'Stop').stdout)  # compared as versions
        os.remove(os.path.join(self.claude_dir(), 'plugins', 'installed_plugins.json'))
        self.assertIn('args: hook Stop', self.run_script(NEW_RUN, 'hook', 'Stop').stdout)  # no version known: go on, the bridge is the likelier one
        os.remove(os.path.join(self.claude_dir(), 'settings.json'))
        self.assertIn('args: hook Stop', self.run_script(NEW_RUN, 'hook', 'Stop').stdout)  # the old plugin is not enabled at all

    def test_the_bootstrap_tells_the_person_about_a_still_enabled_old_plugin(self):
        self.pins('0.6.0')
        self.enable(OLD_ID, '0.5.0')
        result = self.run_script(NEW_ENSURE, server='')
        self.assertIn('old monitor-agent plugin is still enabled', json.loads(result.stdout)['systemMessage'])
        self.assertIn('/aipod:connect', json.loads(result.stdout)['systemMessage'])


@unittest.skipUnless(OS and ARCH, 'the scripts run on macOS and Linux here')
class BridgePlugin(AipodCase):
    plugin_folder = PLUGIN

    def test_the_bridge_stands_down_for_hooks_and_mcp_once_aipod_is_enabled_from_either_catalog_and_not_before(self):
        run = os.path.join(PLUGIN, 'scripts', 'run.sh')
        self.install_fake('0.6.0')
        self.assertIn('args: hook Stop', self.run_script(run, 'hook', 'Stop').stdout)
        self.enable('aipod@monitor-agent-releases', '0.5.1')
        self.assertEqual(self.run_script(run, 'hook', 'Stop').stdout, '')
        os.remove(os.path.join(self.claude_dir(), 'settings.json'))
        self.enable(NEW_ID, '0.5.1')
        for args in (('hook', 'Stop'), ('mcp',)):
            quiet = self.run_script(run, *args)
            self.assertEqual((quiet.returncode, quiet.stdout, quiet.stderr), (0, '', ''), args)
        self.assertIn('args: logout', self.run_script(run, 'logout').stdout)  # what a person types still works

    def test_the_bridge_says_once_that_aipod_replaces_it_and_installs_nothing_when_aipod_is_enabled(self):
        ensure = os.path.join(PLUGIN, 'scripts', 'ensure.sh')
        self.pins('0.6.0')
        first = json.loads(self.run_script(ensure, server='').stdout)['systemMessage']
        self.assertIn('now called aipod', first)
        self.assertNotIn('now called aipod', self.run_script(ensure, server='').stdout)  # once
        if os.path.exists(self.log):
            os.remove(self.log)  # what the earlier runs wrote
        self.enable(NEW_ID, '0.5.1')
        result = self.run_script(ensure, server='https://monitor.invalid')
        self.assertEqual((result.returncode, result.stdout), (0, ''))
        self.assertFalse(os.path.exists(self.log))  # nothing was attempted


if __name__ == '__main__':
    unittest.main()
