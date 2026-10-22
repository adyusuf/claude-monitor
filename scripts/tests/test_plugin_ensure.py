"""scripts/ensure.sh of the aipod plugin (ADR-0007): the bootstrap that installs the agent, hermetically (loopback server, fake agent)."""
import hashlib
import json
import os
import subprocess
import time
import unittest

import importlib.util
import os

_spec = importlib.util.spec_from_file_location('plugin_support', os.path.join(os.path.dirname(os.path.abspath(__file__)), 'plugin_support.py'))
support = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(support)
PluginScriptsCase, OS, ARCH, FILE, RUN, ENSURE, Server, zip_of = (support.PluginScriptsCase, support.OS, support.ARCH, support.FILE, support.RUN,
                                                                   support.ENSURE, support.Server, support.zip_of)


@unittest.skipUnless(OS and ARCH, 'the scripts run on macOS and Linux here')
class EnsureScript(PluginScriptsCase):
    def test_a_missing_binary_is_downloaded_checked_installed_and_the_daemon_handed_over(self):
        server = Server({f'/downloads/{FILE}': zip_of('0.3.0')})
        self.addCleanup(server.close)
        self.pins('0.3.0')
        result = self.run_script(ENSURE, server=server.url)
        self.assertEqual(result.returncode, 0)
        self.assertEqual(json.loads(result.stdout)['systemMessage'].startswith('aipod: installing the agent (version 0.3.0)'), True)
        self.assertTrue(self.wait_for('installed version 0.3.0'), open(self.log).read() if os.path.exists(self.log) else 'no log')
        self.assertTrue(os.access(self.bin, os.X_OK))
        self.assertEqual(subprocess.run([self.bin, 'version'], capture_output=True, text=True).stdout.strip(), '0.3.0')
        restarts = os.path.join(self.data, 'bin', 'restarts')
        end = time.time() + 10
        while time.time() < end and not (os.path.exists(restarts) and not os.path.exists(os.path.join(self.data, 'install.lock'))):
            time.sleep(0.1)
        self.assertTrue(os.path.exists(restarts), 'the daemon was not handed over to the new binary')
        # The restarted daemon must know it belongs to the plugin, or it would never notice the plugin being switched off.
        self.assertIn(f'id=aipod@aipod-releases bin={self.bin}', open(restarts).read())
        self.assertFalse(os.path.exists(os.path.join(self.data, 'install.lock')))

    def test_a_plugin_built_for_a_download_host_installs_from_it_without_any_server_address(self):
        server = Server({f'/pods/prod/{FILE}': zip_of('0.3.0')})
        self.addCleanup(server.close)
        self.pins('0.3.0')
        with open(os.path.join(self.root, 'download-base.txt'), 'w') as handle:
            handle.write(f'{server.url}/pods/prod\n')
        result = self.run_script(ENSURE)  # no server option, no agent.json
        self.assertEqual(result.returncode, 0)
        self.assertTrue(self.wait_for('installed version 0.3.0'), open(self.log).read() if os.path.exists(self.log) else 'no log')
        self.assertEqual(server.requests, [f'/pods/prod/{FILE}'])  # never the server's /downloads

    def test_a_download_base_that_is_not_an_address_is_ignored_and_the_server_is_asked(self):
        server = Server({f'/downloads/{FILE}': zip_of('0.3.0')})
        self.addCleanup(server.close)
        self.pins('0.3.0')
        with open(os.path.join(self.root, 'download-base.txt'), 'w') as handle:
            handle.write('ftp://nowhere.invalid/x\n')
        self.run_script(ENSURE, server=server.url)
        self.assertTrue(self.wait_for('installed version 0.3.0'), open(self.log).read() if os.path.exists(self.log) else 'no log')
        self.assertIn('download-base.txt does not hold an https address', open(self.log).read())

    def test_the_restart_after_an_install_keeps_the_push_option_and_the_server_the_wrapper_gives_every_other_run(self):
        server = Server({f'/downloads/{FILE}': zip_of('0.3.0')})
        self.addCleanup(server.close)
        self.pins('0.3.0')
        self.run_script(ENSURE, server=server.url, CLAUDE_PLUGIN_OPTION_PUSH='inbox')
        restarts = os.path.join(self.data, 'bin', 'restarts')
        end = time.time() + 20
        while time.time() < end and not os.path.exists(restarts):
            time.sleep(0.1)
        text = open(restarts).read()
        self.assertIn('push=inbox', text)  # or the daemon the upgrade starts would silently stop pushing
        self.assertIn(f'server={server.url}', text)
        self.assertIn('id=aipod@aipod-releases', text)

    def test_a_download_that_does_not_match_the_pin_is_refused_and_nothing_is_installed(self):
        server = Server({f'/downloads/{FILE}': zip_of('9.9.9')})
        self.addCleanup(server.close)
        self.pins('0.3.0', archive=zip_of('0.3.0'))
        self.run_script(ENSURE, server=server.url)
        self.assertTrue(self.wait_for('does not match the pinned SHA-256'))
        self.assertFalse(os.path.exists(self.bin))

    def test_an_up_to_date_or_newer_binary_is_left_alone_and_the_server_is_not_asked(self):
        server = Server({f'/downloads/{FILE}': zip_of('0.3.0')})
        self.addCleanup(server.close)
        self.pins('0.3.0')
        for installed in ('0.3.0', '0.4.1'):  # equal, and newer (the agent updated itself): never a downgrade
            self.install_fake(installed)
            result = self.run_script(ENSURE, server=server.url)
            self.assertEqual((result.returncode, result.stdout), (0, ''))
        self.assertEqual(server.requests, [])

    def test_an_older_binary_is_replaced_by_the_pinned_version(self):
        server = Server({f'/downloads/{FILE}': zip_of('0.3.0')})
        self.addCleanup(server.close)
        self.pins('0.3.0')
        self.install_fake('0.2.9')
        self.run_script(ENSURE, server=server.url)
        self.assertTrue(self.wait_for('installed version 0.3.0'))
        self.assertEqual(subprocess.run([self.bin, 'version'], capture_output=True, text=True).stdout.strip(), '0.3.0')

    def test_without_an_https_server_or_a_pinned_build_nothing_is_downloaded_and_the_user_is_told(self):
        server = Server({f'/downloads/{FILE}': zip_of('0.3.0')})
        self.addCleanup(server.close)
        self.pins('0.3.0')
        refused = self.run_script(ENSURE, server='http://aipod.invalid')
        self.assertIn('set the server address', json.loads(refused.stdout)['systemMessage'])
        self.assertFalse(os.path.exists(self.bin))
        with open(os.path.join(self.root, 'agent-release.txt'), 'w') as f:
            f.write('0.3.0\n')  # the placeholder committed before a release
        unpinned = self.run_script(ENSURE, server=server.url)
        self.assertIn('no published build', json.loads(unpinned.stdout)['systemMessage'])
        self.assertIn('nothing was installed', open(self.log).read())
        self.assertEqual(server.requests, [])

    def test_a_hostile_file_name_in_the_pins_is_refused(self):
        server = Server({})
        self.addCleanup(server.close)
        for name in ('../../etc/passwd', 'aipod-../../x-y.zip', f'aipod-{OS}-{ARCH}.zip/../x'):
            with open(os.path.join(self.root, 'agent-release.txt'), 'w') as f:
                f.write(f'0.3.0\n{OS}-{ARCH} {"a" * 64} {name}\n')
            self.run_script(ENSURE, server=server.url)
            self.assertIn('is not a build file name', open(self.log).read(), name)
        self.assertEqual(server.requests, [])

    def test_a_server_address_that_only_starts_like_a_local_one_is_refused(self):
        self.pins('0.3.0')
        for address in ('http://localhost.attacker.example', 'http://127.0.0.1.attacker.example', 'http://localhost@attacker.example',
                        'http://localhostx:80', 'http://attacker.example', 'ftp://aipod.invalid', 'https://a b', "https://x'y", 'https://x$(id)'):
            result = self.run_script(ENSURE, server=address)
            self.assertIn('set the server address', json.loads(result.stdout)['systemMessage'], address)
            self.assertFalse(os.path.exists(os.path.join(self.data, 'server.txt')), address)
        for address in ('http://localhost', 'http://localhost:5080', 'http://127.0.0.1:9872/', 'https://aipod.invalid', 'https://aipod.invalid/base'):
            self.run_script(ENSURE, server=address)
            self.assertEqual(open(os.path.join(self.data, 'server.txt')).read().strip(), address.rstrip('/'))

    def test_a_failed_install_is_not_retried_at_every_session_start_and_a_new_pinned_version_is(self):
        server = Server({f'/downloads/{FILE}': zip_of('9.9.9')})
        self.addCleanup(server.close)
        self.pins('0.3.0', archive=zip_of('0.3.0'))
        self.run_script(ENSURE, server=server.url)
        self.assertTrue(self.wait_for('does not match the pinned SHA-256'))
        end = time.time() + 10
        while time.time() < end and os.path.exists(os.path.join(self.data, 'install.lock')):
            time.sleep(0.1)
        before = len(server.requests)
        again = self.run_script(ENSURE, server=server.url)
        self.assertIn('the last install of version 0.3.0 failed', json.loads(again.stdout)['systemMessage'])
        self.assertEqual(len(server.requests), before)  # nothing downloaded the second time
        self.pins('0.3.0', archive=zip_of('9.9.9'))  # the same version pinned again (the remedy for a wrong pin) is a new chance too
        self.run_script(ENSURE, server=server.url)
        end = time.time() + 10
        while time.time() < end and len(server.requests) == before:
            time.sleep(0.1)
        self.assertGreater(len(server.requests), before)
        self.assertTrue(self.wait_for('installed version 0.3.0'))
        end = time.time() + 10
        while time.time() < end and os.path.exists(os.path.join(self.data, 'install.lock')):
            time.sleep(0.1)
        os.remove(self.bin)
        os.remove(os.path.join(self.data, 'bin', 'restarts')) if os.path.exists(os.path.join(self.data, 'bin', 'restarts')) else None
        with open(os.path.join(self.data, 'install.failed'), 'w') as f:
            f.write('0.3.0 stale\n')  # a failure of an earlier pin
        before = len(server.requests)
        self.pins('0.3.1', archive=zip_of('0.3.1'))  # a new version is a new chance
        self.run_script(ENSURE, server=server.url)
        end = time.time() + 10
        while time.time() < end and len(server.requests) == before:
            time.sleep(0.1)
        self.assertGreater(len(server.requests), before)

    def test_any_of_the_pinned_builds_of_a_version_is_accepted(self):
        server = Server({f'/downloads/{FILE}': zip_of('0.3.0')})
        self.addCleanup(server.close)
        real = hashlib.sha256(zip_of('0.3.0')).hexdigest()
        with open(os.path.join(self.root, 'agent-release.txt'), 'w') as f:
            f.write(f'0.3.0\n{OS}-{ARCH} {"0" * 64} {FILE}\n{OS}-{ARCH} {real} {FILE}\n')  # the test build and the prod build of the same version
        self.run_script(ENSURE, server=server.url)
        self.assertTrue(self.wait_for('installed version 0.3.0'))

    def test_a_replacement_that_cannot_be_moved_into_place_leaves_the_old_binary_where_it_was(self):
        server = Server({f'/downloads/{FILE}': zip_of('0.3.0')})
        self.addCleanup(server.close)
        self.pins('0.3.0')
        self.install_fake('0.2.9')
        os.chmod(os.path.dirname(self.bin), 0o500)  # nothing can be written into the folder: neither the rename nor the move succeeds
        self.addCleanup(os.chmod, os.path.dirname(self.bin), 0o700)
        self.run_script(ENSURE, server=server.url)
        self.assertTrue(self.wait_for('could not replace the installed binary'))
        self.assertEqual(subprocess.run([self.bin, 'version'], capture_output=True, text=True).stdout.strip(), '0.2.9')

    def test_a_server_value_with_a_second_line_is_refused(self):
        self.pins('0.3.0')
        result = self.run_script(ENSURE, server='https://ok.invalid\n$(id)')
        self.assertIn('set the server address', json.loads(result.stdout)['systemMessage'])
        self.assertFalse(os.path.exists(os.path.join(self.data, 'server.txt')))

    def test_a_long_install_log_is_cut_to_its_last_lines(self):
        os.makedirs(self.data, exist_ok=True)
        with open(self.log, 'w') as f:
            f.write(''.join(f'line {i} ' + 'x' * 100 + '\n' for i in range(3000)))
        self.run_script(ENSURE, server='https://aipod.invalid')
        lines = open(self.log).read().splitlines()
        self.assertLess(len(lines), 400)
        self.assertTrue(any('line 2999' in l for l in lines))

    def test_every_path_exits_0_even_without_the_plugin_variables(self):
        result = subprocess.run(['sh', ENSURE], env={'PATH': os.environ['PATH']}, capture_output=True, text=True, timeout=30)
        self.assertEqual((result.returncode, result.stdout), (0, ''))


if __name__ == '__main__':
    unittest.main()
