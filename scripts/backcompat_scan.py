#!/usr/bin/env python3
"""Backward-compatibility scan (global rule #4): the API, the agent protocol and the database only GROW.

    python3 scripts/backcompat_scan.py [--base <git ref>]      # default base: origin/dev

Compared with the base, the change must not:
  1. drop, rename or retype anything in a NEW migration's Up() (DropTable, DropColumn, RenameTable, RenameColumn,
     AlterColumn), nor edit or delete a migration the base already has (merged migrations are history);
  2. remove a record, or remove, reorder or retype a positional parameter of a record, or change the value of a
     public constant, in the contract (src/ClaudeMonitor.Contracts) or in the API's request/response records
     (src/ClaudeMonitor.Api/Endpoints); new parameters may only be added at the end;
  3. remove a route the API maps (MapGet/MapPost/... string literals in src/ClaudeMonitor.Api/Endpoints).

Exit codes: 0 additive only · 1 a breaking change (listed) · 2 the base could not be read (NOT RUN, blocks).
"""
from __future__ import annotations

import argparse
import re
import subprocess
import sys

MIGRATIONS = "src/ClaudeMonitor.Api/Data/Migrations/"
CONTRACT_DIRS = ("src/ClaudeMonitor.Contracts/", "src/ClaudeMonitor.Api/Endpoints/")
ROUTE_DIR = "src/ClaudeMonitor.Api/Endpoints/"
DESTRUCTIVE = re.compile(r"\.(DropTable|DropColumn|RenameTable|RenameColumn|AlterColumn)\b|\bDROP\s+(TABLE|COLUMN)\b|\bRENAME\b",
                         re.IGNORECASE)
RECORD = re.compile(r"record\s+(\w+)\s*\(([^)]*)\)", re.S)
CONST = re.compile(r"public\s+const\s+\w+\s+(\w+)\s*=\s*([^;]+);")
ROUTE = re.compile(r"\.Map(Get|Post|Put|Patch|Delete|Group)\(\s*\"([^\"]*)\"")


def git(*args: str) -> str:
    return subprocess.run(["git", *args], check=True, capture_output=True, text=True).stdout


def files_at(ref: str, prefix: str) -> dict[str, str]:
    """{path: content} of the .cs files under prefix at ref ("" = working tree via git ls-files)."""
    if ref:
        names = [n for n in git("ls-tree", "-r", "--name-only", ref, "--", prefix).splitlines() if n.endswith(".cs")]
        return {n: git("show", f"{ref}:{n}") for n in names}
    names = [n for n in git("ls-files", "--cached", "--others", "--exclude-standard", "--", prefix).splitlines()
             if n.endswith(".cs")]
    out = {}
    for n in names:
        try:
            with open(n, encoding="utf-8") as handle:
                out[n] = handle.read()
        except FileNotFoundError:
            continue
    return out


def up_body(source: str) -> str:
    start = source.find("void Up(")
    end = source.find("void Down(")
    return source[start:end if end > start else len(source)] if start >= 0 else ""


def params(text: str) -> list[tuple[str, str]]:
    """[(type, name)] of a positional record's parameters, defaults dropped."""
    out, depth, current = [], 0, ""
    for ch in text + ",":
        if ch in "<([" : depth += 1
        if ch in ">)]": depth -= 1
        if ch == "," and depth == 0:
            decl = current.split("=")[0].strip()
            if decl:
                parts = decl.rsplit(None, 1)
                out.append((" ".join(parts[0].split()), parts[1]) if len(parts) == 2 else ("", parts[0]))
            current = ""
        else:
            current += ch
    return out


def shapes(sources: dict[str, str]) -> tuple[dict, dict, set]:
    records, consts, routes = {}, {}, set()
    for path, text in sources.items():
        clean = re.sub(r"//[^\n]*", "", text)
        for name, body in RECORD.findall(clean):
            records[name] = params(body)
        for name, value in CONST.findall(clean):
            consts[f"{path}:{name}"] = value.strip()
        if path.startswith(ROUTE_DIR):
            routes.update(f"{path}:{verb}:{route}" for verb, route in ROUTE.findall(clean))
    return records, consts, routes


def scan(base: str) -> list[str]:
    problems = []
    old_migrations, new_migrations = files_at(base, MIGRATIONS), files_at("", MIGRATIONS)
    for path, text in old_migrations.items():
        if new_migrations.get(path) != text and not path.endswith("ModelSnapshot.cs"):
            problems.append(f"{path}: a merged migration was edited or deleted (add a new migration instead)")
    for path, text in new_migrations.items():
        if path not in old_migrations and not path.endswith(("Designer.cs", "ModelSnapshot.cs")):
            for line in up_body(text).splitlines():
                if DESTRUCTIVE.search(line):
                    problems.append(f"{path}: destructive change in Up(): {line.strip()[:100]}")

    old, new = {}, {}
    for prefix in CONTRACT_DIRS:
        old.update(files_at(base, prefix))
        new.update(files_at("", prefix))
    (old_records, old_consts, old_routes), (new_records, new_consts, new_routes) = shapes(old), shapes(new)
    for name, fields in old_records.items():
        if name not in new_records:
            problems.append(f"record {name} was removed")
        elif new_records[name][:len(fields)] != fields:
            problems.append(f"record {name}: parameters changed {fields} -> {new_records[name]} (only appending is allowed)")
    for key, value in old_consts.items():
        if key in new_consts and new_consts[key] != value:
            problems.append(f"constant {key} changed value {value} -> {new_consts[key]}")
        elif key not in new_consts and not any(k.endswith(":" + key.split(":")[1]) for k in new_consts):
            problems.append(f"constant {key} was removed")
    for route in sorted(old_routes - new_routes):
        if not any(r.split(":", 1)[1] == route.split(":", 1)[1] for r in new_routes):
            problems.append(f"route removed: {route.split(':', 1)[1]}")
    return problems


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--base", default="origin/dev")
    args = parser.parse_args(argv)
    try:
        git("rev-parse", "--verify", args.base)
    except subprocess.CalledProcessError:
        print(f"  NOT RUN: the base {args.base} cannot be read")
        return 2
    problems = scan(args.base)
    if problems:
        print(f"  ✗ {len(problems)} breaking change(s) against {args.base} (global #4):")
        for p in problems:
            print("    " + p)
        return 1
    print(f"  ✓ additive only against {args.base}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
