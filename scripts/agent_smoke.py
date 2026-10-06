#!/usr/bin/env python3
"""Smoke test of the PUBLISHED agent binary: its daemon must stay up and talk to an API.

The unit tests run the agent on the JIT, so they never run the code `dotnet publish -c Release` ships (single file,
ReadyToRun). That gap hid a crash: with `EnableCompressionInSingleFile` on, the published daemon died on macOS within
seconds with System.AccessViolationException inside System.Net.Http (docs/adr-0002-agent-platform.md, "Published build").

This script publishes the agent for the host the way scripts/build-agent.sh does (or takes --binary), starts
`cm-agent daemon` against a fake API on 127.0.0.1 in a throw-away home (file credentials, never the user's home,
keychain or Claude Code configuration), lets it run, and checks that:
  1. the process is still alive after --seconds, and
  2. it reached the API: a heartbeat, the settings call and the open event stream.

Exit codes: 0 passed, 1 failed, 3 not run (the host is not one the agent ships for, or dotnet is missing).
Usage: python3 scripts/agent_smoke.py [--binary PATH] [--seconds N]
"""
import argparse
import http.server
import json
import os
import platform
import secrets
import shutil
import subprocess
import sys
import tempfile
import threading
import time

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
PROJECT = os.path.join('src', 'ClaudeMonitor.Agent')
WORKSPACE = '00000000-0000-0000-0000-000000000001'
AGENT = '00000000-0000-0000-0000-000000000002'


class FakeApi(http.server.ThreadingHTTPServer):
    """Answers what the daemon asks for and counts the calls. The stream stays open and pings, like the real one."""

    daemon_threads = True

    def __init__(self):
        super().__init__(('127.0.0.1', 0), _Handler)
        self.calls = {}
        self.lock = threading.Lock()

    def count(self, key):
        with self.lock:
            self.calls[key] = self.calls.get(key, 0) + 1

    def seen(self, key):
        with self.lock:
            return self.calls.get(key, 0)


class _Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'

    def log_message(self, *_):
        pass

    def reply(self, body, status=200):
        data = json.dumps(body).encode()
        self.send_response(status)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if self.path.startswith('/api/agent/settings'):
            self.server.count('settings')
            self.reply({'maskSecrets': True, 'eventMaxBytes': 262144, 'workspaceId': WORKSPACE})
        elif self.path.startswith('/api/agent/stream'):
            self.server.count('stream')
            self.send_response(200)
            self.send_header('Content-Type', 'text/event-stream')
            self.send_header('Transfer-Encoding', 'chunked')
            self.end_headers()
            try:
                while True:
                    chunk = b'event: ping\ndata: {}\n\n'
                    self.wfile.write(b'%x\r\n%s\r\n' % (len(chunk), chunk))
                    self.wfile.flush()
                    time.sleep(1)
            except OSError:
                pass  # the daemon went away
        else:
            self.reply({}, 404)

    def do_POST(self):
        self.rfile.read(int(self.headers.get('Content-Length') or 0))
        if self.path.startswith('/api/agent/heartbeat'):
            self.server.count('heartbeat')
            self.reply({})
        elif self.path.startswith('/api/agent/batches'):
            self.server.count('batches')
            self.reply({'batchSeq': 0, 'duplicate': False, 'stored': 0})
        else:
            self.reply({})


def host_rid():
    arch = {'arm64': 'arm64', 'aarch64': 'arm64', 'x86_64': 'x64', 'amd64': 'x64'}.get(platform.machine().lower())
    system = {'Darwin': 'osx', 'Windows': 'win', 'Linux': 'linux'}.get(platform.system())
    return f'{system}-{arch}' if system and arch else None


def publish(rid, out):
    """The shipped configuration: scripts/build-agent.sh publishes with exactly this command line."""
    command = ['dotnet', 'publish', os.path.join(ROOT, PROJECT), '-c', 'Release', '-r', rid, '-o', out, '--nologo', '-v', 'q']
    done = subprocess.run(command, capture_output=True, text=True)
    if done.returncode != 0:
        print(done.stdout[-2000:] + done.stderr[-2000:])
        return None
    name = 'cm-agent.exe' if rid.startswith('win') else 'cm-agent'
    return os.path.join(out, name)


def write_home(home, port):
    identity = {'machineKey': secrets.token_hex(24), 'server': f'http://127.0.0.1:{port}',
                'agentId': AGENT, 'workspaceId': WORKSPACE}
    with open(os.path.join(home, 'agent.json'), 'w', encoding='utf-8') as handle:
        json.dump(identity, handle)
    for account in ('access-token', 'refresh-token'):  # no trailing newline: the value goes into a header
        with open(os.path.join(home, 'cred-' + account), 'w', encoding='utf-8') as handle:
            handle.write('smoke-' + account)


def run_daemon(binary, seconds):
    """Returns (problems, calls): what is wrong with the daemon after `seconds`, and what the fake API saw."""
    api = FakeApi()
    threading.Thread(target=api.serve_forever, daemon=True).start()
    home = tempfile.mkdtemp(prefix='cm-agent-smoke-')
    try:
        write_home(home, api.server_address[1])
        env = dict(os.environ, CM_AGENT_HOME=home, CM_CREDENTIALS='file')
        child = subprocess.Popen([binary, 'daemon'], env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        try:
            code = child.wait(timeout=seconds)
        except subprocess.TimeoutExpired:
            code = None
            child.terminate()
        _, errors = child.communicate(timeout=30)
        problems = []
        if code is not None:
            problems.append(f'the daemon exited after less than {seconds} s with code {code}: {errors.strip()[:600]}')
        for key in ('heartbeat', 'settings', 'stream'):
            if api.seen(key) == 0:
                problems.append(f'the daemon never reached the API ({key} was not called)')
        return problems, dict(api.calls)
    finally:
        api.shutdown()
        api.server_close()
        shutil.rmtree(home, ignore_errors=True)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    parser.add_argument('--binary', help='the published cm-agent to test; default: publish the host build')
    parser.add_argument('--seconds', type=int, default=10)
    args = parser.parse_args(argv)

    out = None
    binary = args.binary
    try:
        if binary is None:
            rid = host_rid()
            if rid is None:
                print('agent smoke: NOT RUN — the agent ships for macOS, Windows and Linux only')
                return 3
            if shutil.which('dotnet') is None:
                print('agent smoke: NOT RUN — dotnet is not installed')
                return 3
            out = tempfile.mkdtemp(prefix='cm-agent-publish-')
            print(f'agent smoke: publishing {rid} (Release)')
            binary = publish(rid, out)
            if binary is None:
                print('agent smoke: FAILED — dotnet publish failed')
                return 1
        problems, calls = run_daemon(binary, args.seconds)
    finally:
        if out:
            shutil.rmtree(out, ignore_errors=True)
    if problems:
        print('agent smoke: FAILED')
        for problem in problems:
            print('  - ' + problem)
        return 1
    print(f'agent smoke: passed — the daemon stayed up {args.seconds} s and called the API {calls}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
