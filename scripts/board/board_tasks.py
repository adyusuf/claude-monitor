"""Adding a task: the next free id under a lock, and an id that exists is refused.

Ids used to be typed by hand, so two sessions could both write `add T-25` and the later title
silently replaced the earlier task. `add auto` reads the log and appends while holding an
exclusive lock, so parallel sessions cannot draw the same number."""
from __future__ import annotations

import fcntl
import os
import re
from pathlib import Path

from board_config import AUTO_TASK_ID, TASK_ID_PATTERN, TASK_LOCK_FILE
from board_store import append_event, read_events


class TaskExists(Exception):
    def __init__(self, task_id: str):
        super().__init__(task_id)
        self.task_id = task_id


def _number(task_id: str) -> int:
    return int(task_id.split("-", 1)[1])


def add_task(bdir: Path, event: dict) -> str:
    """Append a task_add and return its id. `event["id"]` is a T-n id or AUTO_TASK_ID."""
    bdir.mkdir(parents=True, exist_ok=True)
    fd = os.open(bdir / TASK_LOCK_FILE, os.O_WRONLY | os.O_CREAT, 0o600)
    try:
        fcntl.flock(fd, fcntl.LOCK_EX)
        events = read_events(bdir)
        added = {e["id"] for e in events if e.get("type") == "task_add" and e.get("id")}
        seen = [_number(e["id"]) for e in events
                if isinstance(e.get("id"), str) and re.match(TASK_ID_PATTERN, e["id"])]
        task_id = event["id"]
        if task_id == AUTO_TASK_ID:
            task_id = f"T-{max(seen, default=0) + 1}"
        elif task_id in added:
            raise TaskExists(task_id)
        append_event(bdir, {**event, "id": task_id})
        return task_id
    finally:
        os.close(fd)  # closing releases the flock
