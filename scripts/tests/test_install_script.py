"""scripts/install/aipod-install.sh: the installer and the removal script of the old agent, hermetically.

A private HOME, a download folder read through file://, and stubs for `claude` and `security` that keep their state in files: no real keychain,
Claude Code configuration, agent or server is touched, and no process outside the test's own folder is ever signalled.
"""
import hashlib
import importlib.util
import io
import json
import os
import shutil
import stat
import subprocess
import tempfile
import time
import unittest
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
SCRIPT = os.path.normpath(os.path.join(HERE, '..', 'install', 'aipod-install.sh'))
_spec = importlib.util.spec_from_file_location('plugin_support', os.path.join(HERE, 'plugin_support.py'))
support = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(support)
OS, ARCH, FILE = support.OS, support.ARCH, support.FILE
SERVER = 'https://aipod.example.net'

NEW_AGENT = r'''#!/bin/sh
# Fake aipod agent: logs what it is asked and keeps a fake identity in AP_AGENT_HOME.
home="${AP_AGENT_HOME:-$(dirname "$0")/..}"
echo "$*" >> "$home/calls"
case "$1" in
  version) echo "@VERSION@" ;;
  status) if grep -q agentId "$home/agent.json" 2>/dev/null; then echo "connected: $(sed -n 's/.*"server": *"\([^"]*\)".*/\1/p' "$home/agent.json")"; else echo "not connected"; fi ;;
  login) mkdir -p "$home"; printf '{"machineKey":"k","server":"%s","agentId":"11111111-1111-1111-1111-111111111111"}' "$3" > "$home/agent.json" ;;
esac
'''
OLD_AGENT = r'''#!/bin/sh
home="$(dirname "$0")/.."
case "$1" in
  version) echo "0.5.0" ;;
  uninstall) echo "$*" >> "$home/old-calls" ;;
  daemon) while [ ! -f "$home/daemon.stop" ]; do sleep 1; done ;;
esac
'''
CLAUDE = r'''#!/bin/sh
state="$STUB_STATE"
echo "$*" >> "$state/claude-calls"
touch "$state/plugins" "$state/marketplaces"
case "$1 $2 $3" in
  "plugin list --json") printf '[\n'; while read -r id; do printf '  {\n    "id": "%s",\n    "scope": "user"\n  },\n' "$id"; done < "$state/plugins"; printf '  {\n    "id": "other@other",\n    "scope": "user"\n  }\n]\n' ;;
  "plugin marketplace list") printf '[\n'; while read -r n; do printf '  {\n    "name": "%s",\n    "source": "url"\n  },\n' "$n"; done < "$state/marketplaces"; printf '  {\n    "name": "other",\n    "source": "github"\n  }\n]\n' ;;
  "plugin marketplace add") case "$4" in *aipod-marketplace.json) echo aipod-releases >> "$state/marketplaces" ;; esac ;;
  "plugin marketplace remove") grep -vx "$4" "$state/marketplaces" > "$state/m.tmp"; mv "$state/m.tmp" "$state/marketplaces" ;;
  "plugin install "*) echo "$3" >> "$state/plugins" ;;
  "plugin uninstall "*) grep -vx "$3" "$state/plugins" > "$state/p.tmp"; mv "$state/p.tmp" "$state/plugins" ;;
esac
exit 0
'''
SECURITY = r'''#!/bin/sh
kc="$STUB_STATE/kc"
mkdir -p "$kc"
case "$1" in
  -i) read -r line; eval "set -- $line"; shift; service=""; account=""; secret=""
      while [ $# -gt 0 ]; do case "$1" in -s) service="$2"; shift ;; -a) account="$2"; shift ;; -w) secret="$2"; shift ;; esac; shift; done
      printf '%s' "$secret" > "$kc/${service}__${account}" ;;
  find-generic-password) shift; service=""; account=""; want=0
      while [ $# -gt 0 ]; do case "$1" in -s) service="$2"; shift ;; -a) account="$2"; shift ;; -w) want=1 ;; esac; shift; done
      [ -f "$kc/${service}__${account}" ] || exit 44
      [ "$want" = 1 ] && cat "$kc/${service}__${account}" && echo ;;
  delete-generic-password) shift; while [ $# -gt 0 ]; do case "$1" in -s) service="$2"; shift ;; -a) account="$2"; shift ;; esac; shift; done
      rm -f "$kc/${service}__${account}" ;;
esac
exit 0
'''


def read(path):
    with open(path) as f:
        return f.read()


def zip_of(version):
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, 'w') as z:
        info = zipfile.ZipInfo('aipod')
        info.external_attr = 0o700 << 16
        z.writestr(info, NEW_AGENT.replace('@VERSION@', version))
    return buffer.getvalue()


@unittest.skipUnless(OS and ARCH, 'the script runs on macOS and Linux')
class InstallScript(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix='ap-install-test-')
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.home = os.path.join(self.tmp, 'home')
        self.state = os.path.join(self.tmp, 'state')
        self.stubs = os.path.join(self.tmp, 'stubs')
        self.dl = os.path.join(self.tmp, 'dl')
        for d in (self.home, self.state, self.stubs, self.dl):
            os.makedirs(d)
        for name, body in (('claude', CLAUDE), ('security', SECURITY)):
            self.write(os.path.join(self.stubs, name), body, 0o755)
        if OS == 'macos':
            self.new_home = os.path.join(self.home, 'Library', 'Application Support', 'aipod')
            self.old_home = os.path.join(self.home, 'Library', 'Application Support', 'ClaudeMonitor')
        else:
            self.new_home = os.path.join(self.home, '.aipod')
            self.old_home = os.path.join(self.home, '.claude-monitor')
        self.new_bin = os.path.join(self.new_home, 'bin', 'aipod')
        self.publish('0.5.1')

    def write(self, path, text, mode=0o600):
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, 'w') as f:
            f.write(text)
        os.chmod(path, mode)

    def publish(self, version, tamper=False):
        archive = zip_of(version)
        entries = [{'channel': 'prod', 'version': version, 'os': 'windows', 'arch': 'x64', 'file': 'aipod-windows-x64.zip', 'sha256': 'f' * 64},
                   {'channel': 'prod', 'version': version, 'os': OS, 'arch': ARCH, 'file': FILE, 'sha256': hashlib.sha256(archive).hexdigest()}]
        with open(os.path.join(self.dl, FILE), 'wb') as f:
            f.write(archive + (b'x' if tamper else b''))
        with open(os.path.join(self.dl, 'manifest.json'), 'w') as f:
            json.dump({'format': 'aipod-update/1', 'entries': entries}, f, indent=2)

    def old_agent(self, tokens=True, daemon=False, plugin=True):
        self.write(os.path.join(self.old_home, 'bin', 'cm-agent'), OLD_AGENT, 0o755)
        self.write(os.path.join(self.old_home, 'agent.json'),
                   json.dumps({'machineKey': 'k', 'server': SERVER, 'agentId': '22222222-2222-2222-2222-222222222222', 'push': False}))
        if tokens:
            for account, secret in (('access-token', 'old-access'), ('refresh-token', 'old-refresh')):
                if OS == 'macos':
                    self.write(os.path.join(self.state, 'kc', f'claude-monitor-agent__{account}'), secret)
                else:
                    self.write(os.path.join(self.old_home, f'cred-{account}'), secret)
        if plugin:
            self.write(os.path.join(self.state, 'plugins'), 'monitor-agent@monitor-agent-local\n')
            self.write(os.path.join(self.state, 'marketplaces'), 'monitor-agent-local\n')
        if daemon:
            return subprocess.Popen([os.path.join(self.old_home, 'bin', 'cm-agent'), 'daemon'], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    def run_script(self, *args, **extra):
        env = {'PATH': self.stubs + os.pathsep + os.environ['PATH'], 'HOME': self.home, 'STUB_STATE': self.state, 'TMPDIR': self.tmp,
               'AIPOD_INSTALL_UNSIGNED_OK': '1', 'CLAUDE_CONFIG_DIR': os.path.join(self.tmp, 'claude')}
        env.update(extra)
        return subprocess.run(['sh', SCRIPT, '--from', f'file://{self.dl}', *args], env=env, capture_output=True, text=True, timeout=120,
                              stdin=subprocess.DEVNULL, start_new_session=True)  # a new session: no terminal to ask on

    def calls(self, name='claude-calls'):
        path = os.path.join(self.state, name)
        return read(path) if os.path.exists(path) else ''

    def token(self, account):
        if OS == 'macos':
            path = os.path.join(self.state, 'kc', f'aipod__{account}')
        else:
            path = os.path.join(self.new_home, f'cred-{account}')
        return read(path).strip() if os.path.exists(path) else None

    def test_a_fresh_machine_gets_the_build_a_login_the_inbox_push_and_the_plugin(self):
        result = self.run_script('--server', SERVER)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual('0.5.1', subprocess.run([self.new_bin, 'version'], capture_output=True, text=True).stdout.strip())
        calls = read(os.path.join(self.new_home, 'calls'))
        self.assertIn(f'login --server {SERVER}', calls)
        self.assertIn('config push inbox', calls)
        claude = self.calls()
        self.assertIn(f'plugin marketplace add file://{self.dl}/aipod-marketplace.json', claude)
        self.assertIn(f'plugin install aipod@aipod-releases --config server={SERVER}', claude)

    def test_an_old_agent_is_kept_when_the_question_is_not_answered_yes(self):
        self.old_agent()
        result = self.run_script()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn('Nothing was changed', result.stdout)
        self.assertTrue(os.path.exists(os.path.join(self.old_home, 'bin', 'cm-agent')))
        self.assertFalse(os.path.exists(self.new_bin))
        self.assertNotIn('uninstall', self.calls())

    def test_an_old_agent_is_replaced_and_its_settings_and_login_carry_over(self):
        self.old_agent()
        result = self.run_script('--yes')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertFalse(os.path.exists(os.path.join(self.old_home, 'bin', 'cm-agent')))
        identity = json.loads(read(os.path.join(self.new_home, 'agent.json')))
        self.assertEqual('22222222-2222-2222-2222-222222222222', identity['agentId'])  # the same machine on the web
        self.assertEqual('old-access', self.token('access-token'))
        self.assertEqual('old-refresh', self.token('refresh-token'))
        agent_calls = read(os.path.join(self.new_home, 'calls'))
        self.assertNotIn('login', agent_calls)  # already connected: no new code
        self.assertNotIn('config push', agent_calls)  # a saved choice is never touched
        claude = self.calls()
        self.assertIn('plugin uninstall monitor-agent@monitor-agent-local', claude)
        self.assertIn('plugin marketplace remove monitor-agent-local', claude)
        self.assertIn('plugin install aipod@aipod-releases', claude)
        self.assertTrue(os.path.isdir(self.old_home), 'the old data folder stays unless --purge')

    def test_purge_deletes_the_old_data_after_the_carry_over(self):
        self.old_agent()
        result = self.run_script('--yes', '--purge')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertFalse(os.path.exists(self.old_home))
        self.assertEqual('old-access', self.token('access-token'))
        if OS == 'macos':
            self.assertFalse(os.path.exists(os.path.join(self.state, 'kc', 'claude-monitor-agent__access-token')))

    def test_remove_old_alone_removes_and_installs_nothing(self):
        self.old_agent()
        result = self.run_script('--remove-old', '--yes')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertFalse(os.path.exists(os.path.join(self.old_home, 'bin', 'cm-agent')))
        self.assertFalse(os.path.exists(self.new_bin))
        self.assertNotIn('install aipod', self.calls())

    def test_remove_old_with_nothing_old_says_so(self):
        result = self.run_script('--remove-old', '--yes')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn('No old agent found', result.stdout)

    def test_the_old_daemon_is_stopped_before_its_binary_goes(self):
        process = self.old_agent(daemon=True)
        self.addCleanup(process.kill)
        self.write(os.path.join(self.old_home, 'daemon.pid'), str(process.pid))
        result = self.run_script('--remove-old', '--yes')
        self.assertEqual(0, result.returncode, result.stderr)
        end = time.time() + 15
        while process.poll() is None and time.time() < end:
            time.sleep(0.2)
        self.assertIsNotNone(process.poll(), 'the old daemon is still running')

    def test_a_download_that_does_not_match_the_manifest_is_refused(self):
        self.publish('0.5.1', tamper=True)
        result = self.run_script('--server', SERVER)
        self.assertNotEqual(0, result.returncode)
        self.assertIn('does not match the SHA-256', result.stderr)
        self.assertFalse(os.path.exists(self.new_bin))

    def test_an_installed_build_that_is_current_is_not_downloaded_again_and_its_plugin_is_updated(self):
        self.write(self.new_bin, NEW_AGENT.replace('@VERSION@', '0.5.1'), 0o700)
        self.write(os.path.join(self.new_home, 'agent.json'), json.dumps({'machineKey': 'k', 'server': SERVER, 'agentId': '3'}))
        self.write(os.path.join(self.state, 'plugins'), 'aipod@aipod-releases\n')
        self.write(os.path.join(self.state, 'marketplaces'), 'aipod-releases\n')
        os.remove(os.path.join(self.dl, FILE))  # a download would fail
        result = self.run_script()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn('is the newest build', result.stdout)
        self.assertIn('plugin update aipod@aipod-releases', self.calls())
        self.assertIn('plugin marketplace update aipod-releases', self.calls())

    def test_an_older_installed_build_is_updated_and_the_login_stays(self):
        self.write(self.new_bin, NEW_AGENT.replace('@VERSION@', '0.5.0'), 0o700)
        self.write(os.path.join(self.new_home, 'agent.json'), json.dumps({'machineKey': 'k', 'server': SERVER, 'agentId': '3'}))
        result = self.run_script()  # the question defaults to yes
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual('0.5.1', subprocess.run([self.new_bin, 'version'], capture_output=True, text=True).stdout.strip())
        self.assertNotIn('login', read(os.path.join(self.new_home, 'calls')))

    def test_a_dry_run_changes_nothing(self):
        self.old_agent()
        result = self.run_script('--yes', '--dry-run')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn('[dry run]', result.stdout)
        self.assertIn('[dry run] claude plugin uninstall monitor-agent@monitor-agent-local', result.stdout)  # what would happen is shown
        self.assertIn('[dry run] claude plugin install aipod@aipod-releases', result.stdout)
        self.assertTrue(os.path.exists(os.path.join(self.old_home, 'bin', 'cm-agent')))
        self.assertFalse(os.path.exists(self.new_home))
        self.assertEqual('', self.calls().replace('plugin list --json\n', '').replace('plugin marketplace list --json\n', ''))

    def test_a_server_that_is_not_https_is_refused(self):
        result = self.run_script('--server', 'http://example.org')
        self.assertNotEqual(0, result.returncode)
        self.assertIn('must be https', result.stderr)

    def test_the_script_is_executable_shell_that_the_help_describes(self):
        self.assertTrue(os.stat(SCRIPT).st_mode & stat.S_IXUSR)
        result = self.run_script('--help')
        self.assertEqual(0, result.returncode)
        self.assertIn('--remove-old', result.stdout)


if __name__ == '__main__':
    unittest.main()
