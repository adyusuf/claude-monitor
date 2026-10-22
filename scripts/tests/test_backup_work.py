"""deploy/windows/BackupCrypto.psm1 scratch folders of backup.ps1 with PowerShell 7: two runs that start in the same
second (the test and prod tasks, 22/10/2026) get folders of their own, and the cleanup removes the plain dump also when
a run failed half-way. Skipped (not passed) where pwsh is not installed."""
from __future__ import annotations

import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

MODULE = Path(__file__).resolve().parents[2] / "deploy" / "windows" / "BackupCrypto.psm1"
PWSH = shutil.which("pwsh")
STAMP = "2026-10-22T193647Z"


@unittest.skipUnless(PWSH, "pwsh is not installed")
class BackupWork(unittest.TestCase):
    def run_ps(self, script: str) -> subprocess.CompletedProcess:
        return subprocess.run([PWSH, "-NoProfile", "-Command", f"Import-Module '{MODULE}'; $ErrorActionPreference='Stop'; {script}"],
                              capture_output=True, text=True)

    def test_runs_in_the_same_second_get_their_own_folders(self):
        with tempfile.TemporaryDirectory() as tmp:
            r = self.run_ps(
                f"$a = New-BackupWork -Environment test -Stamp '{STAMP}' -Temp '{tmp}'; "
                f"$b = New-BackupWork -Environment prod -Stamp '{STAMP}' -Temp '{tmp}'; "
                f"$c = New-BackupWork -Environment prod -Stamp '{STAMP}' -Temp '{tmp}'; "
                "$a.Folder; $b.Folder; $c.Folder; $c.Zip")
            self.assertEqual(0, r.returncode, r.stderr)
            a, b, c, c_zip = r.stdout.split()
            self.assertEqual(3, len({a, b, c}))
            for folder in (a, b, c):
                self.assertTrue(Path(folder).is_dir(), folder)
                self.assertEqual(Path(tmp).resolve(), Path(folder).parent.resolve())
            self.assertIn("-test-", Path(a).name)
            self.assertIn("-prod-", Path(b).name)
            self.assertEqual(c + ".zip", c_zip)

    def test_cleanup_removes_the_dump_and_the_zip_and_tolerates_what_is_missing(self):
        with tempfile.TemporaryDirectory() as tmp:
            r = self.run_ps(
                f"$w = New-BackupWork -Environment test -Stamp '{STAMP}' -Temp '{tmp}'; "
                "Set-Content (Join-Path $w.Folder 'database.dump') 'plain'; Set-Content $w.Zip 'zip'; "
                "Remove-BackupWork $w; Remove-BackupWork $w; "
                "$x = New-BackupWork -Environment prod -Stamp 's' -Temp '" + tmp + "'; Remove-BackupWork $x; 'ok'")
            self.assertEqual(0, r.returncode, r.stderr)
            self.assertEqual("ok", r.stdout.strip())
            self.assertEqual([], list(Path(tmp).iterdir()))


if __name__ == "__main__":
    unittest.main()
