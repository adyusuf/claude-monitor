"""The control file: what the board page asks of the sessions (remove a task, disable a role, decide), and
which of those changes each session has already been told about. Written by the board server, read by the hook.
Split from board_store.py, which keeps the event log and the fold."""
from __future__ import annotations

import json
import sys
from pathlib import Path

from board_config import ACK_FILE, CHANGES_KEPT, CONTROL_FILE, ControlAction
from board_store import atomic_write, now_iso


def empty_control() -> dict:
    return {"version": 0, "removed_tasks": [], "disabled_roles": [], "changes": [], "decisions": {}}


def read_control(bdir: Path) -> dict:
    path = bdir / CONTROL_FILE
    if not path.exists():
        return empty_control()
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        print(f"board: unreadable control file, treating as empty: {exc}", file=sys.stderr)
        return empty_control()
    return {**empty_control(), **data}


def apply_control(bdir: Path, action: str, value: str,
                  choice: str | None = None, note: str | None = None) -> dict:
    ctl = read_control(bdir)
    if action == ControlAction.DECIDE:
        return _decide(bdir, ctl, value, choice, note)
    lists = {ControlAction.REMOVE_TASK: ("removed_tasks", True),
             ControlAction.RESTORE_TASK: ("removed_tasks", False),
             ControlAction.DISABLE_ROLE: ("disabled_roles", True),
             ControlAction.ENABLE_ROLE: ("disabled_roles", False)}
    if action not in lists:
        raise ValueError(f"unknown action: {action}")
    field, add = lists[action]
    items = set(ctl[field])
    items.add(value) if add else items.discard(value)
    ctl[field] = sorted(items)
    return record_change(bdir, ctl, {"action": action, "value": value})


def record_change(bdir: Path, ctl: dict, change: dict) -> dict:
    """Numbers a change, keeps the last CHANGES_KEPT, and writes the control file."""
    ctl["version"] += 1
    ctl["changes"] = (ctl["changes"] + [{"v": ctl["version"], "ts": now_iso(), **change}])[-CHANGES_KEPT:]
    atomic_write(bdir / CONTROL_FILE, ctl)
    return ctl


def _decide(bdir: Path, ctl: dict, tid: str, choice: str | None, note: str | None) -> dict:
    if not choice:
        raise ValueError("a decision needs a choice")
    decision = {"choice": choice, "note": note or "", "ts": now_iso(), "v": ctl["version"] + 1}
    ctl["decisions"][tid] = decision
    return record_change(bdir, ctl, {"action": ControlAction.DECIDE, "value": tid,
                                     "choice": choice, "note": note or "", "ts": decision["ts"]})


def _acks(bdir: Path) -> dict:
    ack_path = bdir / ACK_FILE
    try:
        return json.loads(ack_path.read_text(encoding="utf-8")) if ack_path.exists() else {}
    except (OSError, json.JSONDecodeError):
        return {}


def peek_changes(bdir: Path, session: str) -> list[dict]:
    """Control changes this session has not been told about yet — without marking them."""
    seen = _acks(bdir).get(session, 0)
    return [c for c in read_control(bdir)["changes"] if c["v"] > seen]


def unseen_changes(bdir: Path, session: str) -> list[dict]:
    """Control changes this session has not been told about yet; marks them as seen."""
    fresh = peek_changes(bdir, session)
    if fresh:
        acks = _acks(bdir)
        acks[session] = max(c["v"] for c in fresh)
        atomic_write(bdir / ACK_FILE, acks)
    return fresh
