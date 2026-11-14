"""scripts/zip_package.py packs the release package where `zip` is not installed (Git Bash on the Windows server has no zip,
15/11/2026): the same members and bytes as `zip -r`, with the folders relative to the staging folder."""
from __future__ import annotations

import os
import subprocess
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "zip_package.py"


class ZipPackage(unittest.TestCase):
    def pack(self, stage: Path, out: Path, *members: str) -> subprocess.CompletedProcess:
        return subprocess.run([sys.executable, str(SCRIPT), str(stage), str(out), *members], capture_output=True, text=True)

    def test_packs_folders_and_files_relative_to_the_stage(self):
        with tempfile.TemporaryDirectory() as tmp:
            stage = Path(tmp) / "stage"
            (stage / "api" / "bin").mkdir(parents=True)
            (stage / "web").mkdir()
            (stage / "api" / "Podium.Api.exe").write_bytes(b"exe")
            (stage / "api" / "bin" / "x.dll").write_bytes(b"dll")
            (stage / "web" / "index.html").write_text("<html></html>", encoding="utf-8")
            (stage / "release.json").write_text('{"commit":"abc"}\n', encoding="utf-8")
            out = Path(tmp) / "podium-abc.zip"
            r = self.pack(stage, out, "api", "web", "release.json")
            self.assertEqual(r.returncode, 0, r.stderr)
            with zipfile.ZipFile(out) as z:
                self.assertIsNone(z.testzip())
                self.assertEqual(sorted(n for n in z.namelist() if not n.endswith("/")),
                                 ["api/Podium.Api.exe", "api/bin/x.dll", "release.json", "web/index.html"])
                self.assertEqual(z.read("api/bin/x.dll"), b"dll")
                self.assertEqual(z.read("release.json"), b'{"commit":"abc"}\n')

    def test_keeps_an_empty_folder(self):
        with tempfile.TemporaryDirectory() as tmp:
            stage = Path(tmp) / "stage"
            (stage / "web" / "empty").mkdir(parents=True)
            out = Path(tmp) / "p.zip"
            self.assertEqual(self.pack(stage, out, "web").returncode, 0)
            with zipfile.ZipFile(out) as z:
                self.assertIn("web/empty/", z.namelist())

    def test_a_missing_member_fails_and_leaves_no_package(self):
        with tempfile.TemporaryDirectory() as tmp:
            stage = Path(tmp) / "stage"
            stage.mkdir()
            out = Path(tmp) / "p.zip"
            r = self.pack(stage, out, "api")
            self.assertNotEqual(r.returncode, 0)
            self.assertIn("api is not in the stage", r.stderr)
            self.assertFalse(out.exists())


    def test_a_file_dated_before_1980_is_packed_like_zip_does(self):
        # NuGet content carries a 1980-01-01 00:00 UTC date that reads as 1979 west of UTC; zip packs it, zipfile refuses by default.
        with tempfile.TemporaryDirectory() as tmp:
            stage = Path(tmp) / "stage"
            (stage / "api").mkdir(parents=True)
            old = stage / "api" / "old.dll"
            old.write_bytes(b"dll")
            os.utime(old, (0, 0))
            out = Path(tmp) / "p.zip"
            r = self.pack(stage, out, "api")
            self.assertEqual(r.returncode, 0, r.stderr)
            with zipfile.ZipFile(out) as z:
                self.assertEqual(z.read("api/old.dll"), b"dll")

    def test_a_failure_while_packing_leaves_no_partial_file(self):
        with tempfile.TemporaryDirectory() as tmp:
            stage = Path(tmp) / "stage"
            stage.mkdir()
            (stage / "api").write_bytes(b"x")
            out = Path(tmp) / "no-such-folder" / "p.zip"  # the package cannot be created here
            r = self.pack(stage, out, "api")
            self.assertNotEqual(r.returncode, 0)
            self.assertEqual(list(Path(tmp).rglob("*.partial")), [])

if __name__ == "__main__":
    unittest.main()
