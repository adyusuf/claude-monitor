"""Machine-wide list of projects that have a live board, so ONE server shows them all
(docs/live-board.md). Each project still keeps its own data in
<main checkout>/.claude/board; this file only records where those boards are.
"""
from __future__ import annotations

import fcntl
import hashlib
import json
import sys
from contextlib import contextmanager
from pathlib import Path

from board_config import REGISTRY
from board_store import atomic_write, fold, now_iso, read_control, read_events


def project_id(root: Path) -> str:
    return hashlib.sha1(str(root).encode("utf-8")).hexdigest()[:10]


@contextmanager
def _locked(path: Path):
    """Serialise writers: two sessions starting at once must not drop each other."""
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path.with_suffix(".lock"), "a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        try:
            yield
        finally:
            fcntl.flock(lock, fcntl.LOCK_UN)


def load(path: Path | None = None) -> dict:
    path = path or REGISTRY  # resolved per call, so tests can point it at a temporary file
    if not path.exists():
        return {}
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        print(f"board: unreadable registry {path}, treating as empty: {exc}", file=sys.stderr)
        return {}
    return data if isinstance(data, dict) else {}


def register(root: Path, bdir: Path, path: Path | None = None) -> dict:
    path = path or REGISTRY
    root = root.resolve()
    entry = {"id": project_id(root), "name": root.name, "root": str(root),
             "dir": str(bdir.resolve()), "registered": now_iso()}
    with _locked(path):
        data = load(path)
        data[entry["id"]] = entry
        atomic_write(path, data)
    return entry


def register_if_missing(root: Path, bdir: Path, path: Path | None = None) -> bool:
    """List this project on the live page unless it already is. Called by everything that WRITES a
    board (the CLI, the hook), so a repository whose SessionStart hook is missing or has not run
    still shows up: visibility must not depend on the one hook that used to be the only registrar.
    A read of the registry, no write, when the project is there already. True = it was added."""
    if project_id(root.resolve()) in load(path):
        return False
    register(root, bdir, path)
    return True


def summary(entry: dict) -> dict:
    """One tab's worth of numbers for a project, read from its own board."""
    bdir = Path(entry["dir"])
    state = fold(read_events(bdir), read_control(bdir))
    tasks = list(state["tasks"].values())

    def count(*statuses: str) -> int:
        return sum(1 for t in tasks if t["status"] in statuses)

    return {**entry, "mode": state["mode"], "last_event": state["last_event"],
            "turn_open": any(s["turn_open"] for s in state["sessions"].values()),
            "running": count("running"), "waiting": count("waiting", "agent_done"),
            "done": count("done"), "total": len(state["tasks"])}
