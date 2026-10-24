"""deploy/windows/PodiumOps.psm1 (the SMTP settings of check-health.ps1), run in real PowerShell. Skipped (not passed)
where no PowerShell is installed. Every test runs under PowerShell 7 and, where present, Windows PowerShell 5.1 as well:
the scheduled health check runs powershell.exe.

Pinned: the hourly alarm mail read AIPOD_SMTP_* from aipod.env alone, while the API reads each field from the env key
first and else from the "Smtp" node of the server-only appsettings.Production.json (ApiConfig.SmtpValue). 58 alarm
mails in 7 days were refused (event 1021) on 24/10/2026.
"""
from __future__ import annotations

import json
import os
import shutil
import subprocess
import tempfile
import time
import unittest
from pathlib import Path

MODULE = Path(__file__).resolve().parents[2] / "deploy" / "windows" / "PodiumOps.psm1"
SHELLS = [s for s in (shutil.which("pwsh"), shutil.which("powershell")) if s]
COMMIT_A, COMMIT_B, COMMIT_C = "a" * 40, "b" * 40, "c" * 40


def run(shell: str, script: str) -> subprocess.CompletedProcess:
    command = f"$ErrorActionPreference = 'Stop'; Import-Module '{MODULE}'; {script}"
    return subprocess.run([shell, "-NoProfile", "-Command", command], capture_output=True, text=True)


@unittest.skipUnless(SHELLS, "no PowerShell is installed")
class PowerShellCase(unittest.TestCase):
    def ps(self, script: str) -> object:
        """The JSON the script prints; the same in every available shell."""
        first = None
        for index, shell in enumerate(SHELLS):
            result = run(shell, script)
            self.assertEqual(0, result.returncode, f"{Path(shell).name}: {result.stdout}{result.stderr}")
            out = json.loads(result.stdout)
            if index == 0:
                first = out
            else:
                self.assertEqual(first, out, f"{Path(shell).name} differs from {Path(SHELLS[0]).name}")
        return first

    def ps_error(self, script: str) -> str:
        """The message of a script that must fail in every available shell."""
        message = ""
        for shell in SHELLS:
            result = run(shell, script)
            self.assertNotEqual(0, result.returncode, f"{Path(shell).name} did not throw: {result.stdout}")
            message = message or result.stdout + result.stderr
        return message


class SmtpSettings(PowerShellCase):
    NODE = {"Smtp": {"Host": "smtp.node", "Port": 587, "User": "node-user", "Password": "node-pass",
                     "From": "node@from", "StartTls": True}}
    BASE = {"AIPOD_SMTP_HOST": "h", "AIPOD_SMTP_PORT": "25", "AIPOD_SMTP_FROM": "f@x"}

    def setUp(self):
        self.dir = Path(tempfile.mkdtemp()).resolve()
        self.addCleanup(shutil.rmtree, self.dir, True)
        self.settings = self.dir / "appsettings.Production.json"
        self.settings.write_text(json.dumps(self.NODE), encoding="utf-8")

    @staticmethod
    def table(values: dict) -> str:
        return "@{" + "; ".join(f"'{k}' = '{v}'" for k, v in values.items()) + "}"

    def smtp(self, values: dict, path: Path | None = None) -> dict:
        where = f"-AppSettingsPath '{path}'" if path else ""
        return self.ps(f"Get-SmtpSettings -Values {self.table(values)} {where} | ConvertTo-Json -Compress")

    def test_env_values_win_per_field(self):
        got = self.smtp({"AIPOD_SMTP_HOST": "smtp.env", "AIPOD_SMTP_PORT": "465", "AIPOD_SMTP_USER": "env-user",
                         "AIPOD_SMTP_PASSWORD": "env-pass", "AIPOD_SMTP_FROM": "env@from", "AIPOD_SMTP_TLS": "false"},
                        self.settings)
        self.assertEqual(("smtp.env", 465, "env-user", "env-pass", "env@from", False),
                         (got["Host"], got["Port"], got["User"], got["Password"], got["From"], got["StartTls"]))

    def test_empty_env_values_fall_back_to_the_node(self):
        got = self.smtp({"AIPOD_SMTP_HOST": "", "AIPOD_SMTP_PORT": "", "AIPOD_SMTP_USER": "", "AIPOD_SMTP_PASSWORD": "",
                         "AIPOD_SMTP_FROM": "", "AIPOD_SMTP_TLS": ""}, self.settings)
        self.assertEqual(("smtp.node", 587, "node-user", "node-pass", "node@from", True),
                         (got["Host"], got["Port"], got["User"], got["Password"], got["From"], got["StartTls"]))

    def test_mixed_per_field_is_the_server_today(self):
        # host, port, tls and sender in the environment file; user and password empty there, only in the node.
        got = self.smtp({"AIPOD_SMTP_HOST": "smtp.env", "AIPOD_SMTP_PORT": "465", "AIPOD_SMTP_TLS": "true",
                         "AIPOD_SMTP_USER": "", "AIPOD_SMTP_PASSWORD": "", "AIPOD_SMTP_FROM": "env@from"}, self.settings)
        self.assertEqual(("smtp.env", 465, "env@from"), (got["Host"], got["Port"], got["From"]))
        self.assertEqual(("node-user", "node-pass"), (got["User"], got["Password"]))

    def test_starttls_follows_the_api_unset_is_on_and_only_true_keeps_it_on(self):
        self.assertTrue(self.smtp(self.BASE)["StartTls"], "unset: on, the production default")
        self.assertTrue(self.smtp({**self.BASE, "AIPOD_SMTP_TLS": "true"})["StartTls"])
        self.assertTrue(self.smtp({**self.BASE, "AIPOD_SMTP_TLS": "TRUE"})["StartTls"])
        # ApiConfig turns TLS off for every set value that is not "true": a plain relay written as 0/no/off, or a typo.
        for value in ("false", "FALSE", "0", "no", "off", "fasle"):
            self.assertFalse(self.smtp({**self.BASE, "AIPOD_SMTP_TLS": value})["StartTls"], value)

    def test_starttls_from_the_node_as_a_json_boolean_or_a_string(self):
        for value in (False, "False", "false", "0"):
            self.settings.write_text(json.dumps({"Smtp": {**self.NODE["Smtp"], "StartTls": value}}), encoding="utf-8")
            self.assertFalse(self.smtp({}, self.settings)["StartTls"], repr(value))
        for value in (True, "True", "true"):
            self.settings.write_text(json.dumps({"Smtp": {**self.NODE["Smtp"], "StartTls": value}}), encoding="utf-8")
            self.assertTrue(self.smtp({}, self.settings)["StartTls"], repr(value))

    def test_a_missing_file_leaves_only_the_env(self):
        got = self.smtp({**self.BASE, "AIPOD_SMTP_HOST": "smtp.env"}, self.dir / "missing.json")
        self.assertEqual("smtp.env", got["Host"])
        self.assertIsNone(got["User"])
        self.assertIsNone(got["Password"])

    def test_unresolved_host_port_or_sender_throw_and_name_the_field(self):
        missing = self.dir / "missing.json"
        for key, word in (("AIPOD_SMTP_HOST", "host"), ("AIPOD_SMTP_PORT", "port"), ("AIPOD_SMTP_FROM", "sender")):
            values = {k: v for k, v in self.BASE.items() if k != key}
            message = self.ps_error(f"Get-SmtpSettings -Values {self.table(values)} -AppSettingsPath '{missing}'")
            self.assertIn(word, message)
            self.assertIn("not set", message)
        self.assertIn("no appsettings.Production.json found", self.ps_error("Get-SmtpSettings -Values @{}"))

    def test_a_port_that_is_not_a_number_throws(self):
        message = self.ps_error(f"Get-SmtpSettings -Values {self.table({**self.BASE, 'AIPOD_SMTP_PORT': 'x'})}")
        self.assertIn("not a number", message)

    def test_an_unreadable_file_throws_without_quoting_it(self):
        self.settings.write_text("{ not json SECRET-VALUE", encoding="utf-8")
        message = self.ps_error(f"Get-SmtpSettings -Values @{{}} -AppSettingsPath '{self.settings}'")
        self.assertIn("could not be parsed", message)
        self.assertNotIn("SECRET-VALUE", message)

    def test_a_file_that_cannot_be_read_says_read_not_parse(self):
        # A directory where the file should be: present for Test-Path, impossible to read as text.
        unreadable = self.dir / "unreadable.json"
        unreadable.mkdir()
        message = self.ps_error(f"Get-SmtpSettings -Values @{{}} -AppSettingsPath '{unreadable}'")
        self.assertIn("could not be read", message)
        self.assertNotIn("could not be parsed", message)


class FindAppSettings(PowerShellCase):
    def setUp(self):
        self.home = Path(tempfile.mkdtemp()).resolve()  # long form: PowerShell returns it
        self.addCleanup(shutil.rmtree, self.home, True)
        (self.home / "releases" / "empty").mkdir(parents=True)

    def release(self, commit: str, with_file: bool, age: int = 0) -> str:
        """A release folder `age` seconds old; returns the path its appsettings.Production.json has (or would have)."""
        api = self.home / "releases" / commit / "api"
        api.mkdir(parents=True)
        file = api / "appsettings.Production.json"
        if with_file:
            file.write_text("{}", encoding="utf-8")
        stamp = time.time() - age
        os.utime(api.parent, (stamp, stamp))
        return str(file)

    def find(self, commit: str = "") -> str | None:
        return self.ps(f"$p = Find-AppSettings -EnvironmentHome '{self.home}' -Commit '{commit}'; "
                       "if ($p) { $p | ConvertTo-Json -Compress } else { 'null' }")

    def test_the_release_the_api_reported_comes_first_even_if_older(self):
        reported = self.release(COMMIT_A, True, age=7200)
        self.release(COMMIT_B, True, age=0)
        self.assertEqual(reported, self.find(COMMIT_A))

    def test_without_the_file_in_that_release_the_newest_one_that_has_it(self):
        self.release(COMMIT_A, True, age=7200)
        newer = self.release(COMMIT_B, True, age=3600)
        self.release(COMMIT_C, False, age=0)  # the live release, newest, but without the file
        self.assertEqual(newer, self.find(COMMIT_C))

    def test_no_commit_reported_takes_the_newest_with_the_file(self):
        self.release(COMMIT_A, True, age=7200)
        newer = self.release(COMMIT_B, True, age=3600)
        self.assertEqual(newer, self.find(""))

    def test_no_release_has_it_is_null_not_an_error(self):
        self.release(COMMIT_A, False)
        self.assertIsNone(self.find(COMMIT_A))

    def test_a_commit_that_is_not_40_hex_is_ignored(self):
        newer = self.release(COMMIT_B, True)
        self.assertEqual(newer, self.find("..\\..\\x"))

    def test_no_releases_folder_at_all_is_null(self):
        shutil.rmtree(self.home / "releases")
        self.assertIsNone(self.find(COMMIT_A))


if __name__ == "__main__":
    unittest.main()
