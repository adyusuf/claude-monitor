"""scripts/agent_smoke.py: the published daemon must stay up and reach the API.

The checks are run against stand-in "binaries" (shell scripts), so no dotnet publish is needed: what is under test is
the script's verdict — a daemon that dies, one that idles without calling the API, and one that behaves.
"""
import contextlib
import importlib.util
import io
import os
import platform
import shutil
import stat
import tempfile
import textwrap
import unittest
from unittest import mock

SCRIPT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'agent_smoke.py'))
spec = importlib.util.spec_from_file_location('agent_smoke', SCRIPT)
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)

# A stand-in daemon that does what the real one does first: read its identity from CM_AGENT_HOME and call the API.
HEALTHY = textwrap.dedent('''\
    import json, os, time, urllib.request
    home = os.environ['CM_AGENT_HOME']
    identity = json.load(open(os.path.join(home, 'agent.json')))
    assert os.environ['CM_CREDENTIALS'] == 'file'
    token = open(os.path.join(home, 'cred-access-token')).read()
    assert '\\n' not in token and token
    base = identity['server']
    urllib.request.urlopen(urllib.request.Request(base + '/api/agent/heartbeat', data=b'', method='POST')).read()
    urllib.request.urlopen(base + '/api/agent/settings').read()
    stream = urllib.request.urlopen(base + '/api/agent/stream')
    stream.readline()
    time.sleep(60)
    ''')


@unittest.skipIf(platform.system() == 'Windows', 'the stand-ins are shell scripts')
class Smoke(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.dir, True)

    def binary(self, body):
        path = os.path.join(self.dir, f'cm-agent-{len(os.listdir(self.dir))}')
        with open(path, 'w', encoding='utf-8') as handle:
            handle.write(body)
        os.chmod(path, os.stat(path).st_mode | stat.S_IXUSR)
        return path

    def python_binary(self, source):
        script = os.path.join(self.dir, 'daemon.py')
        with open(script, 'w', encoding='utf-8') as handle:
            handle.write(source)
        return self.binary(f'#!/bin/sh\nexec python3 "{script}"\n')

    def test_a_daemon_that_dies_fails_with_its_exit_code_and_message(self):
        binary = self.binary('#!/bin/sh\necho "Fatal error. AccessViolation" >&2\nexit 134\n')
        problems, _ = smoke.run_daemon(binary, 5)
        self.assertTrue(any('exited' in p and '134' in p and 'AccessViolation' in p for p in problems), problems)

    def test_a_daemon_that_never_calls_the_api_fails(self):
        binary = self.binary('#!/bin/sh\nexec sleep 60\n')
        problems, calls = smoke.run_daemon(binary, 1)
        self.assertEqual({}, calls)
        self.assertEqual(3, len(problems), problems)
        self.assertFalse(any('exited' in p for p in problems), problems)

    def test_a_daemon_that_stays_up_and_calls_the_api_passes(self):
        problems, calls = smoke.run_daemon(self.python_binary(HEALTHY), 2)
        self.assertEqual([], problems)
        self.assertEqual({'heartbeat', 'settings', 'stream'}, set(calls))

    def test_the_fake_api_answers_unknown_paths_with_404_and_takes_batches(self):
        import json
        import urllib.error
        import urllib.request
        api = smoke.FakeApi()
        import threading
        threading.Thread(target=api.serve_forever, daemon=True).start()
        self.addCleanup(api.server_close)
        self.addCleanup(api.shutdown)
        base = f'http://127.0.0.1:{api.server_address[1]}'
        with self.assertRaises(urllib.error.HTTPError) as caught:
            urllib.request.urlopen(base + '/nope')
        self.assertEqual(404, caught.exception.code)
        batch = urllib.request.urlopen(urllib.request.Request(base + '/api/agent/batches', data=b'{}', method='POST'))
        self.assertEqual(False, json.load(batch)['duplicate'])
        other = urllib.request.urlopen(urllib.request.Request(base + '/api/agent/commands/x/status', data=b'{}', method='POST'))
        self.assertEqual(200, other.status)
        self.assertEqual(1, api.seen('batches'))

    def run_main(self, *args):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = smoke.main(list(args))
        return code, out.getvalue()

    def test_main_passes_and_fails_on_the_verdict(self):
        code, output = self.run_main('--binary', self.python_binary(HEALTHY), '--seconds', '2')
        self.assertEqual(0, code, output)
        self.assertIn('passed', output)
        code, output = self.run_main('--binary', self.binary('#!/bin/sh\nexit 134\n'), '--seconds', '2')
        self.assertEqual(1, code)
        self.assertIn('FAILED', output)

    def test_main_is_not_run_on_a_host_the_agent_does_not_ship_for(self):
        with mock.patch.object(smoke, 'host_rid', return_value=None):
            code, output = self.run_main()
        self.assertEqual(3, code)
        self.assertIn('NOT RUN', output)

    def test_main_is_not_run_without_dotnet(self):
        with mock.patch.object(smoke, 'host_rid', return_value='osx-arm64'), mock.patch.object(smoke.shutil, 'which', return_value=None):
            code, output = self.run_main()
        self.assertEqual(3, code)
        self.assertIn('dotnet is not installed', output)

    def test_main_fails_when_the_publish_fails_and_tests_what_it_published(self):
        with mock.patch.object(smoke, 'host_rid', return_value='osx-arm64'), \
                mock.patch.object(smoke.shutil, 'which', return_value='/usr/bin/dotnet'):
            with mock.patch.object(smoke, 'publish', return_value=None):
                code, output = self.run_main()
            self.assertEqual(1, code)
            self.assertIn('dotnet publish failed', output)
            with mock.patch.object(smoke, 'publish', return_value=self.python_binary(HEALTHY)):
                code, output = self.run_main('--seconds', '2')
            self.assertEqual(0, code, output)

    def test_host_rid_names_the_platforms_the_agent_ships_for(self):
        def rid(system, machine):
            with mock.patch.object(smoke.platform, 'system', return_value=system), \
                    mock.patch.object(smoke.platform, 'machine', return_value=machine):
                return smoke.host_rid()

        self.assertEqual('osx-arm64', rid('Darwin', 'arm64'))
        self.assertEqual('osx-x64', rid('Darwin', 'x86_64'))
        self.assertEqual('win-x64', rid('Windows', 'AMD64'))
        self.assertEqual('win-arm64', rid('Windows', 'ARM64'))
        self.assertIsNone(rid('Linux', 'x86_64'))
        self.assertIsNone(rid('Darwin', 'ppc'))

    def test_publish_runs_the_shipped_command_and_names_the_binary(self):
        ran = []

        def fake_run(command, **_):
            ran.append(command)
            return mock.Mock(returncode=0, stdout='', stderr='')

        with mock.patch.object(smoke.subprocess, 'run', fake_run):
            self.assertEqual(os.path.join('out', 'cm-agent'), smoke.publish('osx-arm64', 'out'))
            self.assertEqual(os.path.join('out', 'cm-agent.exe'), smoke.publish('win-x64', 'out'))
        self.assertIn('Release', ran[0])
        self.assertIn('osx-arm64', ran[0])
        with mock.patch.object(smoke.subprocess, 'run', return_value=mock.Mock(returncode=1, stdout='boom', stderr='')), \
                contextlib.redirect_stdout(io.StringIO()):
            self.assertIsNone(smoke.publish('osx-arm64', 'out'))


if __name__ == '__main__':
    unittest.main()
