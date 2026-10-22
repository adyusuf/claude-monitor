"""Every PowerShell script under deploy/windows and scripts/install parses (PowerShell 7's own parser). They run only on
Windows, so a syntax error would otherwise first show there. Skipped (not passed) where pwsh is not installed."""
from __future__ import annotations

import shutil
import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
FOLDER = ROOT / "deploy" / "windows"
INSTALL = ROOT / "scripts" / "install" / "aipod-install.ps1"
PWSH = shutil.which("pwsh")


@unittest.skipUnless(PWSH, "pwsh is not installed")
class PowerShellScripts(unittest.TestCase):
    def test_every_script_parses(self):
        scripts = sorted(FOLDER.glob("*.ps*1")) + [INSTALL]
        self.assertGreaterEqual(len(scripts), 7)
        for script in scripts:
            check = ("$errors = $null; [void][System.Management.Automation.Language.Parser]::ParseFile("
                     f"'{script}', [ref]$null, [ref]$errors); $errors | ForEach-Object {{ $_.Message }}; exit $errors.Count")
            result = subprocess.run([PWSH, "-NoProfile", "-Command", check], capture_output=True, text=True)
            self.assertEqual(0, result.returncode, f"{script.name}: {result.stdout}{result.stderr}")

    def test_the_installer_refuses_to_run_off_windows(self):
        if __import__("platform").system() == "Windows":
            self.skipTest("this is Windows")
        result = subprocess.run([PWSH, "-NoProfile", "-File", str(INSTALL)], capture_output=True, text=True)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("is for Windows", result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
