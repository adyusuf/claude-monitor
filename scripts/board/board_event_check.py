"""Which events the fold may apply (board_store.fold).

The log is shared and append-only: a hook of another version, a crash mid-write or a hand edit can leave an
event the fold would raise on, and one raise takes the whole page down (/api/state) until someone edits the
file. An event that is not well formed is skipped whole - before it changes anything, so the state keeps its
shapes - and reported once per kind of problem, not on every poll."""
from __future__ import annotations

import math
import sys

from board_todos import TODO_EVENTS

STRINGS = frozenset(("type", "ts", "session", "id", "title", "branch", "role", "note", "status", "mode", "by",
                     "agent", "task", "tool_use_id", "agent_id", "agent_type", "description", "reason",
                     "transcript", "agent_transcript"))
LISTS = frozenset(("roles", "commits", "options"))
NUMBERS = frozenset(("eta_min", "est_cost"))
NEEDS_ID = ("task_add", "task_set")
NEEDS_TOOL_USE = ("agent_pre", "agent_denied", "agent_post")
REPORT_LIMIT, SHOWN = 50, 40
_reported: set[tuple[str, str]] = set()


def problem(ev) -> str | None:
    """Why the fold must not apply this event, or None when it may. Walks the event's own fields (a handful),
    not the whole field list: the server folds the entire log on every poll."""
    if not isinstance(ev, dict):
        return "not an object"
    for name, value in ev.items():
        if value is None:
            continue
        if name in STRINGS:
            if not isinstance(value, str):
                return f"{name} is not text"
        elif name in LISTS:
            if not isinstance(value, list):
                return f"{name} is not a list"
        elif name in NUMBERS:
            # NaN and Infinity are "numbers" to Python's json but not to a browser: json.dumps would hand the
            # page text it cannot parse, and the whole board would go blank.
            if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
                return f"{name} is not a finite number"
    kind = ev.get("type")
    if kind in NEEDS_ID and not ev.get("id"):
        return "no id"
    if kind in NEEDS_TOOL_USE and not ev.get("tool_use_id"):
        return "no tool_use_id"
    if kind in TODO_EVENTS:
        todos, item, change = ev.get("todos"), ev.get("item"), ev.get("change")
        if todos is not None and not (isinstance(todos, list) and all(isinstance(t, dict) for t in todos)):
            return "todos is not a list of objects"
        if item is not None and not isinstance(item, dict):
            return "item is not an object"
        if change is not None and not (isinstance(change, dict) and isinstance(change.get("id"), str)):
            return "change has no id"
    return None


def report(ev, why: str) -> None:
    """Once per (kind, problem) for the life of the process: the server folds the log on every poll. Bounded: a
    log full of distinct junk prints REPORT_LIMIT lines and one note, and a long `type` is cut to SHOWN characters."""
    kind = ev.get("type") if isinstance(ev, dict) else None
    key = (repr(kind)[:SHOWN] if kind is not None else "?", why)
    if key in _reported:
        return
    if len(_reported) >= REPORT_LIMIT:
        if len(_reported) == REPORT_LIMIT:
            _reported.add(("", "limit"))
            print(f"board: more than {REPORT_LIMIT} kinds of malformed event; the rest are skipped silently", file=sys.stderr)
        return
    _reported.add(key)
    print(f"board: skipping a malformed {key[0]} event ({why}); the rest of the log is used", file=sys.stderr)
