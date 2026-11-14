"""backup.ps1 makes its own backups folder (15/11/2026): a machine whose <env>\\backups folder was never created (set up
before setup-server.ps1 did it, or the folder was deleted) failed every nightly run with DirectoryNotFound and no backup
was taken. Get-BackupFolder creates the folder when it is missing, and backup.ps1 asks for it before it writes anything.
Skipped (not passed) where pwsh is not installed."""
from __future__ import annotations

import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

WINDOWS = Path(__file__).resolve().parents[2] / "deploy" / "windows"
MODULE = WINDOWS / "BackupCrypto.psm1"
BACKUP = WINDOWS / "backup.ps1"
PWSH = shutil.which("pwsh")


@unittest.skipUnless(PWSH, "pwsh is not installed")
class BackupFolder(unittest.TestCase):
    def run_ps(self, script: str) -> subprocess.CompletedProcess:
        return subprocess.run([PWSH, "-NoProfile", "-Command", f"Import-Module '{MODULE}'; $ErrorActionPreference='Stop'; {script}"],
                              capture_output=True, text=True)

    def test_a_missing_backups_folder_is_created_and_its_path_returned(self):
        with tempfile.TemporaryDirectory() as tmp:
            r = self.run_ps(f"Get-BackupFolder '{tmp}'")
            self.assertEqual(r.returncode, 0, r.stderr)
            self.assertTrue((Path(tmp) / "backups").is_dir())
            self.assertEqual(Path(r.stdout.strip()), Path(tmp) / "backups")

    def test_an_existing_backups_folder_keeps_its_files(self):
        with tempfile.TemporaryDirectory() as tmp:
            folder = Path(tmp) / "backups"
            folder.mkdir()
            (folder / "prod-2026-11-14T000000Z.cmbak").write_bytes(b"older backup")
            r = self.run_ps(f"Get-BackupFolder '{tmp}'")
            self.assertEqual(r.returncode, 0, r.stderr)
            self.assertEqual((folder / "prod-2026-11-14T000000Z.cmbak").read_bytes(), b"older backup")


class BackupScript(unittest.TestCase):
    def test_backup_asks_for_the_folder_before_it_writes_anything(self):
        text = BACKUP.read_text(encoding="utf-8")
        self.assertIn("Get-BackupFolder", text)
        self.assertLess(text.index("Get-BackupFolder"), text.index("Protect-BackupFile"))
        self.assertLess(text.index("Get-BackupFolder"), text.index("pg_dump.exe"))


if __name__ == "__main__":
    unittest.main()
