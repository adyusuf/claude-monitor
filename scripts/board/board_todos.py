"""The main session's todo list on the board (T-2): what the CLI's todo tools send, and how the
fold applies it. Shapes were read from the CLI binary (not guessed): TodoWrite sends the whole
list; TaskCreate returns {task: {id, subject}} ("Task #<id> created successfully"); TaskUpdate
takes {taskId, status: pending|in_progress|completed|deleted, subject?, activeForm?}. A payload
that is not that shape yields None — the board then has no todo line, never an invented one."""
from __future__ import annotations

import re

from board_config import TODO_MAX_ITEMS, TODO_TEXT_MAX, TodoStatus

CREATED_PATTERN = r"^Task #(\S+) created successfully"
TODO_EVENTS = ("todo_sync", "todo_add", "todo_update")


def _text(value) -> str:
    return str(value or "")[:TODO_TEXT_MAX]


def _item(content, status: str, active, task_id: str | None = None) -> dict:
    running = status == TodoStatus.IN_PROGRESS
    return {"id": task_id, "content": _text(content), "status": status,
            "active": _text((active if running else None) or content)}


def snapshot(tool_input: dict) -> list[dict] | None:
    """TodoWrite: the whole list, cut down to what the board shows."""
    todos = tool_input.get("todos")
    if not isinstance(todos, list):
        return None
    return [_item(t.get("content"), t["status"], t.get("activeForm"))
            for t in todos[:TODO_MAX_ITEMS] if isinstance(t, dict) and t.get("status") in TodoStatus.ALL]


def created_id(tool_response) -> str | None:
    """TaskCreate's task id from its result: {task: {id}} or the 'Task #<id> created' text."""
    if isinstance(tool_response, dict) and isinstance(tool_response.get("task"), dict):
        task_id = tool_response["task"].get("id")
        return str(task_id) if task_id not in (None, "") else None
    m = re.match(CREATED_PATTERN, tool_response) if isinstance(tool_response, str) else None
    return m.group(1) if m else None


def created(tool_input: dict, tool_response) -> dict | None:
    task_id = created_id(tool_response)
    if task_id is None or not tool_input.get("subject"):
        return None
    return _item(tool_input["subject"], TodoStatus.PENDING, tool_input.get("activeForm"), task_id)


def updated(tool_input: dict) -> dict | None:
    """TaskUpdate: only the fields it carries; a status the CLI does not document is dropped."""
    task_id = tool_input.get("taskId")
    if task_id in (None, ""):
        return None
    change = {"id": str(task_id)}
    if tool_input.get("status") in TodoStatus.ALL + (TodoStatus.DELETED,):
        change["status"] = tool_input["status"]
    if tool_input.get("subject"):
        change["content"] = _text(tool_input["subject"])
    if tool_input.get("activeForm"):
        change["active"] = _text(tool_input["activeForm"])
    return change if len(change) > 1 else None


def apply(sess: dict, kind: str, ev: dict) -> None:
    """Fold one todo event into the session's list (called by board_store.fold)."""
    todos = sess.setdefault("todos", [])
    if kind == "todo_sync":
        todos[:] = ev.get("todos") or []
    elif kind == "todo_add" and ev.get("item"):
        todos[:] = [t for t in todos if t.get("id") != ev["item"].get("id")][:TODO_MAX_ITEMS - 1] + [ev["item"]]
    elif kind == "todo_update" and ev.get("change"):
        ch = ev["change"]
        target = next((t for t in todos if t.get("id") == ch["id"]), None)
        if target is None:
            return  # a task this board never saw created: nothing to update, nothing invented
        if ch.get("status") == TodoStatus.DELETED:
            todos.remove(target)
            return
        target.update({k: v for k, v in ch.items() if k != "id"})
        if "content" in ch and "active" not in ch and target.get("status") != TodoStatus.IN_PROGRESS:
            target["active"] = target["content"]
