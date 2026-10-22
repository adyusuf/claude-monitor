"""scripts/publish_agent_downloads.sh: what goes to the download host, in which order, and what is refused."""
import hashlib
import json
import os
import subprocess
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
SCRIPT = os.path.join(HERE, '..', 'publish_agent_downloads.sh')
ZIPS = ['aipod-macos-arm64.zip', 'aipod-windows-x64.zip']


def build(root, channel='prod', version='0.4.0', tamper=False):
    sums = []
    entries = []
    for name in ZIPS:
        data = name.encode()
        with open(os.path.join(root, name), 'wb') as handle:
            handle.write(data)
        digest = hashlib.sha256(data).hexdigest()
        sums.append(f'{digest}  {name}')
        entries.append({'channel': channel, 'version': version, 'file': name, 'sha256': digest})
    with open(os.path.join(root, 'SHA256SUMS'), 'w') as handle:
        handle.write('\n'.join(sums) + '\n')
    with open(os.path.join(root, 'manifest.json'), 'w') as handle:
        json.dump({'format': 'aipod-update/1', 'entries': entries}, handle)
    if tamper:
        with open(os.path.join(root, ZIPS[0]), 'wb') as handle:
            handle.write(b'changed')


def run(channel, folder, env=None, extra=()):
    full = dict(os.environ, **(env or {}))
    return subprocess.run(['bash', SCRIPT, channel, folder, *extra], capture_output=True, text=True, env=full, cwd=HERE)


class PublishTests(unittest.TestCase):
    def test_dry_run_lists_the_kept_copy_then_the_current_one_with_the_manifest_last(self):
        with tempfile.TemporaryDirectory() as folder:
            build(folder)
            result = run('prod', folder, {'AGENT_R2_BUCKET': 'b'}, ['--dry-run'])
        self.assertEqual(result.returncode, 0, result.stderr)
        lines = [line for line in result.stdout.splitlines() if line.startswith('put ')]
        keys = [line.split()[1] for line in lines]
        self.assertEqual(keys, [
            'b/daemons/0.4.0/prod/aipod-macos-arm64.zip', 'b/daemons/0.4.0/prod/aipod-windows-x64.zip',
            'b/daemons/0.4.0/prod/SHA256SUMS', 'b/daemons/0.4.0/prod/manifest.json',
            'b/daemons/prod/aipod-macos-arm64.zip', 'b/daemons/prod/aipod-windows-x64.zip',
            'b/daemons/prod/SHA256SUMS', 'b/daemons/prod/manifest.json'])

    def test_the_plugin_files_go_up_before_the_checksums_and_the_manifest_when_the_build_has_them(self):
        with tempfile.TemporaryDirectory() as folder:
            build(folder)
            for name in ('aipod-plugin.zip', 'aipod-marketplace.json'):
                with open(os.path.join(folder, name), 'wb') as handle:
                    handle.write(name.encode())
            result = run('prod', folder, {'AGENT_R2_BUCKET': 'b'}, ['--dry-run'])
        self.assertEqual(result.returncode, 0, result.stderr)
        keys = [line.split()[1] for line in result.stdout.splitlines() if line.startswith('put b/daemons/prod/')]
        self.assertEqual(keys, [
            'b/daemons/prod/aipod-macos-arm64.zip', 'b/daemons/prod/aipod-windows-x64.zip',
            'b/daemons/prod/aipod-plugin.zip', 'b/daemons/prod/aipod-marketplace.json',
            'b/daemons/prod/SHA256SUMS', 'b/daemons/prod/manifest.json'])

    def test_the_installers_go_up_beside_the_zips_when_the_build_has_them(self):
        with tempfile.TemporaryDirectory() as folder:
            build(folder)
            for name in ('aipod-install.sh', 'aipod-install.ps1'):
                with open(os.path.join(folder, name), 'wb') as handle:
                    handle.write(name.encode())
            result = run('prod', folder, {'AGENT_R2_BUCKET': 'b'}, ['--dry-run'])
        self.assertEqual(result.returncode, 0, result.stderr)
        keys = [line.split()[1] for line in result.stdout.splitlines() if line.startswith('put b/daemons/prod/')]
        self.assertEqual(keys, [
            'b/daemons/prod/aipod-macos-arm64.zip', 'b/daemons/prod/aipod-windows-x64.zip',
            'b/daemons/prod/aipod-install.sh', 'b/daemons/prod/aipod-install.ps1',
            'b/daemons/prod/SHA256SUMS', 'b/daemons/prod/manifest.json'])

    def test_the_prefix_can_be_changed(self):
        with tempfile.TemporaryDirectory() as folder:
            build(folder, channel='test')
            result = run('test', folder, {'AGENT_R2_BUCKET': 'b', 'AGENT_R2_PREFIX': 'agents'}, ['--dry-run'])
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn('b/agents/test/manifest.json', result.stdout)

    def test_a_manifest_signed_for_the_other_channel_is_refused(self):
        with tempfile.TemporaryDirectory() as folder:
            build(folder, channel='test')
            result = run('prod', folder, {'AGENT_R2_BUCKET': 'b'}, ['--dry-run'])
        self.assertEqual(result.returncode, 2)
        self.assertIn("signed for 'test', not 'prod'", result.stderr)

    def test_files_that_do_not_match_the_checksums_are_refused(self):
        with tempfile.TemporaryDirectory() as folder:
            build(folder, tamper=True)
            result = run('prod', folder, {'AGENT_R2_BUCKET': 'b'}, ['--dry-run'])
        self.assertEqual(result.returncode, 2)
        self.assertIn('SHA256SUMS does not match', result.stderr)

    def test_no_bucket_no_manifest_and_a_bad_channel_are_refused(self):
        with tempfile.TemporaryDirectory() as folder:
            build(folder)
            env = {k: v for k, v in os.environ.items() if k != 'AGENT_R2_BUCKET'}
            self.assertEqual(subprocess.run(['bash', SCRIPT, 'prod', folder, '--dry-run'], capture_output=True, text=True, env=env, cwd=HERE).returncode, 2)
            os.remove(os.path.join(folder, 'manifest.json'))
            self.assertEqual(run('prod', folder, {'AGENT_R2_BUCKET': 'b'}, ['--dry-run']).returncode, 2)
            self.assertEqual(run('stage', folder, {'AGENT_R2_BUCKET': 'b'}, ['--dry-run']).returncode, 2)

    def test_a_manifest_with_two_versions_is_refused(self):
        with tempfile.TemporaryDirectory() as folder:
            build(folder)
            path = os.path.join(folder, 'manifest.json')
            with open(path) as handle:
                manifest = json.load(handle)
            manifest['entries'][1]['version'] = '0.3.0'
            with open(path, 'w') as handle:
                json.dump(manifest, handle)
            result = run('prod', folder, {'AGENT_R2_BUCKET': 'b'}, ['--dry-run'])
        self.assertEqual(result.returncode, 1)
        self.assertIn('one channel and one version', result.stderr)


if __name__ == '__main__':
    unittest.main()
