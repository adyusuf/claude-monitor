#!/usr/bin/env python3
"""Does the suite notice a fault? Mutation check for one module - run by hand before a release, not in the gate.

    python3 scripts/mutation_check.py scripts/board/board_store.py --tests test_board_store test_board_fuzz

It copies `scripts/` to a temporary directory (the working tree is never touched), makes ONE small change at a
time to the chosen module - a comparison flipped, `and` swapped with `or`, a boolean inverted, a number moved by
one, a `not` dropped - and runs the named test modules against each change. A change the tests notice is
KILLED; one they do not is a SURVIVOR: the line was run (coverage counts it) but nothing checks what it does
(global rule #29: an added test must catch a fault under mutation). Survivors are listed with the line and the
change, so a test can be written for each - or the change judged harmless (an equivalent mutant).

Exit codes: 0 done (survivors are a finding, not a failure unless --min-score is given) · 1 below --min-score ·
2 usage, or the unmutated suite is already red (a mutation score on a red suite means nothing).
"""
from __future__ import annotations

import argparse
import ast
import copy
import random
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

COMPARE = {ast.Eq: ast.NotEq, ast.NotEq: ast.Eq, ast.Lt: ast.GtE, ast.GtE: ast.Lt, ast.Gt: ast.LtE,
           ast.LtE: ast.Gt, ast.In: ast.NotIn, ast.NotIn: ast.In, ast.Is: ast.IsNot, ast.IsNot: ast.Is}
BOOL = {ast.And: ast.Or, ast.Or: ast.And}
TIMEOUT_S = 120


def _sites(tree: ast.AST) -> list[tuple[str, ast.AST, int]]:
    """Every place a change can be made: (kind, node, operator index), in source order."""
    found = []
    for node in ast.walk(tree):
        if isinstance(node, ast.Compare):
            found += [("compare", node, i) for i, op in enumerate(node.ops) if type(op) in COMPARE]
        elif isinstance(node, ast.BoolOp) and type(node.op) in BOOL:
            found.append(("boolop", node, 0))
        elif isinstance(node, ast.Constant) and isinstance(node.value, bool):
            found.append(("bool", node, 0))
        elif isinstance(node, ast.Constant) and isinstance(node.value, int):
            found.append(("number", node, 0))
        elif isinstance(node, ast.UnaryOp) and isinstance(node.op, ast.Not):
            found.append(("not", node, 0))
    found.sort(key=lambda s: (getattr(s[1], "lineno", 0), getattr(s[1], "col_offset", 0), s[2]))
    return found


def count(source: str) -> int:
    return len(_sites(ast.parse(source)))


def mutate(source: str, index: int) -> tuple[str, int, str]:
    """Source with the `index`-th site changed -> (new source, line number, what was done)."""
    tree = ast.parse(source)
    kind, node, i = _sites(tree)[index]
    line = getattr(node, "lineno", 0)
    if kind == "compare":
        old = type(node.ops[i]).__name__
        node.ops[i] = COMPARE[type(node.ops[i])]()
        what = f"{old} -> {type(node.ops[i]).__name__}"
    elif kind == "boolop":
        what = f"{type(node.op).__name__} -> {BOOL[type(node.op)].__name__}"
        node.op = BOOL[type(node.op)]()
    elif kind == "bool":
        what = f"{node.value} -> {not node.value}"
        node.value = not node.value
    elif kind == "number":
        what = f"{node.value} -> {node.value + 1}"
        node.value += 1
    else:
        what = "`not` removed"
        node_copy = copy.copy(node.operand)
        node.__class__, node.__dict__ = node_copy.__class__, node_copy.__dict__
    return ast.unparse(ast.fix_missing_locations(tree)) + "\n", line, what


def run_tests(root: Path, modules: list[str], timeout: int = TIMEOUT_S) -> bool:
    """True when the named test modules pass in `root/scripts/tests`. A hang counts as a failure."""
    try:
        done = subprocess.run([sys.executable, "-m", "unittest", *modules], cwd=root / "scripts" / "tests",
                              capture_output=True, timeout=timeout)
    except subprocess.TimeoutExpired:
        return False
    return done.returncode == 0


def check(target: Path, modules: list[str], limit: int | None, seed: int, runner=run_tests) -> dict:
    """Mutate `target` (a file below a `scripts/` folder) and report -> {total, killed, survivors}."""
    scripts = next(p for p in target.resolve().parents if p.name == "scripts")
    relative = target.resolve().relative_to(scripts.parent)
    source = target.read_text(encoding="utf-8")
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        shutil.copytree(scripts, root / "scripts", ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
        copy_of = root / relative
        if not runner(root, modules):
            raise SystemExit("the unmutated suite is not green: fix it before measuring")
        picks = list(range(count(source)))
        if limit is not None and limit < len(picks):
            picks = sorted(random.Random(seed).sample(picks, limit))
        survivors, killed = [], 0
        for index in picks:
            changed, line, what = mutate(source, index)
            copy_of.write_text(changed, encoding="utf-8")
            if runner(root, modules):
                survivors.append({"line": line, "change": what, "text": source.splitlines()[line - 1].strip()})
            else:
                killed += 1
        copy_of.write_text(source, encoding="utf-8")
    return {"total": len(picks), "killed": killed, "survivors": survivors}


def main(argv: list[str] | None = None, runner=run_tests) -> int:
    ap = argparse.ArgumentParser(description="Mutation check for one module (see the module docstring).")
    ap.add_argument("module", type=Path, help="the file to mutate, below a scripts/ folder")
    ap.add_argument("--tests", nargs="+", required=True, help="test module names (scripts/tests/<name>.py without .py)")
    ap.add_argument("--max", type=int, default=None, help="try at most this many changes (random, see --seed)")
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--min-score", type=float, default=None, help="exit 1 when killed/total is lower (0-1)")
    args = ap.parse_args(argv)
    if not args.module.is_file():
        print(f"no such file: {args.module}", file=sys.stderr)
        return 2
    try:
        result = check(args.module, args.tests, args.max, args.seed, runner)
    except StopIteration:
        print("the module must be below a folder named scripts/", file=sys.stderr)
        return 2
    except SystemExit as exc:
        print(exc, file=sys.stderr)
        return 2
    total, killed = result["total"], result["killed"]
    score = killed / total if total else 1.0
    for s in result["survivors"]:
        print(f"SURVIVOR {args.module}:{s['line']}  {s['change']}   | {s['text']}")
    print(f"{killed}/{total} changes caught ({score:.0%}); {len(result['survivors'])} survivor(s)")
    return 1 if args.min_score is not None and score < args.min_score else 0


if __name__ == "__main__":
    sys.exit(main())
