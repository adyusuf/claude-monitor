"""scripts/sign_manifest.py: what is signed, that the signature verifies, and that nothing unsigned or odd is let through.

A throw-away key is made with openssl for each run; the Keychain is never touched (the key arrives through --key-env).
Verification here is done with openssl itself, independent of the script's own signing code; the .NET side checks the
same signatures with UpdateManifest.Verify (known-answer test in ClaudeMonitor.Agent.Tests).
"""
import base64
import contextlib
import importlib.util
import io
import json
import os
import subprocess
import tempfile
import unittest
from unittest import mock

SCRIPT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'sign_manifest.py'))
spec = importlib.util.spec_from_file_location('sign_manifest', SCRIPT)
sm = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sm)

SHA = 'a' * 64


def new_key():
    return base64.b64encode(sm.openssl(['ecparam', '-name', 'prime256v1', '-genkey', '-noout', '-outform', 'DER'])).decode()


def verifies(public_b64, text, signature_b64):
    with tempfile.TemporaryDirectory() as d:
        key, sig, msg = (os.path.join(d, n) for n in ('pub.der', 'sig', 'msg'))
        for path, mode, data in ((key, 'wb', base64.b64decode(public_b64)), (sig, 'wb', base64.b64decode(signature_b64)), (msg, 'w', text)):
            with open(path, mode) as f:
                f.write(data)
        return subprocess.run(['openssl', 'dgst', '-sha256', '-verify', key, '-keyform', 'DER', '-signature', sig, msg],
                              capture_output=True, check=False).returncode == 0


class PayloadTests(unittest.TestCase):
    def test_the_text_is_one_field_per_line_in_a_fixed_order(self):
        self.assertEqual(sm.payload('test', '0.3.1', 'macos', 'arm64', SHA, '0.2.0'),
                         'cm-agent-update/1\nchannel=test\nversion=0.3.1\nos=macos\narch=arm64\n' + f'sha256={SHA}\nminSupported=0.2.0\n')

    def test_a_field_with_a_line_break_or_nothing_is_refused(self):
        for bad in ('', 'x\ny', 'x\ry'):
            with self.assertRaises(ValueError):
                sm.payload('test', bad, 'macos', 'arm64', SHA, '0.2.0')


class SigningTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.dir.cleanup)
        keys = tempfile.TemporaryDirectory()  # never the committed deploy/update-keys: a test must not overwrite or delete them
        self.addCleanup(keys.cleanup)
        patcher = mock.patch.object(sm, 'KEYS', keys.name)
        patcher.start()
        self.addCleanup(patcher.stop)
        self.key = new_key()
        self.public = sm.public_key(self.key)
        for name, body in (('cm-agent-macos-arm64.zip', b'one'), ('cm-agent-windows-x64.zip', b'two'), ('SHA256SUMS', b'x'), ('notes.txt', b'y')):
            with open(os.path.join(self.dir.name, name), 'wb') as f:
                f.write(body)

    def manifest(self, **kw):
        args = dict(channel='test', downloads=self.dir.name, version='0.3.1', min_supported='0.2.0', private_b64=self.key)
        args.update(kw)
        return sm.build_manifest(**args)

    def test_every_zip_gets_an_entry_with_its_hash_and_a_signature_that_verifies(self):
        entries = {e['file']: e for e in self.manifest()['entries']}
        self.assertEqual(sorted(entries), ['cm-agent-macos-arm64.zip', 'cm-agent-windows-x64.zip'])
        mac = entries['cm-agent-macos-arm64.zip']
        self.assertEqual((mac['os'], mac['arch'], mac['channel'], mac['version'], mac['minSupported']), ('macos', 'arm64', 'test', '0.3.1', '0.2.0'))
        import hashlib
        self.assertEqual(mac['sha256'], hashlib.sha256(b'one').hexdigest())
        text = sm.payload('test', '0.3.1', 'macos', 'arm64', mac['sha256'], '0.2.0')
        self.assertTrue(verifies(self.public, text, mac['signature']))

    def test_the_signature_covers_every_field(self):
        mac = self.manifest()['entries'][0]
        good = dict(channel='test', version='0.3.1', os_name='macos', arch='arm64', sha256=mac['sha256'], min_supported='0.2.0')
        self.assertTrue(verifies(self.public, sm.payload(**good), mac['signature']))
        for field, other in (('channel', 'prod'), ('version', '0.3.2'), ('os_name', 'windows'), ('arch', 'x64'), ('sha256', SHA), ('min_supported', '0.1.0')):
            self.assertFalse(verifies(self.public, sm.payload(**{**good, field: other}), mac['signature']), field)

    def test_another_key_does_not_verify(self):
        mac = self.manifest()['entries'][0]
        text = sm.payload('test', '0.3.1', 'macos', 'arm64', mac['sha256'], '0.2.0')
        self.assertFalse(verifies(sm.public_key(new_key()), text, mac['signature']))

    def test_odd_input_is_refused(self):
        with self.assertRaises(ValueError):
            self.manifest(channel='dev')
        with self.assertRaises(ValueError):
            self.manifest(version='0.3')
        with self.assertRaises(ValueError):
            self.manifest(min_supported='latest')
        with tempfile.TemporaryDirectory() as empty, self.assertRaises(RuntimeError):
            self.manifest(downloads=empty)

    def test_the_command_writes_manifest_json_from_the_key_in_the_environment(self):
        out = io.StringIO()
        with mock.patch.dict(os.environ, {'TEST_KEY': self.key}), contextlib.redirect_stdout(out):
            code = sm.main(['sign', '--channel', 'prod', '--downloads', self.dir.name, '--version', '1.0.0', '--min-supported', '0.3.0', '--key-env', 'TEST_KEY'])
        self.assertEqual(code, 0)
        with open(os.path.join(self.dir.name, 'manifest.json')) as f:
            manifest = json.load(f)
        self.assertEqual(manifest['format'], 'cm-agent-update/1')
        self.assertEqual({e['channel'] for e in manifest['entries']}, {'prod'})

    def test_a_signature_that_does_not_verify_is_never_published(self):
        with mock.patch.object(sm, 'sign', return_value=base64.b64encode(b'not a signature').decode()):
            with self.assertRaises(RuntimeError):
                self.manifest()

    def test_a_key_that_is_not_the_shipped_one_is_refused(self):
        shipped = os.path.join(sm.KEYS, 'test.pub')
        with open(shipped, 'w', encoding='utf-8') as f:
            f.write(sm.public_key(new_key()))
        err = io.StringIO()
        with mock.patch.dict(os.environ, {'TEST_KEY': self.key}), contextlib.redirect_stderr(err):
            code = sm.main(['sign', '--channel', 'test', '--downloads', self.dir.name, '--version', '0.3.1', '--min-supported', '0.2.0', '--key-env', 'TEST_KEY'])
        self.assertEqual(code, 1)
        self.assertIn('deploy/update-keys/test.pub', err.getvalue())
        self.assertFalse(os.path.exists(os.path.join(self.dir.name, 'manifest.json')))

    def test_the_shipped_key_itself_is_accepted(self):
        shipped = os.path.join(sm.KEYS, 'test.pub')
        with open(shipped, 'w', encoding='utf-8') as f:
            f.write(self.public + '\n')
        with mock.patch.dict(os.environ, {'TEST_KEY': self.key}), contextlib.redirect_stdout(io.StringIO()):
            code = sm.main(['sign', '--channel', 'test', '--downloads', self.dir.name, '--version', '0.3.1', '--min-supported', '0.2.0', '--key-env', 'TEST_KEY'])
        self.assertEqual(code, 0)

    def test_a_missing_key_fails_and_writes_nothing(self):
        err = io.StringIO()
        with mock.patch.dict(os.environ, {}, clear=False), contextlib.redirect_stderr(err):
            os.environ.pop('NO_SUCH_KEY', None)
            code = sm.main(['sign', '--channel', 'test', '--downloads', self.dir.name, '--version', '0.3.1', '--min-supported', '0.2.0', '--key-env', 'NO_SUCH_KEY'])
        self.assertEqual(code, 1)
        self.assertIn('NO_SUCH_KEY', err.getvalue())
        self.assertFalse(os.path.exists(os.path.join(self.dir.name, 'manifest.json')))


if __name__ == '__main__':
    unittest.main()
