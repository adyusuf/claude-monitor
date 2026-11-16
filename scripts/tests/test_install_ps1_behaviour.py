"""scripts/install/aipod-install.ps1 run by pwsh against a local file:/// download host (F-209), mirroring test_install_script.py.

Off Windows the script is told it runs on Windows (OS=Windows_NT) and the Windows-only cmdlets it asks about an older agent are stubbed to find
none. What runs: the build choice from the manifest, the SHA-256 refusal, the carry-over search and the login decision. Not run here: the
Credential Manager copy and the unpacking (Windows paths), which need a Windows runner (F-328). Skipped (not passed) where pwsh is not installed."""
from __future__ import annotations

import hashlib
import importlib.util
import io
import json
import os
import shutil
import stat
import subprocess
import tempfile
import unittest
import zipfile
from pathlib import Path

_spec = importlib.util.spec_from_file_location("install_support", Path(__file__).resolve().parent / "install_support.py")
install_support = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(install_support)
INSTALL = Path(install_support.assembled(install_support.PS1))  # the file the download host serves, joined from the step files (F-332)
PWSH = shutil.which("pwsh")
SERVER = "https://aipod.invalid"
PLAIN = "$PSStyle.OutputRendering = 'PlainText'; $ErrorView = 'ConciseView'; "
STUBS = "function Get-ScheduledTask { }; function Get-Service { }; function Get-CimInstance { }; "


def zip_bytes() -> bytes:
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as archive:
        archive.writestr("aipod.exe", b"MZ fake")
    return buffer.getvalue()


@unittest.skipUnless(PWSH, "pwsh is not installed")
class InstallerBehaviour(unittest.TestCase):
    def setUp(self):
        self.temp = Path(tempfile.mkdtemp(prefix="aipod_ps1_"))
        self.host = self.temp / "host"
        self.profile = self.temp / "profile"
        self.home = self.profile / ".aipod"
        for folder in (self.host, self.profile, self.temp / "local", self.temp / "claude"):
            folder.mkdir(parents=True)
        self.calls = self.temp / "calls"

    def tearDown(self):
        shutil.rmtree(self.temp)

    def publish(self, entries, files=None):
        (self.host / "manifest.json").write_text(json.dumps({"format": "aipod-update/1", "entries": entries}))
        for name, data in (files or {}).items():
            (self.host / name).write_bytes(data)

    @staticmethod
    def entry(version, sha256="0" * 64, os_name="windows", arch="x64", file=None):
        return {"os": os_name, "arch": arch, "version": version, "file": file or f"aipod-{os_name}-{arch}.zip", "sha256": sha256}

    def fake_installed(self, version, status=""):
        """An installed aipod.exe (PowerShell maps the script's 'bin\\aipod.exe' to bin/aipod.exe here) that logs what it is asked."""
        binary = self.home / "bin" / "aipod.exe"
        binary.parent.mkdir(parents=True, exist_ok=True)
        binary.write_text(f'#!/bin/sh\necho "$*" >> "{self.calls}"\ncase "$1" in version) echo {version} ;; status) printf "%s\\n" "{status}" ;; esac\nexit 0\n')
        binary.chmod(binary.stat().st_mode | stat.S_IXUSR)

    def run_installer(self, *args, arch="AMD64"):
        env = {"OS": "Windows_NT", "PROCESSOR_ARCHITECTURE": arch, "USERPROFILE": str(self.profile), "LOCALAPPDATA": str(self.temp / "local"),
               "AP_AGENT_HOME": str(self.home), "CLAUDE_CONFIG_DIR": str(self.temp / "claude"), "PATH": "/usr/bin:/bin", "HOME": str(self.profile),
               "TMPDIR": str(self.temp)}
        quoted = " ".join("'" + a.replace("'", "''") + "'" if not a.startswith("-") else a for a in args)
        command = f"{PLAIN}{STUBS}& '{INSTALL}' -From '{self.host.as_uri()}' {quoted}"
        result = subprocess.run([PWSH, "-NoProfile", "-NonInteractive", "-Command", command], capture_output=True, text=True, env=env, timeout=120)
        return result.returncode, result.stdout + result.stderr

    def logged(self):
        return self.calls.read_text().splitlines() if self.calls.exists() else []

    def test_a_download_whose_sha256_is_not_the_manifests_is_refused_and_nothing_is_installed(self):
        self.publish([self.entry("1.2.3", sha256="ab" * 32)], {"aipod-windows-x64.zip": zip_bytes()})
        code, out = self.run_installer("-Yes", "-NoPlugin", "-NoLogin")
        self.assertNotEqual(0, code, out)
        self.assertIn("refused: aipod-windows-x64.zip does not match the SHA-256", " ".join(out.split()))
        self.assertFalse((self.home / "bin" / "aipod.exe").exists())

    def test_a_download_that_matches_is_unpacked_and_installed(self):
        data = zip_bytes()
        self.publish([self.entry("1.2.3", sha256=hashlib.sha256(data).hexdigest())], {"aipod-windows-x64.zip": data})
        code, out = self.run_installer("-Yes", "-NoPlugin", "-NoLogin")
        self.assertEqual(0, code, out)
        self.assertEqual(b"MZ fake", (self.home / "bin" / "aipod.exe").read_bytes())

    def test_the_newest_build_for_this_cpu_is_chosen_by_version_not_by_text(self):
        self.publish([self.entry("1.9.0"), self.entry("1.10.0"), self.entry("9.9.9", arch="arm64"), self.entry("9.9.9", os_name="macos")])
        code, out = self.run_installer("-DryRun", "-NoPlugin")
        self.assertEqual(0, code, out)
        self.assertIn("newest build: aipod 1.10.0 (windows-x64)", out)
        self.assertIn("[dry run] aipod-windows-x64.zip would be downloaded", out)
        self.assertFalse(self.home.exists() and any(self.home.iterdir()))

    def test_no_build_for_this_cpu_and_a_file_name_that_is_not_a_build_are_refused(self):
        self.publish([self.entry("1.2.3")])
        code, out = self.run_installer("-DryRun", arch="ARM64")
        self.assertNotEqual(0, code)
        self.assertIn("the download host has no build for windows-arm64", " ".join(out.split()))

        self.publish([self.entry("1.2.3", file="../aipod-windows-x64.zip")])
        code, out = self.run_installer("-DryRun")
        self.assertNotEqual(0, code)
        self.assertIn("is not a build file name", " ".join(out.split()))

    def test_the_old_home_is_found_for_the_carry_over_and_a_dry_run_copies_nothing(self):
        old = self.profile / ".claude-monitor"
        old.mkdir()
        (old / "agent.json").write_text(json.dumps({"server": SERVER, "machineKey": "k" * 24}))
        self.publish([self.entry("1.2.3")])
        code, out = self.run_installer("-DryRun", "-NoPlugin")
        self.assertEqual(0, code, out)
        self.assertIn(f"Carrying the old settings and login over from {old}", out)
        self.assertIn(f"[dry run] agent.json and the tokens would be copied into {self.home}", out)
        self.assertFalse((self.home / "agent.json").exists())

    def test_an_up_to_date_machine_that_is_not_connected_logs_in_to_the_server_asked_for(self):
        self.fake_installed("1.2.3")
        self.publish([self.entry("1.2.3")])
        code, out = self.run_installer("-Server", SERVER, "-NoPlugin")
        self.assertEqual(0, code, out)
        self.assertIn("aipod 1.2.3 is installed and is the newest build.", out)
        self.assertIn("Connecting this machine", out)
        self.assertEqual(["version", "status", f"login --server {SERVER}", "restart", "config push inbox"], self.logged())

    def test_a_machine_already_connected_to_that_server_keeps_its_login(self):
        self.fake_installed("1.2.3", status=f"connected: {SERVER}/")
        self.publish([self.entry("1.2.3")])
        code, out = self.run_installer("-Server", SERVER, "-NoPlugin")
        self.assertEqual(0, code, out)
        self.assertIn(f"This machine is already connected to {SERVER} (its login was kept).", out)
        self.assertNotIn(f"login --server {SERVER}", self.logged())
        self.assertIn("restart", self.logged())


if __name__ == "__main__":
    unittest.main()
