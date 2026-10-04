"""scripts/build-release.sh refuses a server-only appsettings.Production.json before it builds anything.

That file may hold the SMTP password (the "Smtp" node ApiConfig reads). It is git-ignored, so the clean-tree check
does not see it, and `dotnet publish` would copy it into a package that must hold no secret.
"""
import os
import shutil
import subprocess
import tempfile
import unittest

SCRIPT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'build-release.sh'))


class Guard(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.root, True)
        api = os.path.join(self.root, 'src', 'ClaudeMonitor.Api')
        os.makedirs(api)
        with open(os.path.join(api, 'appsettings.json'), 'w', encoding='utf-8') as handle:
            handle.write('{}\n')
        with open(os.path.join(self.root, '.gitignore'), 'w', encoding='utf-8') as handle:
            handle.write('appsettings.Production*.json\n')
        for command in (['init', '-q'], ['config', 'user.email', 't@t'], ['config', 'user.name', 't'],
                        ['add', '-A'], ['commit', '-qm', 'init']):
            subprocess.run(['git', *command], cwd=self.root, check=True)
        # npm and dotnet that record they ran: the guard must stop the build before either.
        self.bin = os.path.join(self.root, 'fakebin')
        os.makedirs(self.bin)
        self.ran = os.path.join(self.root, 'ran.txt')
        for tool in ('npm', 'dotnet'):
            path = os.path.join(self.bin, tool)
            with open(path, 'w', encoding='utf-8') as handle:
                handle.write(f'#!/bin/sh\necho {tool} >> "{self.ran}"\nexit 1\n')
            os.chmod(path, 0o700)

    def build(self):
        env = dict(os.environ, PATH=self.bin + os.pathsep + os.environ['PATH'])
        return subprocess.run(['bash', SCRIPT], cwd=self.root, capture_output=True, text=True, env=env)

    def test_a_local_production_settings_file_stops_the_build_before_anything_runs(self):
        with open(os.path.join(self.root, 'src', 'ClaudeMonitor.Api', 'appsettings.Production.json'), 'w',
                  encoding='utf-8') as handle:
            handle.write('{"Smtp": {"Password": "x"}}\n')
        result = self.build()
        self.assertEqual(result.returncode, 2, result.stderr)
        self.assertIn('appsettings.Production.json would ship', result.stderr)
        self.assertFalse(os.path.exists(self.ran), 'npm/dotnet ran before the guard')

    def test_without_it_the_build_goes_on(self):
        result = self.build()
        self.assertNotIn('refusing', result.stderr)
        with open(self.ran, encoding='utf-8') as handle:
            self.assertIn('npm', handle.read())                 # it reached the build (the fake npm then fails)


if __name__ == '__main__':
    unittest.main()
