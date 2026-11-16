"""scripts/build-agent.sh with stub tools: the six targets, the missing-channel-key stop, pins before the signed manifest, the signing branch (F-330)."""
import os
import shutil
import stat
import subprocess
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
SCRIPT = os.path.join(HERE, '..', 'build-agent.sh')
ASSEMBLE = os.path.join(HERE, '..', 'install', 'assemble.sh')
TARGETS = {
    'osx-arm64': 'aipod-macos-arm64', 'osx-x64': 'aipod-macos-x64', 'win-x64': 'aipod-windows-x64',
    'win-arm64': 'aipod-windows-arm64', 'linux-x64': 'aipod-linux-x64', 'linux-arm64': 'aipod-linux-arm64',
}
VERSION = '9.8.7'

# Each stub appends one line "<tool> <args>" to $CALLS; dotnet leaves a binary where `-o` points (aipod.exe for Windows), zip writes the zip.
STUB = '#!/usr/bin/env bash\nprintf "%s %s\\n" "$(basename "$0")" "$*" >> "$CALLS"\n'
DOTNET = STUB + '''rid=""; o=""
while [ $# -gt 0 ]; do case "$1" in -r) rid="$2"; shift ;; -o) o="$2"; shift ;; esac; shift; done
mkdir -p "$o"; name=aipod; case "$rid" in win-*) name=aipod.exe ;; esac; printf '%s' "$rid" > "$o/$name"
'''
ZIP = STUB + 'printf "zip" > "$3"\n'
PYTHON_STUB = 'import os, sys\nwith open(os.environ["CALLS"], "a") as h:\n    h.write("{name} " + " ".join(sys.argv[1:]) + "\\n")\n'


def write(path, text, executable=False):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'w') as handle:
        handle.write(text)
    if executable:
        os.chmod(path, os.stat(path).st_mode | stat.S_IXUSR)


class BuildAgentTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.mkdtemp(prefix='aipod-build-')
        self.root = os.path.join(self.temp, 'repo')
        self.bin = os.path.join(self.temp, 'bin')
        self.out = os.path.join(self.temp, 'out')
        self.calls = os.path.join(self.temp, 'calls')
        os.makedirs(os.path.join(self.root, 'scripts'))
        shutil.copyfile(SCRIPT, os.path.join(self.root, 'scripts', 'build-agent.sh'))
        write(os.path.join(self.root, 'src', 'Podium.Agent', 'Podium.Agent.csproj'), f'<Project><PropertyGroup><Version>{VERSION}</Version></PropertyGroup></Project>\n')
        for name, label in (('write_plugin_pins.py', 'pins'), ('sign_manifest.py', 'sign'), ('build_plugin_site.py', 'site')):
            write(os.path.join(self.root, 'scripts', name), PYTHON_STUB.replace('{name}', label))
        os.makedirs(os.path.join(self.root, 'scripts', 'install'))
        shutil.copyfile(ASSEMBLE, os.path.join(self.root, 'scripts', 'install', 'assemble.sh'))
        write(os.path.join(self.root, 'scripts', 'install', 'sh', '00-first.part'), '#!/bin/sh\n')
        write(os.path.join(self.root, 'scripts', 'install', 'sh', '99-last.part'), 'exit 0\n')
        write(os.path.join(self.root, 'scripts', 'install', 'ps1', '00-first.part'), '# installer\n')
        subprocess.run(['git', 'init', '-q', self.root], check=True)
        write(os.path.join(self.bin, 'dotnet'), DOTNET, executable=True)
        write(os.path.join(self.bin, 'zip'), ZIP, executable=True)
        for tool in ('codesign', 'xcrun'):
            write(os.path.join(self.bin, tool), STUB, executable=True)

    def tearDown(self):
        shutil.rmtree(self.temp)

    def run_script(self, **env):
        full = {key: value for key, value in os.environ.items()
                if key not in ('AGENT_UPDATE_CHANNEL', 'AGENT_SIGN_IDENTITY', 'AGENT_NOTARY_PROFILE', 'AGENT_DOWNLOADS_URL', 'AGENT_MIN_SUPPORTED')}
        full.update(env, PATH=self.bin + os.pathsep + os.environ['PATH'], CALLS=self.calls)
        return subprocess.run(['bash', os.path.join(self.root, 'scripts', 'build-agent.sh'), self.out],
                              capture_output=True, text=True, env=full, cwd=self.root)

    def lines(self, tool=None):
        if not os.path.exists(self.calls):
            return []
        with open(self.calls) as handle:
            found = [line.rstrip('\n') for line in handle]
        return [line for line in found if tool is None or line.split(' ', 1)[0] == tool]

    def test_every_target_is_built_and_zipped_under_its_download_name_with_its_checksum_and_the_pins(self):
        result = self.run_script()
        self.assertEqual(result.returncode, 0, result.stderr)
        rids = [line.split(' -r ')[1].split()[0] for line in self.lines('dotnet')]
        self.assertEqual(rids, list(TARGETS))
        for line in self.lines('dotnet'):
            self.assertNotIn('UpdatePublicKey', line)  # no channel: no key is built in
        for name in TARGETS.values():
            self.assertTrue(os.path.exists(os.path.join(self.out, name + '.zip')), name)
        with open(os.path.join(self.out, 'SHA256SUMS')) as handle:
            sums = handle.read().split('\n')
        self.assertEqual(sorted(line.split()[1] for line in sums if line), sorted(name + '.zip' for name in TARGETS.values()))
        zipped = {line.split()[-1] for line in self.lines('zip')}
        self.assertIn('aipod.exe', zipped)
        self.assertIn('aipod', zipped)
        self.assertEqual(self.lines('pins'), [f'pins {self.out} {VERSION}'])
        self.assertEqual(self.lines('sign'), [])
        self.assertEqual(self.lines('codesign'), [])
        self.assertFalse(os.path.exists(os.path.join(self.out, 'manifest.json')))
        self.assertIn('no AGENT_UPDATE_CHANNEL', result.stdout)
        self.assertTrue(os.path.exists(os.path.join(self.out, 'aipod-install.ps1')))
        # The installers are served as one file each: the step files joined in name order, the shell one executable (F-332).
        with open(os.path.join(self.out, 'aipod-install.sh')) as handle:
            self.assertEqual('#!/bin/sh\nexit 0\n', handle.read())
        self.assertTrue(os.stat(os.path.join(self.out, 'aipod-install.sh')).st_mode & stat.S_IXUSR)

    def test_a_channel_without_its_public_key_stops_before_anything_is_built(self):
        result = self.run_script(AGENT_UPDATE_CHANNEL='test')
        self.assertEqual(result.returncode, 1)
        self.assertIn("no public key for channel 'test'", result.stderr)
        self.assertEqual(self.lines('dotnet'), [])
        self.assertEqual(self.lines('pins'), [])

    def test_a_channel_builds_its_key_in_and_the_pins_are_written_before_the_signed_manifest(self):
        write(os.path.join(self.root, 'deploy', 'update-keys', 'test.pub'), 'KEY-PART-1\nKEY-PART-2\n')
        result = self.run_script(AGENT_UPDATE_CHANNEL='test')
        self.assertEqual(result.returncode, 0, result.stderr)
        for line in self.lines('dotnet'):
            self.assertIn('-p:UpdateChannel=test -p:UpdatePublicKey=KEY-PART-1KEY-PART-2', line)
        order = [line.split(' ', 1)[0] for line in self.lines() if line.split(' ', 1)[0] in ('pins', 'sign')]
        self.assertEqual(order, ['pins', 'sign'])
        self.assertEqual(self.lines('sign'), [f'sign sign --channel test --downloads {self.out} --version {VERSION} --min-supported {VERSION}'])

    def test_a_signing_identity_signs_and_verifies_only_the_macos_binaries(self):
        result = self.run_script(AGENT_SIGN_IDENTITY='Developer ID Application: Example (TEAM)')
        self.assertEqual(result.returncode, 0, result.stderr)
        signed = self.lines('codesign')
        self.assertEqual(len(signed), 4)  # sign + verify for each of the two macOS builds
        self.assertTrue(all('/osx-' in line for line in signed), signed)
        self.assertEqual(sum('--sign Developer ID Application: Example (TEAM)' in line for line in signed), 2)
        self.assertEqual(self.lines('xcrun'), [])  # no notary profile: no notarisation


if __name__ == '__main__':
    unittest.main()
