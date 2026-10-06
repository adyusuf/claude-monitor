#!/usr/bin/env python3
"""End-to-end test of the agent's self-update with REAL published binaries and a fake server (ADR-0004).

Runs on the host (macOS or Windows): it publishes the agent twice (0.3.0 and 0.3.1) with a throw-away update key built in
(channel "test"), installs 0.3.0 into a throw-away home (file credentials; never the user's home, keychain or Claude Code
configuration), starts its daemon against a fake API on 127.0.0.1 and then checks four things:

  1. an update signed with the right key is downloaded and installed: the binary is replaced, the daemon restarts and
     `cm-agent status` reports 0.3.1;
  2. a manifest signed with another key changes nothing and `cm-agent status` shows why (bad-signature);
  3. a download whose bytes differ from the signed hash changes nothing (hash-mismatch);
  4. a new daemon that the API never answers is rolled back to 0.3.0 (the fake API refuses 0.3.1's heartbeats).

Exit codes: 0 passed, 1 failed, 3 not run (unsupported host, no dotnet, or no openssl).
Usage: python3 scripts/update_smoke.py
"""
import argparse
import base64
import http.server
import importlib.util
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
import zipfile

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
PROJECT = os.path.join(ROOT, 'src', 'ClaudeMonitor.Agent')
WORKSPACE = '00000000-0000-0000-0000-000000000001'
AGENT = '00000000-0000-0000-0000-000000000002'
spec = importlib.util.spec_from_file_location('sign_manifest', os.path.join(ROOT, 'scripts', 'sign_manifest.py'))
sm = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sm)


class Server(http.server.ThreadingHTTPServer):
    daemon_threads = True

    def __init__(self):
        super().__init__(('127.0.0.1', 0), Handler)
        self.manifest = {}       # (os, arch) -> entry
        self.zips = {}           # file name -> bytes
        self.refuse_heartbeat_from = None   # an agent version whose heartbeats get a 500
        self.heartbeats = []     # the X-Agent-Version of every answered heartbeat

    @property
    def origin(self):
        return f'http://127.0.0.1:{self.server_address[1]}'


class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'

    def log_message(self, *_):
        pass

    def send(self, body, status=200, kind='application/json'):
        data = body if isinstance(body, bytes) else json.dumps(body).encode()
        self.send_response(status)
        self.send_header('Content-Type', kind)
        self.send_header('Content-Length', str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        path = self.path.split('?')[0]
        if path == '/api/agent/settings':
            self.send({'maskSecrets': True, 'eventMaxBytes': 262144, 'workspaceId': WORKSPACE, 'agentUpdate': 'off'})
        elif path == '/api/agent/latest':
            query = dict(p.split('=') for p in self.path.split('?', 1)[1].split('&'))
            entry = self.server.manifest.get((query['os'], query['arch']))
            if entry is None:
                self.send({'title': 'no_update'}, 404)
            else:
                self.send({'version': entry['version'], 'url': f'{self.server.origin}/downloads/{entry["file"]}', 'sha256': entry['sha256'],
                           'signature': entry['signature'], 'minSupported': entry['minSupported'], 'channel': entry['channel']})
        elif path.startswith('/downloads/') and path[len('/downloads/'):] in self.server.zips:
            self.send(self.server.zips[path[len('/downloads/'):]], kind='application/zip')
        elif path == '/api/agent/stream':
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
                pass
        else:
            self.send({}, 404)

    def do_POST(self):
        self.rfile.read(int(self.headers.get('Content-Length') or 0))
        if self.path.startswith('/api/agent/heartbeat'):
            version = self.headers.get('X-Agent-Version')
            if version == self.server.refuse_heartbeat_from:
                self.send({'title': 'refused'}, 500)
            else:
                self.server.heartbeats.append(version)
                self.send({})
        elif self.path.startswith('/api/agent/batches'):
            self.send({'batchSeq': 0, 'duplicate': False, 'stored': 0})
        else:
            self.send({})


def host():
    arch = {'arm64': 'arm64', 'aarch64': 'arm64', 'x86_64': 'x64', 'amd64': 'x64'}.get(platform.machine().lower())
    system = {'Darwin': 'osx', 'Windows': 'win'}.get(platform.system())
    return (system, arch) if system and arch else None


def publish(rid, out, version, public_key):
    command = ['dotnet', 'publish', PROJECT, '-c', 'Release', '-r', rid, '-o', out, '--nologo', '-v', 'q',
               f'-p:Version={version}', '-p:UpdateChannel=test', f'-p:UpdatePublicKey={public_key}']
    done = subprocess.run(command, capture_output=True, text=True)
    if done.returncode != 0:
        print(done.stdout[-2000:] + done.stderr[-2000:])
        return None
    return os.path.join(out, 'cm-agent.exe' if rid.startswith('win') else 'cm-agent')


def agent(binary, home, *args, timeout=240):
    env = dict(os.environ, CM_AGENT_HOME=home, CM_CREDENTIALS='file', CM_UPDATE_HEALTH_WAIT='20')
    done = subprocess.run([binary, *args], env=env, capture_output=True, text=True, timeout=timeout)
    return done.returncode, done.stdout + done.stderr


def start_daemon(binary, home):
    env = dict(os.environ, CM_AGENT_HOME=home, CM_CREDENTIALS='file')
    return subprocess.Popen([binary, 'daemon'], env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def wait_for(condition, seconds):
    until = time.time() + seconds
    while time.time() < until:
        if condition():
            return True
        time.sleep(0.5)
    return False


def zip_binary(binary, target):
    with zipfile.ZipFile(target, 'w', zipfile.ZIP_DEFLATED) as z:
        z.write(binary, os.path.basename(binary))
    with open(target, 'rb') as f:
        return f.read()


def run(installed, offered, system, arch, key, public):
    problems = []
    os_name = 'macos' if system == 'osx' else 'windows'
    work = tempfile.mkdtemp(prefix='cm-update-smoke-')
    server = Server()
    threading.Thread(target=server.serve_forever, daemon=True).start()
    home = os.path.join(work, 'home')
    os.makedirs(os.path.join(home, 'bin'))
    bin_path = os.path.join(home, 'bin', os.path.basename(installed))
    shutil.copy2(installed, bin_path)
    with open(os.path.join(home, 'agent.json'), 'w', encoding='utf-8') as f:
        json.dump({'machineKey': secrets.token_hex(24), 'server': server.origin, 'agentId': AGENT, 'workspaceId': WORKSPACE}, f)
    for account in ('access-token', 'refresh-token'):
        with open(os.path.join(home, 'cred-' + account), 'w', encoding='utf-8') as f:
            f.write('smoke-' + account)

    def publish_offer(signing_key, tamper=False):
        folder = os.path.join(work, 'downloads')
        shutil.rmtree(folder, ignore_errors=True)
        os.makedirs(folder)
        name = f'cm-agent-{os_name}-{arch}.zip'
        data = zip_binary(offered, os.path.join(folder, name))
        manifest = sm.build_manifest('test', folder, '0.3.1', '0.3.0', signing_key)
        entry = manifest['entries'][0]
        server.manifest = {(os_name, arch): entry}
        server.zips = {name: data + b'x' if tamper else data}

    daemon = start_daemon(bin_path, home)
    try:
        if not wait_for(lambda: '0.3.0' in server.heartbeats, 40):
            return ['the installed 0.3.0 daemon never reached the fake API']

        # 1. a good update
        publish_offer(key)
        code, text = agent(bin_path, home, 'update')
        print(f'  update: exit {code}: {text.strip()[:300]}')
        if code != 0:
            problems.append(f'1: `cm-agent update` failed: {text.strip()[:400]}')
        if not wait_for(lambda: '0.3.1' in server.heartbeats, 60):
            problems.append('1: the restarted daemon (0.3.1) never reached the fake API')
        code, text = agent(bin_path, home, 'version')
        if text.strip() != '0.3.1':
            problems.append(f'1: the installed binary reports "{text.strip()}", not 0.3.1')
        code, status = agent(bin_path, home, 'status')
        print('  status after the update:\n    ' + status.strip().replace('\n', '\n    '))
        if 'version: 0.3.1' not in status or 'daemon: running' not in status:
            problems.append('1: `cm-agent status` does not show 0.3.1 with a running daemon')
        # bring the home back to 0.3.0 for the refusal cases
        agent(bin_path, home, 'update', '--check')
        stop_daemon(home)
        wait_for(lambda: agent(bin_path, home, 'status')[1].count('daemon: not running') > 0, 30)
        shutil.copy2(installed, bin_path)
        for leftover in ('.prev', '.new', '.bad'):
            if os.path.exists(bin_path + leftover):
                os.remove(bin_path + leftover)
        daemon = start_daemon(bin_path, home)
        server.heartbeats.clear()
        wait_for(lambda: '0.3.0' in server.heartbeats, 40)

        # 2. wrong signing key
        publish_offer(base64.b64encode(sm.openssl(['ecparam', '-name', 'prime256v1', '-genkey', '-noout', '-outform', 'DER'])).decode())
        code, text = agent(bin_path, home, 'update')
        if code == 0 or agent(bin_path, home, 'version')[1].strip() != '0.3.0':
            problems.append(f'2: a bad signature was not refused or changed the binary: {text.strip()[:300]}')
        status = agent(bin_path, home, 'status')[1]
        if 'bad-signature' not in status:
            problems.append('2: `cm-agent status` does not show the bad signature')

        # 3. the download differs from the signed hash
        publish_offer(key, tamper=True)
        code, text = agent(bin_path, home, 'update')
        if code == 0 or agent(bin_path, home, 'version')[1].strip() != '0.3.0' or 'hash' not in text:
            problems.append(f'3: a download with the wrong hash was not refused: {text.strip()[:300]}')

        # 4. the new daemon is not answered: rolled back
        publish_offer(key)
        server.refuse_heartbeat_from = '0.3.1'
        code, text = agent(bin_path, home, 'update', timeout=240)
        print(f'  rollback case: exit {code}: {text.strip()[:300]}')
        if code == 0 or agent(bin_path, home, 'version')[1].strip() != '0.3.0':
            problems.append(f'4: the unhealthy 0.3.1 was not rolled back: {text.strip()[:300]}')
        if 'rolled back' not in agent(bin_path, home, 'status')[1]:
            problems.append('4: `cm-agent status` does not show the rollback')
    finally:
        stop_daemon(home)
        time.sleep(2)
        daemon.terminate()
        server.shutdown()
        server.server_close()
        shutil.rmtree(work, ignore_errors=True)
    return problems


def stop_daemon(home):
    with open(os.path.join(home, 'daemon.stop'), 'w', encoding='utf-8') as f:
        f.write('smoke')


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    parser.add_argument('--installed', help='skip the publish: a cm-agent 0.3.0 built with the update key of this run (only useful when debugging)')
    parser.add_argument('--offered', help='skip the publish: the cm-agent 0.3.1 to offer')
    args = parser.parse_args(argv)
    target = host()
    if target is None or shutil.which('dotnet') is None or shutil.which('openssl') is None:
        print('update smoke: NOT RUN — needs macOS or Windows, dotnet and openssl')
        return 3
    system, arch = target
    key = base64.b64encode(sm.openssl(['ecparam', '-name', 'prime256v1', '-genkey', '-noout', '-outform', 'DER'])).decode()
    public = sm.public_key(key)
    out = tempfile.mkdtemp(prefix='cm-update-publish-')
    try:
        installed, offered = args.installed, args.offered
        if installed is None or offered is None:
            print(f'update smoke: publishing {system}-{arch} twice (0.3.0 and 0.3.1)')
            installed = publish(f'{system}-{arch}', os.path.join(out, 'a'), '0.3.0', public)
            offered = publish(f'{system}-{arch}', os.path.join(out, 'b'), '0.3.1', public)
            if installed is None or offered is None:
                print('update smoke: FAILED — dotnet publish failed')
                return 1
        problems = run(installed, offered, system, arch, key, public)
    finally:
        shutil.rmtree(out, ignore_errors=True)
    if problems:
        print('update smoke: FAILED')
        for p in problems:
            print('  - ' + p)
        return 1
    print('update smoke: passed — a signed 0.3.1 was installed and the daemon restarted; a bad signature, a wrong hash and an unhealthy build changed nothing')
    return 0


if __name__ == '__main__':
    sys.exit(main())
