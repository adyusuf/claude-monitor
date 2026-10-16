"""The Claude Monitor plugin's shell scripts and pin writer (ADR-0007): scripts/run.sh, scripts/ensure.sh, scripts/write_plugin_pins.py.

Everything is hermetic: a fake agent (a shell script) is zipped and served by a throw-away HTTP server on the loopback, and the scripts run with
a private HOME and plugin data directory. No real Claude Code, agent, server or network is touched.
"""
import hashlib
import http.server
import importlib.util
import io
import json
import os
import platform
import shutil
import subprocess
import tempfile
import threading
import time
import unittest
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
PLUGIN = os.path.normpath(os.path.join(HERE, '..', '..', 'plugin', 'monitor-agent'))
RUN, ENSURE = os.path.join(PLUGIN, 'scripts', 'run.sh'), os.path.join(PLUGIN, 'scripts', 'ensure.sh')
spec = importlib.util.spec_from_file_location('write_plugin_pins', os.path.join(HERE, '..', 'write_plugin_pins.py'))
pins_module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pins_module)

OS = {'Darwin': 'macos', 'Linux': 'linux'}.get(platform.system())


def native_machine():
    """The hardware's CPU, not this Python's: an x86_64 Python under Rosetta on Apple silicon says x86_64 (ensure.sh asks macOS the same way)."""
    machine = platform.machine().lower()
    if OS == 'macos' and machine == 'x86_64':
        translated = subprocess.run(['sysctl', '-n', 'sysctl.proc_translated'], capture_output=True, text=True)
        if translated.stdout.strip() == '1':
            return 'arm64'
    return machine


ARCH = {'arm64': 'arm64', 'aarch64': 'arm64', 'x86_64': 'x64', 'amd64': 'x64'}.get(native_machine())
FILE = f'cm-agent-{OS}-{ARCH}.zip'
FAKE = '#!/bin/sh\ncase "$1" in\n  version) echo "{version}" ;;\n  restart) echo "restarted id=$CM_PLUGIN_ID bin=$CM_BINARY" >> "$(dirname "$0")/restarts" ;;\n  *) echo "args: $*"; echo "BIN=$CM_BINARY ID=$CM_PLUGIN_ID SERVER=$CM_SERVER" ;;\nesac\n'


def zip_of(version):
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, 'w') as z:
        info = zipfile.ZipInfo('cm-agent')
        info.external_attr = 0o755 << 16
        z.writestr(info, FAKE.format(version=version))
    return buffer.getvalue()


class Server:
    """Serves {path: bytes} on the loopback and counts the requests."""

    def __init__(self, files):
        outer = self
        self.requests = []

        class Handler(http.server.BaseHTTPRequestHandler):
            def do_GET(self):
                outer.requests.append(self.path)
                body = files.get(self.path)
                self.send_response(200 if body is not None else 404)
                self.end_headers()
                if body is not None:
                    self.wfile.write(body)

            def log_message(self, *args):
                pass

        self.httpd = http.server.HTTPServer(('127.0.0.1', 0), Handler)
        self.url = f'http://127.0.0.1:{self.httpd.server_port}'
        threading.Thread(target=self.httpd.serve_forever, daemon=True).start()

    def close(self):
        self.httpd.shutdown()
        self.httpd.server_close()


@unittest.skipUnless(OS and ARCH, 'the scripts run on macOS and Linux here')
class PluginScripts(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix='cm-plugin-test-')
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.data = os.path.join(self.tmp, 'data')
        self.root = os.path.join(self.tmp, 'root')
        shutil.copytree(PLUGIN, self.root)
        self.bin = os.path.join(self.data, 'bin', 'cm-agent')
        self.log = os.path.join(self.data, 'install.log')

    def pins(self, version, archive=None, extra=''):
        digest = hashlib.sha256(archive if archive is not None else zip_of(version)).hexdigest()
        with open(os.path.join(self.root, 'agent-release.txt'), 'w') as f:
            f.write(f'{version}\n{OS}-{ARCH} {digest} {FILE}\n{extra}')

    def run_script(self, script, *args, server='', **extra):
        env = {'PATH': os.environ['PATH'], 'HOME': self.tmp, 'CLAUDE_PLUGIN_DATA': self.data, 'CLAUDE_PLUGIN_ROOT': self.root,
               'CLAUDE_PLUGIN_OPTION_SERVER': server, 'CLAUDE_CONFIG_DIR': os.path.join(self.tmp, 'claude')}
        env.update(extra)
        return subprocess.run(['sh', script, *args], env=env, capture_output=True, text=True, timeout=60)

    def wait_for(self, text):
        end = time.time() + 20
        while time.time() < end:
            if os.path.exists(self.log) and text in open(self.log).read():
                return True
            time.sleep(0.1)
        return False

    def install_fake(self, version):
        os.makedirs(os.path.dirname(self.bin), exist_ok=True)
        with open(self.bin, 'w') as f:
            f.write(FAKE.format(version=version))
        os.chmod(self.bin, 0o755)

    # ---- run.sh ----

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

    # ---- ensure.sh ----

    def test_a_missing_binary_is_downloaded_checked_installed_and_the_daemon_handed_over(self):
        server = Server({f'/downloads/{FILE}': zip_of('0.3.0')})
        self.addCleanup(server.close)
        self.pins('0.3.0')
        result = self.run_script(ENSURE, server=server.url)
        self.assertEqual(result.returncode, 0)
        self.assertEqual(json.loads(result.stdout)['systemMessage'].startswith('Claude Monitor: installing the agent (version 0.3.0)'), True)
        self.assertTrue(self.wait_for('installed version 0.3.0'), open(self.log).read() if os.path.exists(self.log) else 'no log')
        self.assertTrue(os.access(self.bin, os.X_OK))
        self.assertEqual(subprocess.run([self.bin, 'version'], capture_output=True, text=True).stdout.strip(), '0.3.0')
        restarts = os.path.join(self.data, 'bin', 'restarts')
        end = time.time() + 10
        while time.time() < end and not (os.path.exists(restarts) and not os.path.exists(os.path.join(self.data, 'install.lock'))):
            time.sleep(0.1)
        self.assertTrue(os.path.exists(restarts), 'the daemon was not handed over to the new binary')
        # The restarted daemon must know it belongs to the plugin, or it would never notice the plugin being switched off.
        self.assertIn(f'id=monitor-agent@monitor-agent-releases bin={self.bin}', open(restarts).read())
        self.assertFalse(os.path.exists(os.path.join(self.data, 'install.lock')))

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
        refused = self.run_script(ENSURE, server='http://monitor.invalid')
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
        for name in ('../../etc/passwd', 'cm-agent-../../x-y.zip', f'cm-agent-{OS}-{ARCH}.zip/../x'):
            with open(os.path.join(self.root, 'agent-release.txt'), 'w') as f:
                f.write(f'0.3.0\n{OS}-{ARCH} {"a" * 64} {name}\n')
            self.run_script(ENSURE, server=server.url)
            self.assertIn('is not a build file name', open(self.log).read(), name)
        self.assertEqual(server.requests, [])

    def test_a_server_address_that_only_starts_like_a_local_one_is_refused(self):
        self.pins('0.3.0')
        for address in ('http://localhost.attacker.example', 'http://127.0.0.1.attacker.example', 'http://localhost@attacker.example',
                        'http://localhostx:80', 'http://attacker.example', 'ftp://monitor.invalid', 'https://a b', "https://x'y", 'https://x$(id)'):
            result = self.run_script(ENSURE, server=address)
            self.assertIn('set the server address', json.loads(result.stdout)['systemMessage'], address)
            self.assertFalse(os.path.exists(os.path.join(self.data, 'server.txt')), address)
        for address in ('http://localhost', 'http://localhost:5080', 'http://127.0.0.1:9872/', 'https://monitor.invalid', 'https://monitor.invalid/base'):
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

    def test_a_long_install_log_is_cut_to_its_last_lines(self):
        os.makedirs(self.data, exist_ok=True)
        with open(self.log, 'w') as f:
            f.write(''.join(f'line {i} ' + 'x' * 100 + '\n' for i in range(3000)))
        self.run_script(ENSURE, server='https://monitor.invalid')
        lines = open(self.log).read().splitlines()
        self.assertLess(len(lines), 400)
        self.assertTrue(any('line 2999' in l for l in lines))

    def test_the_old_local_plugin_still_enabled_is_pointed_out(self):
        os.makedirs(os.path.join(self.tmp, 'claude'))
        with open(os.path.join(self.tmp, 'claude', 'settings.json'), 'w') as f:
            json.dump({'enabledPlugins': {'monitor-agent@monitor-agent-local': True}}, f)
        self.install_fake('0.3.0')
        self.pins('0.3.0')
        result = self.run_script(ENSURE, server='https://monitor.invalid')
        self.assertIn('old local plugin is still enabled', json.loads(result.stdout)['systemMessage'])

    def test_every_path_exits_0_even_without_the_plugin_variables(self):
        result = subprocess.run(['sh', ENSURE], env={'PATH': os.environ['PATH']}, capture_output=True, text=True, timeout=30)
        self.assertEqual((result.returncode, result.stdout), (0, ''))


class Pins(unittest.TestCase):
    SUMS = ('a' * 64 + '  cm-agent-macos-arm64.zip\n' + 'B' * 64 + '  cm-agent-windows-x64.zip\n' + 'c' * 63 + '  cm-agent-linux-x64.zip\n'
            + 'd' * 64 + '  other.zip\n')

    def test_only_well_formed_build_lines_become_pins_in_a_stable_order(self):
        found = pins_module.pins(self.SUMS)
        self.assertEqual(sorted(found), ['macos-arm64', 'windows-x64'])
        text = pins_module.render('1.2.3', pins_module.merge('1.2.3', found, ''))
        self.assertEqual(text.splitlines()[0], '1.2.3')
        self.assertEqual(text.splitlines()[1], 'macos-arm64 ' + 'a' * 64 + ' cm-agent-macos-arm64.zip')
        self.assertEqual(text.splitlines()[2], 'windows-x64 ' + 'b' * 64 + ' cm-agent-windows-x64.zip')

    def test_the_builds_of_one_version_accumulate_and_a_new_version_starts_over(self):
        test_build = pins_module.pins('1' * 64 + '  cm-agent-linux-x64.zip\n')
        prod_build = pins_module.pins('2' * 64 + '  cm-agent-linux-x64.zip\n')
        first = pins_module.render('1.2.3', pins_module.merge('1.2.3', test_build, ''))
        both = pins_module.render('1.2.3', pins_module.merge('1.2.3', prod_build, first))
        self.assertEqual(both.splitlines(), ['1.2.3', 'linux-x64 ' + '1' * 64 + ' cm-agent-linux-x64.zip', 'linux-x64 ' + '2' * 64 + ' cm-agent-linux-x64.zip'])
        again = pins_module.render('1.2.3', pins_module.merge('1.2.3', prod_build, both))
        self.assertEqual(again, both)  # writing the same build twice changes nothing
        newer = pins_module.render('1.3.0', pins_module.merge('1.3.0', prod_build, both))
        self.assertEqual(newer.splitlines(), ['1.3.0', 'linux-x64 ' + '2' * 64 + ' cm-agent-linux-x64.zip'])
        self.assertEqual(pins_module.existing('not a version\nx y z\n'), (None, {}))

    def test_a_bad_version_or_a_missing_sums_file_is_refused(self):
        with tempfile.TemporaryDirectory() as empty:
            self.assertEqual(pins_module.main(['x', empty, '1.2']), 2)
            self.assertEqual(pins_module.main(['x', empty, '1.2.3']), 2)  # no SHA256SUMS
            with open(os.path.join(empty, 'SHA256SUMS'), 'w') as f:
                f.write('nothing useful\n')
            self.assertEqual(pins_module.main(['x', empty, '1.2.3']), 2)


if __name__ == '__main__':
    unittest.main()
