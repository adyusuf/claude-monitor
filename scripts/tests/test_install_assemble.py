"""scripts/install/assemble.sh: the installers are split into step files (global rule #9) but served as ONE file each (F-332).

`curl ... | sh` and a downloaded .ps1 cannot source a sibling file, so the step files are joined into the served file. Here: the parts are
what the rule needs (each under 300 lines, named so they join in step order), the joined file is exactly the parts one after the other with
the same functions in the same order, the shell one parses, is executable and is ShellCheck clean, the PowerShell one parses, and a missing
step folder stops the assembly instead of serving a half installer. The behaviour of the joined installers is test_install_script.py and
test_install_ps1_behaviour.py, which run them. Skipped (not passed): ShellCheck or pwsh where not installed."""
import importlib.util
import os
import re
import shutil
import stat
import subprocess
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location('install_support', os.path.join(HERE, 'install_support.py'))
support = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(support)
SH_PARTS = os.path.join(support.INSTALL_DIR, 'sh')
PS1_PARTS = os.path.join(support.INSTALL_DIR, 'ps1')
MAX_LINES = 300  # global rule #9
PWSH = shutil.which('pwsh')
SHELLCHECK = shutil.which('shellcheck')

# The steps in the order the installer runs them: a part added, dropped or moved is a decision to make here, not a silent change.
SH_STEPS = ['00-options', '10-environment', '20-find-old', '30-carry-over', '40-remove-old', '50-download-host', '60-deciding',
            '70-install-build', '80-login', '90-plugin', '99-finish']
PS1_STEPS = ['00-parameters', '10-environment', '20-credentials', '30-find-old', '40-carry-over', '50-remove-old', '60-download-host',
             '70-deciding', '80-install-build', '90-login', '95-plugin', '99-finish']
SH_FUNCTIONS = ['usage', 'say', 'warn', 'die', 'did', 'act', 'can_prompt', 'confirm', 'json_field', 'newer', 'sha256', 'valid_url', 'have_claude',
                'old_binaries', 'old_plugin_ids', 'old_marketplaces', 'old_daemon_pids', 'old_autostart', 'old_boot_service', 'old_binary_version',
                'found_old', 'list_old', 'identity_home', 'copy_token', 'carry_over', 'stop_old_daemons', 'remove_old', 'connected_to', 'lower']
PS1_FUNCTIONS = ['Say', 'Warn', 'Die', 'Did', 'Act', 'Run-Native', 'Have-Claude', 'Newer', 'Confirm-Step', 'Get-OldBinaries', 'Get-OldPluginIds',
                 'Get-OldMarketplaces', 'Get-OldTask', 'Get-OldService', 'Get-OldDaemons', 'Test-FoundOld', 'Show-Old', 'Get-IdentityHome',
                 'Copy-Carry', 'Stop-OldDaemons', 'Remove-OldAgent', 'Test-Url', 'Get-Remote']


def parts_of(folder):
    return sorted(name for name in os.listdir(folder) if name.endswith('.part'))


def read(path):
    with open(path, encoding='utf-8') as handle:
        return handle.read()


class AssembleTests(unittest.TestCase):
    def test_the_step_files_are_the_installers_steps_in_order_and_none_is_over_300_lines(self):
        self.assertEqual([name + '.part' for name in SH_STEPS], parts_of(SH_PARTS))
        self.assertEqual([name + '.part' for name in PS1_STEPS], parts_of(PS1_PARTS))
        for folder in (SH_PARTS, PS1_PARTS):
            for name in parts_of(folder):
                lines = read(os.path.join(folder, name)).count('\n')
                self.assertLessEqual(lines, MAX_LINES, f'{folder}/{name} has {lines} lines')

    def test_the_served_file_is_the_step_files_one_after_the_other_and_nothing_else(self):
        for folder, served in ((SH_PARTS, support.SH), (PS1_PARTS, support.PS1)):
            joined = ''.join(read(os.path.join(folder, name)) for name in parts_of(folder))
            self.assertEqual(joined, read(support.assembled(served)), served)

    def test_the_served_files_keep_their_functions_in_the_same_order(self):
        sh = read(support.assembled(support.SH))
        self.assertEqual(SH_FUNCTIONS, re.findall(r'^([a-z_0-9]+)\(\) \{', sh, re.MULTILINE))
        ps1 = read(support.assembled(support.PS1))
        self.assertEqual(PS1_FUNCTIONS, re.findall(r'^function ([A-Za-z-]+)', ps1, re.MULTILINE))

    def test_the_shell_installer_starts_with_its_help_text_where_usage_reads_it(self):
        lines = read(support.assembled(support.SH)).split('\n')
        self.assertEqual('#!/bin/sh', lines[0])
        self.assertTrue(all(line.startswith('#') for line in lines[1:15]))  # usage prints lines 2 to 15 of the file itself
        self.assertEqual('set -u', lines[15])

    def test_the_shell_installer_parses_is_executable_and_shellcheck_clean(self):
        script = support.assembled(support.SH)
        self.assertTrue(os.stat(script).st_mode & stat.S_IXUSR)
        parsed = subprocess.run(['sh', '-n', script], capture_output=True, text=True)
        self.assertEqual(0, parsed.returncode, parsed.stderr)
        if not SHELLCHECK:
            self.skipTest('shellcheck is not installed')
        checked = subprocess.run([SHELLCHECK, '--severity=style', script], capture_output=True, text=True)
        self.assertEqual(0, checked.returncode, checked.stdout + checked.stderr)

    @unittest.skipUnless(PWSH, 'pwsh is not installed')
    def test_the_powershell_installer_parses_with_its_try_block_closed(self):
        script = support.assembled(support.PS1)
        check = ('$errors = $null; [void][System.Management.Automation.Language.Parser]::ParseFile('
                 f"'{script}', [ref]$null, [ref]$errors); $errors | ForEach-Object {{ $_.Message }}; exit $errors.Count")
        result = subprocess.run([PWSH, '-NoProfile', '-Command', check], capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_a_missing_step_folder_stops_the_assembly_and_serves_nothing(self):
        with tempfile.TemporaryDirectory() as temp:
            install = os.path.join(temp, 'install')
            os.makedirs(os.path.join(install, 'sh'))
            shutil.copyfile(support.ASSEMBLE, os.path.join(install, 'assemble.sh'))
            shutil.copyfile(os.path.join(SH_PARTS, '99-finish.part'), os.path.join(install, 'sh', '99-finish.part'))
            out = os.path.join(temp, 'out')
            result = subprocess.run(['bash', os.path.join(install, 'assemble.sh'), out], capture_output=True, text=True)
            self.assertNotEqual(0, result.returncode)
            self.assertIn('no step files', result.stderr)
            self.assertFalse(os.path.exists(os.path.join(out, support.PS1)))

    def test_no_output_folder_is_a_usage_error(self):
        result = subprocess.run(['bash', support.ASSEMBLE], capture_output=True, text=True)
        self.assertEqual(2, result.returncode)
        self.assertIn('usage', result.stderr)


if __name__ == '__main__':
    unittest.main()
