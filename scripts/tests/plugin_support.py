"""Shared fixtures of the plugin script tests (ADR-0007): a fake agent, a loopback download server and the base test case.

Everything is hermetic: a fake agent (a shell script) is zipped and served by a throw-away HTTP server on the loopback, and the scripts run with
a private HOME and plugin data directory. No real Claude Code, agent, server or network is touched.
"""
import hashlib
import http.server
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
AIPOD = os.path.normpath(os.path.join(HERE, '..', '..', 'plugin', 'aipod'))
RUN, ENSURE = os.path.join(PLUGIN, 'scripts', 'run.sh'), os.path.join(PLUGIN, 'scripts', 'ensure.sh')

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
FILE = f'aipod-{OS}-{ARCH}.zip'
FAKE = '#!/bin/sh\ncase "$1" in\n  version) echo "{version}" ;;\n  restart) echo "restarted id=$AP_PLUGIN_ID bin=$AP_BINARY push=$AP_PUSH server=$AP_SERVER" >> "$(dirname "$0")/restarts" ;;\n  *) echo "args: $*"; echo "BIN=$AP_BINARY ID=$AP_PLUGIN_ID SERVER=$AP_SERVER" ;;\nesac\n'


def zip_of(version):
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, 'w') as z:
        info = zipfile.ZipInfo('aipod')
        info.external_attr = 0o700 << 16
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


class PluginScriptsCase(unittest.TestCase):
    plugin_folder = PLUGIN  # the aipod plugin's cases set AIPOD

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix='ap-plugin-test-')
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.data = os.path.join(self.tmp, 'data')
        self.root = os.path.join(self.tmp, 'root')
        shutil.copytree(self.plugin_folder, self.root)
        self.bin = os.path.join(self.data, 'bin', 'aipod')
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
        os.chmod(self.bin, 0o700)
