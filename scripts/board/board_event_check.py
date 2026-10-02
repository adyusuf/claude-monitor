"""Which events the fold may apply (board_store.fold).

The log is shared and append-only: a hook of another version, a crash mid-write or a hand edit can leave an
event the fold would raise on, and one raise takes the whole page down (/api/state) until someone edits the
file. An event that is not well formed is skipped whole - before it changes anything, so the state keeps its
shapes - and reported once per kind of problem, not on every poll."""
from __future__ import annotations

import sys

from board_todos import TODO_EVENTS

STRINGS = ("type", "ts", "session", "id", "title", "branch", "role", "note", "status", "mode", "by", "agent",
           "task", "tool_use_id", "agent_id", "agent_type", "description", "reason", "transcript",
           "agent_transcript")
LISTS = ("roles", "commits", "options")
NUMBERS = ("eta_min", "est_cost")
NEEDS_ID = ("task_add", "task_set")
NEEDS_TOOL_USE = ("agent_pre", "agent_denied", "agent_post")
_reported: set[tuple[str, str]] = set()


def problem(ev) -> str | None:
    """Why the fold must not apply this event, or None when it may."""
    if not isinstance(ev, dict):
        return "not an object"
    for name in STRINGS:
        if ev.get(name) is not None and not isinstance(ev[name], str):
            return f"{name} is not text"
    for name in LISTS:
        if ev.get(name) is not None and not isinstance(ev[name], list):
            return f"{name} is not a list"
    for name in NUMBERS:
        if ev.get(name) is not None and (isinstance(ev[name], bool) or not isinstance(ev[name], (int, float))):
            return f"{name} is not a number"
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
    """Once per (kind, problem) for the life of the process: the server folds the log on every poll."""
    key = (str(ev.get("type")) if isinstance(ev, dict) else "?", why)
    if key not in _reported:
        _reported.add(key)
        print(f"board: skipping a malformed {key[0]} event ({why}); the rest of the log is used", file=sys.stderr)
