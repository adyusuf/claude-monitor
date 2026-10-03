"""Which queued tasks a channel server already pushed, so the hooks do not deliver them twice.

A push is only a push once the session acted on it: Claude Code drops a channel message
silently when the channel is gated (org policy, protocol) — the server cannot see that. So a
pushed change is held back from the hooks for CHANNEL_CONFIRM_S and stays held back only if
the session then recorded a turn event (`confirm`, called by the hooks). Unconfirmed after
that, the hooks deliver it like any other queued task — a task is never lost (#6, docs/live-board-sessions.md §5).
"""
from __future__ import annotations

import json
import time
from pathlib import Path

from board_config import (ACK_FILE, CHANNEL_CONFIRM_S, CHANNEL_DELIVERED_FILE, CHANNEL_DELIVERED_KEPT,
                          ControlAction)
from board_store import atomic_write
from board_control import _acks
from board_control import peek_changes as _peek


def _read(bdir: Path) -> dict:
    try:
        data = json.loads((bdir / CHANNEL_DELIVERED_FILE).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}
    return data if isinstance(data, dict) else {}


def mark(bdir: Path, session: str, version: int, now: float | None = None) -> None:
    """Records that change `version` was pushed to `session` (before the push is written)."""
    data = _read(bdir)
    mine = {v: e for v, e in (data.get(session) if isinstance(data.get(session), dict) else {}).items()
            if v.isdigit() and isinstance(e, dict)}  # a damaged entry is dropped, never a crash
    mine[str(version)] = {"ts": time.time() if now is None else now, "confirmed": False}
    keep = sorted(mine, key=int)[-CHANNEL_DELIVERED_KEPT:]
    data[session] = {v: mine[v] for v in keep}
    atomic_write(bdir / CHANNEL_DELIVERED_FILE, data)


def unmark(bdir: Path, session: str, version: int) -> None:
    """Takes a mark back — the push could not be written."""
    data = _read(bdir)
    if isinstance(data.get(session), dict) and data[session].pop(str(version), None) is not None:
        atomic_write(bdir / CHANNEL_DELIVERED_FILE, data)


def confirm(bdir: Path, session: str, now: float | None = None) -> None:
    """The session recorded a turn event: every push made before now worked. Hooks call this."""
    data = _read(bdir)
    mine = data.get(session)
    if not isinstance(mine, dict):
        return
    stamp = time.time() if now is None else now
    changed = False
    for entry in mine.values():
        if isinstance(entry, dict) and not entry.get("confirmed") and entry.get("ts", stamp) <= stamp:
            entry["confirmed"], changed = True, True
    if changed:
        atomic_write(bdir / CHANNEL_DELIVERED_FILE, data)


def pushed(bdir: Path, session: str) -> set:
    """Every version ever pushed to this session (confirmed or not): a server never pushes one twice."""
    mine = _read(bdir).get(session)
    return {int(v) for v in mine if v.isdigit()} if isinstance(mine, dict) else set()


def held_back(bdir: Path, session: str, now: float | None = None) -> set:
    """Versions the hooks must NOT deliver: confirmed pushes, and pushes still inside the grace."""
    stamp = time.time() if now is None else now
    mine = _read(bdir).get(session)
    if not isinstance(mine, dict):
        return set()
    out = set()
    for v, e in mine.items():
        if isinstance(e, dict) and v.isdigit() and (e.get("confirmed") or stamp - e.get("ts", 0) < CHANNEL_CONFIRM_S):
            out.add(int(v))
    return out


def peek_changes(bdir: Path, session: str, now: float | None = None) -> list[dict]:
    """board_control.peek_changes minus the tasks a channel already delivered to this session."""
    gone = held_back(bdir, session, now)
    return [c for c in _peek(bdir, session)
            if not (c["v"] in gone and c["action"] in ControlAction.FOR_ONE_SESSION
                    and c.get("session") == session)]


def unseen_changes(bdir: Path, session: str) -> list[dict]:
    """Like board_control.unseen_changes, over the filtered list; marks what it returns as seen."""
    fresh = peek_changes(bdir, session)
    if fresh:
        acks = _acks(bdir)
        acks[session] = max(c["v"] for c in fresh)
        atomic_write(bdir / ACK_FILE, acks)
    return fresh
