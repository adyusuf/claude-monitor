"""Every PowerShell script under deploy/windows parses (PowerShell 7's own parser). They run only on the Windows
server, so a syntax error would otherwise first show there. Skipped (not passed) where pwsh is not installed."""
from __future__ import annotations

import shutil
import subprocess
import unittest
from pathlib import Path

FOLDER = Path(__file__).resolve().parents[2] / "deploy" / "windows"
PWSH = shutil.which("pwsh")


@unittest.skipUnless(PWSH, "pwsh is not installed")
class PowerShellScripts(unittest.TestCase):
    def test_every_script_parses(self):
        scripts = sorted(FOLDER.glob("*.ps*1"))
        self.assertGreaterEqual(len(scripts), 6)
        for script in scripts:
            check = ("$errors = $null; [void][System.Management.Automation.Language.Parser]::ParseFile("
                     f"'{script}', [ref]$null, [ref]$errors); $errors | ForEach-Object {{ $_.Message }}; exit $errors.Count")
            result = subprocess.run([PWSH, "-NoProfile", "-Command", check], capture_output=True, text=True)
            self.assertEqual(0, result.returncode, f"{script.name}: {result.stdout}{result.stderr}")


if __name__ == "__main__":
    unittest.main()
