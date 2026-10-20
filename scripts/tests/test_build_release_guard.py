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


class PackageHoldsNoAgentBuilds(unittest.TestCase):
    """The agent builds are served from the download host, so the release package carries no zip of them (and nothing else from out/downloads)."""

    def test_out_downloads_is_not_copied_into_the_package(self):
        root = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, root, True)
        os.makedirs(os.path.join(root, 'src', 'ClaudeMonitor.Api'))
        with open(os.path.join(root, 'src', 'ClaudeMonitor.Api', 'appsettings.json'), 'w', encoding='utf-8') as handle:
            handle.write('{}\n')
        with open(os.path.join(root, '.gitignore'), 'w', encoding='utf-8') as handle:
            handle.write('out/\n')
        for command in (['init', '-q'], ['config', 'user.email', 't@t'], ['config', 'user.name', 't'], ['add', '-A'], ['commit', '-qm', 'init']):
            subprocess.run(['git', *command], cwd=root, check=True)
        downloads = os.path.join(root, 'out', 'downloads')
        os.makedirs(downloads)
        for name in ('cm-agent-linux-x64.zip', 'SHA256SUMS', 'manifest.json', 'aipod-plugin.zip'):
            with open(os.path.join(downloads, name), 'w', encoding='utf-8') as handle:
                handle.write(name)
        # npm "builds" web/dist and dotnet "publishes" its -o folder: enough for the script to reach the packing step.
        bin_dir = os.path.join(root, 'fakebin')
        os.makedirs(bin_dir)
        fakes = {
            'npm': 'mkdir -p web/dist && echo app > web/dist/index.html\n',
            'dotnet': 'while [ $# -gt 0 ]; do if [ "$1" = "-o" ]; then mkdir -p "$2"; echo dll > "$2/Api.dll"; fi; shift; done\n',
        }
        for tool, body in fakes.items():
            path = os.path.join(bin_dir, tool)
            with open(path, 'w', encoding='utf-8') as handle:
                handle.write('#!/bin/sh\n' + body)
            os.chmod(path, 0o700)
        env = dict(os.environ, PATH=bin_dir + os.pathsep + os.environ['PATH'])
        result = subprocess.run(['bash', SCRIPT], cwd=root, capture_output=True, text=True, env=env)
        self.assertEqual(result.returncode, 0, result.stderr)
        package = [f for f in os.listdir(os.path.join(root, 'out')) if f.startswith('claude-monitor-') and f.endswith('.zip')]
        self.assertEqual(len(package), 1)
        listing = subprocess.run(['unzip', '-Z1', os.path.join(root, 'out', package[0])], capture_output=True, text=True, check=True).stdout.split()
        self.assertIn('web/index.html', listing)
        self.assertEqual([f for f in listing if 'downloads' in f or f.endswith('.zip')], [])
