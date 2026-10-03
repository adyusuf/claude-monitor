"""A reminder to put the work on the board (T-23, 03/10/2026).

Seen live: a long session ran 60+ tool calls in a project with the board enabled and the page showed no
task at all. The hooks record agents by themselves, but the plan (the task list) only exists when the
session runs `board.py add|set` (the board-plan skill), and nothing asked for it. This is the ask:
once per session, after NUDGE_AFTER main-session tool calls with no `board.py add|set` from that session,
the PostToolUse hook adds one line of context. It never blocks, never repeats, and a session that has
already touched the board is not nudged.
"""
from __future__ import annotations

import json
from pathlib import Path

from board_config import NUDGE_AFTER, NUDGE_SESSIONS_KEPT
from board_store import atomic_write, read_events

STATE_FILE = "nudge.json"

TEXT = ("The live board has no plan from this session after {calls} tool calls. If this work splits into "
        "2+ pieces, put it on the board now: run /adyusuf:board-plan (board.py add auto \"<title>\"), "
        "then keep each task's status current. Ignore this for a one-step question; it is said once.")


def _load(path: Path) -> dict:
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}
    return data if isinstance(data, dict) else {}


def _touched_the_board(bdir, session: str) -> bool:
    return any(e.get("type") == "task_session" and e.get("session") == session for e in read_events(bdir))


def tick(bdir, session: str) -> str | None:
    """Count one main-session tool call; return the reminder text the one time it is due."""
    if not NUDGE_AFTER or not session:
        return None
    path = Path(bdir) / STATE_FILE
    state = _load(path)
    entry = state.get(session)
    if not isinstance(entry, dict):
        entry = {"calls": 0, "done": False}
    if entry.get("done"):
        return None
    entry["calls"] = int(entry.get("calls", 0)) + 1
    text = None
    if entry["calls"] >= NUDGE_AFTER:
        entry["done"] = True
        if not _touched_the_board(bdir, session):
            text = TEXT.format(calls=entry["calls"])
    state[session] = entry
    for old in list(state)[:-NUDGE_SESSIONS_KEPT]:
        del state[old]
    try:
        atomic_write(path, state)
    except OSError:
        return None  # a reminder is never worth breaking a tool call
    return text
