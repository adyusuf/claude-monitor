"""deploy/windows/BackupCrypto.psm1 round trip with PowerShell 7: a file encrypted and decrypted comes back the same,
a wrong key or a changed byte fails the MAC check. Skipped (not passed) where pwsh is not installed."""
from __future__ import annotations

import os
import secrets
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

MODULE = Path(__file__).resolve().parents[2] / "deploy" / "windows" / "BackupCrypto.psm1"
PWSH = shutil.which("pwsh")


def key() -> str:
    import base64
    return base64.urlsafe_b64encode(secrets.token_bytes(32)).decode().rstrip("=")


@unittest.skipUnless(PWSH, "pwsh is not installed")
class BackupCrypto(unittest.TestCase):
    def run_ps(self, script: str) -> subprocess.CompletedProcess:
        return subprocess.run([PWSH, "-NoProfile", "-Command", f"Import-Module '{MODULE}'; $ErrorActionPreference='Stop'; {script}"],
                              capture_output=True, text=True)

    def test_round_trip_wrong_key_and_tampering(self):
        with tempfile.TemporaryDirectory() as tmp:
            plain, enc, back = (os.path.join(tmp, n) for n in ("plain.bin", "enc.cmbak", "back.bin"))
            data = secrets.token_bytes(3 * 1024 * 1024 + 17)
            Path(plain).write_bytes(data)
            k = key()
            r = self.run_ps(f"Protect-BackupFile -Source '{plain}' -Destination '{enc}' -Secret '{k}'")
            self.assertEqual(0, r.returncode, r.stderr)
            self.assertNotIn(data[:64], Path(enc).read_bytes())
            r = self.run_ps(f"Unprotect-BackupFile -Source '{enc}' -Destination '{back}' -Secret '{k}'")
            self.assertEqual(0, r.returncode, r.stderr)
            self.assertEqual(data, Path(back).read_bytes())

            r = self.run_ps(f"Unprotect-BackupFile -Source '{enc}' -Destination '{back}' -Secret '{key()}'")
            self.assertNotEqual(0, r.returncode)
            self.assertIn("authentication failed", r.stderr + r.stdout)

            raw = bytearray(Path(enc).read_bytes())
            raw[100] ^= 1
            Path(enc).write_bytes(bytes(raw))
            r = self.run_ps(f"Unprotect-BackupFile -Source '{enc}' -Destination '{back}' -Secret '{k}'")
            self.assertNotEqual(0, r.returncode)


if __name__ == "__main__":
    unittest.main()
