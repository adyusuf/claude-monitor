"""scripts/backcompat_scan.py against a throw-away repository: additive changes pass, breaking ones are named."""
from __future__ import annotations

import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "backcompat_scan.py"

CONTRACT = """namespace X;
public sealed record EventBatch(long BatchSeq, IReadOnlyList<CapturedEvent> Events);
public static class Kinds { public const string Stop = "stop"; }
"""
ENDPOINTS = """public static class E { public static void Map(RouteGroupBuilder api) {
    var g = api.MapGroup("/things");
    g.MapGet("/{id:guid}", Get);
    g.MapPost("/", Create);
} }
public sealed record ThingResponse(Guid Id, string Name);
"""
MIGRATION = """public partial class Initial : Migration {
    protected override void Up(MigrationBuilder m) { m.CreateTable(name: "things"); }
    protected override void Down(MigrationBuilder m) { m.DropTable(name: "things"); }
}
"""


class BackcompatScan(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.git("init", "-q", "-b", "dev")
        self.write("src/ClaudeMonitor.Contracts/Protocol.cs", CONTRACT)
        self.write("src/ClaudeMonitor.Api/Endpoints/Things.cs", ENDPOINTS)
        self.write("src/ClaudeMonitor.Api/Data/Migrations/001_Initial.cs", MIGRATION)
        self.git("add", "-A")
        self.git("-c", "user.name=t", "-c", "user.email=t@users.noreply.github.com", "commit", "-q", "-m", "base")
        self.git("tag", "base")

    def tearDown(self):
        self.tmp.cleanup()

    def git(self, *args):
        subprocess.run(["git", *args], cwd=self.root, check=True, capture_output=True)

    def write(self, rel, text):
        path = self.root / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")

    def scan(self, base="base"):
        return subprocess.run([sys.executable, str(SCRIPT), "--base", base], cwd=self.root, capture_output=True, text=True,
                              env={**os.environ, "GIT_CONFIG_NOSYSTEM": "1"})

    def assertBreaks(self, fragment):
        result = self.scan()
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertIn(fragment, result.stdout)

    def test_no_change_passes(self):
        result = self.scan()
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertIn("additive only", result.stdout)

    def test_appending_a_parameter_a_record_a_route_and_a_safe_migration_passes(self):
        self.write("src/ClaudeMonitor.Contracts/Protocol.cs",
                   CONTRACT.replace("Events);", "Events, string? Note = null);") + "public sealed record New(int A);\n")
        self.write("src/ClaudeMonitor.Api/Endpoints/Things.cs", ENDPOINTS.replace('g.MapPost("/", Create);',
                                                                                  'g.MapPost("/", Create);\n    g.MapDelete("/{id:guid}", Remove);'))
        self.write("src/ClaudeMonitor.Api/Data/Migrations/002_Add.cs",
                   MIGRATION.replace("CreateTable(name: \"things\")", "AddColumn<string>(name: \"note\")"))
        result = self.scan()
        self.assertEqual(0, result.returncode, result.stdout)

    def test_removing_a_parameter_breaks(self):
        self.write("src/ClaudeMonitor.Contracts/Protocol.cs", CONTRACT.replace(", IReadOnlyList<CapturedEvent> Events", ""))
        self.assertBreaks("record EventBatch: parameters changed")

    def test_retyping_or_reordering_breaks(self):
        self.write("src/ClaudeMonitor.Api/Endpoints/Things.cs", ENDPOINTS.replace("(Guid Id, string Name)", "(string Name, Guid Id)"))
        self.assertBreaks("record ThingResponse")

    def test_removing_a_record_breaks(self):
        self.write("src/ClaudeMonitor.Api/Endpoints/Things.cs", ENDPOINTS.replace("public sealed record ThingResponse(Guid Id, string Name);", ""))
        self.assertBreaks("record ThingResponse was removed")

    def test_changing_or_removing_a_constant_breaks(self):
        self.write("src/ClaudeMonitor.Contracts/Protocol.cs", CONTRACT.replace('"stop"', '"halt"'))
        self.assertBreaks("changed value")
        self.write("src/ClaudeMonitor.Contracts/Protocol.cs", CONTRACT.replace('public const string Stop = "stop";', ""))
        self.assertBreaks("was removed")

    def test_removing_a_route_breaks(self):
        self.write("src/ClaudeMonitor.Api/Endpoints/Things.cs", ENDPOINTS.replace('g.MapPost("/", Create);', ""))
        self.assertBreaks("route removed")

    def test_a_destructive_new_migration_breaks_but_down_is_ignored(self):
        self.write("src/ClaudeMonitor.Api/Data/Migrations/002_Drop.cs",
                   MIGRATION.replace('m.CreateTable(name: "things");', 'm.DropColumn(name: "name", table: "things");'))
        self.assertBreaks("destructive change in Up()")

    def test_editing_a_merged_migration_breaks(self):
        self.write("src/ClaudeMonitor.Api/Data/Migrations/001_Initial.cs", MIGRATION.replace("things", "stuff"))
        self.assertBreaks("a merged migration was edited")

    def test_an_unreadable_base_is_not_run(self):
        result = self.scan("no-such-ref")
        self.assertEqual(2, result.returncode)
        self.assertIn("NOT RUN", result.stdout)


if __name__ == "__main__":
    unittest.main()
