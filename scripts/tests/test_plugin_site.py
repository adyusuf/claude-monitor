"""scripts/build_plugin_site.py (ADR-0007, section 9.2): the plugin zip and the marketplace.json a site serves from /downloads."""
import hashlib
import importlib.util
import io
import json
import os
import stat
import tempfile
import unittest
import zipfile
from contextlib import redirect_stderr

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location('build_plugin_site', os.path.join(HERE, '..', 'build_plugin_site.py'))
site = importlib.util.module_from_spec(spec)
spec.loader.exec_module(site)

BASE = 'https://aipod.invalid'


def make_plugin(root):
    os.makedirs(os.path.join(root, '.claude-plugin'))
    os.makedirs(os.path.join(root, 'scripts'))
    with open(os.path.join(root, '.claude-plugin', 'plugin.json'), 'w') as handle:
        handle.write('{"name": "aipod"}')
    with open(os.path.join(root, 'scripts', 'run.sh'), 'w') as handle:
        handle.write('#!/bin/sh\n')
    os.chmod(os.path.join(root, 'scripts', 'run.sh'), 0o700)  # owner-only execute: all the build reads is S_IXUSR
    with open(os.path.join(root, 'agent-release.txt'), 'w') as handle:
        handle.write('1.2.3\n')
    with open(os.path.join(root, '.DS_Store'), 'w') as handle:
        handle.write('x')


class PluginSite(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.plugin = os.path.join(self.tmp.name, 'plugin')
        make_plugin(self.plugin)
        self.out = os.path.join(self.tmp.name, 'downloads')

    def tearDown(self):
        self.tmp.cleanup()

    def build(self, base=BASE):
        return site.main([self.out, '--base-url', base, '--aipod-dir', self.plugin])

    def refused(self, base, plugin_dir=None):
        with redirect_stderr(io.StringIO()) as err, self.assertRaises(SystemExit) as raised:
            site.main([self.out, '--base-url', base, '--aipod-dir', plugin_dir or self.plugin])
        self.assertEqual(raised.exception.code, 2)
        return err.getvalue()

    def test_the_catalog_names_the_archive_with_the_hash_of_the_zip_it_wrote(self):
        self.assertEqual(self.build(), 0)
        with open(os.path.join(self.out, 'aipod-marketplace.json'), encoding='utf-8') as handle:
            catalog = json.load(handle)
        self.assertEqual(catalog['name'], 'aipod-releases')
        entry = catalog['plugins'][0]
        self.assertEqual(entry['name'], 'aipod')
        self.assertEqual(entry['source']['source'], 'archive')
        self.assertEqual(entry['source']['url'], BASE + '/downloads/aipod-plugin.zip')
        with open(os.path.join(self.out, 'aipod-plugin.zip'), 'rb') as handle:
            self.assertEqual(entry['source']['sha256'], hashlib.sha256(handle.read()).hexdigest())

    def test_the_zip_has_the_plugin_at_its_root_keeps_the_executable_bit_and_leaves_junk_out(self):
        self.build()
        with zipfile.ZipFile(os.path.join(self.out, 'aipod-plugin.zip')) as archive:
            names = sorted(archive.namelist())
            self.assertEqual(names, ['.claude-plugin/plugin.json', 'agent-release.txt', 'scripts/run.sh'])
            mode = archive.getinfo('scripts/run.sh').external_attr >> 16
            self.assertTrue(mode & stat.S_IXUSR)
            self.assertFalse(archive.getinfo('agent-release.txt').external_attr >> 16 & stat.S_IXUSR)

    def test_the_same_files_give_the_same_hash(self):
        self.build()
        with open(os.path.join(self.out, 'aipod-plugin.zip'), 'rb') as handle:
            first = hashlib.sha256(handle.read()).hexdigest()
        os.utime(os.path.join(self.plugin, 'agent-release.txt'), (1, 1))
        self.build()
        with open(os.path.join(self.out, 'aipod-plugin.zip'), 'rb') as handle:
            self.assertEqual(hashlib.sha256(handle.read()).hexdigest(), first)

    def test_an_address_claude_code_would_refuse_is_refused_before_anything_is_written(self):
        for base in ['http://aipod.invalid', 'https://localhost', 'https://app.localhost', 'https://127.0.0.1', 'https://[::1]',
                     'https://169.254.169.254', 'https://0.0.0.0', 'https://aipod.invalid/downloads', 'https://u:p@aipod.invalid',
                     'https://aipod.invalid?x=1', 'aipod.invalid', '']:
            with self.subTest(base=base):
                self.refused(base)
        self.assertFalse(os.path.exists(self.out))

    def test_a_trailing_slash_and_a_port_are_fine_and_a_folder_that_is_not_a_plugin_is_refused(self):
        self.assertEqual(self.build('https://aipod.invalid:8443/'), 0)
        with open(os.path.join(self.out, 'aipod-marketplace.json'), encoding='utf-8') as handle:
            self.assertEqual(json.load(handle)['plugins'][0]['source']['url'], 'https://aipod.invalid:8443/downloads/aipod-plugin.zip')
        empty = os.path.join(self.tmp.name, 'empty')
        os.makedirs(empty)
        self.assertIn('not a plugin folder', self.refused(BASE, empty))
        self.assertIn('not found', self.refused(BASE, os.path.join(self.tmp.name, 'missing')))

    def test_the_repository_plugin_builds_with_its_own_catalog(self):
        self.assertEqual(site.main([self.out, '--base-url', BASE]), 0)
        self.assertEqual(sorted(os.listdir(self.out)), ['aipod-marketplace.json', 'aipod-plugin.zip'])
        with zipfile.ZipFile(os.path.join(self.out, 'aipod-plugin.zip')) as archive:
            self.assertIn('hooks/hooks.json', archive.namelist())
            self.assertEqual(json.loads(archive.read('.claude-plugin/plugin.json'))['name'], 'aipod')
        with open(os.path.join(self.out, 'aipod-marketplace.json'), encoding='utf-8') as handle:
            catalog = json.load(handle)
        with open(os.path.join(self.out, 'aipod-plugin.zip'), 'rb') as handle:
            digest = hashlib.sha256(handle.read()).hexdigest()
        self.assertEqual((catalog['name'], catalog['plugins'][0]['name']), ('aipod-releases', 'aipod'))
        self.assertEqual(catalog['plugins'][0]['source'], {'source': 'archive', 'url': f'{BASE}/downloads/aipod-plugin.zip', 'sha256': digest})


if __name__ == '__main__':
    unittest.main()
