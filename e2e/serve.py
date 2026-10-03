#!/usr/bin/env python3
"""A real board server on a seeded temporary project, for the browser tests (e2e/specs).

Everything lives in a temporary folder that is removed on exit (also on SIGTERM, which is how Playwright
stops it): its own registry, its own transcripts folder, its own skills folder. The user's real boards,
registry and transcripts are never read or written. The port arrives as E2E_PORT from playwright.config.js.

Seed (the specs rely on these ids):
  S_BUSY  a session in a turn      S_IDLE  a session between turns
  T-1 running (S_BUSY) with a live background agent        T-2 waiting (S_IDLE): gets the "Ask status" button
  T-3 done                                                  T-4 running, its title is an XSS probe
  T-5 .. T-45 planned (41 tasks): with the rest they make two pages of 30
"""
import atexit
import os
import shutil
import signal
import sys
import tempfile
from pathlib import Path

BOARD = Path(__file__).resolve().parent.parent / "scripts" / "board"
TMP = Path(tempfile.mkdtemp(prefix="board-e2e-"))
atexit.register(shutil.rmtree, TMP, True)
signal.signal(signal.SIGTERM, lambda *_: sys.exit(0))   # sys.exit runs atexit; the folder goes with it

os.environ["BOARD_REGISTRY"] = str(TMP / "registry.json")
os.environ["BOARD_TRANSCRIPTS_ROOT"] = str(TMP / "transcripts")
os.environ["BOARD_SKILLS_DIR"] = str(TMP / "skills")
os.environ["BOARD_HOST"] = "127.0.0.1"
sys.path.insert(0, str(BOARD))

import board_server  # noqa: E402  (after the environment is set: board_config reads it at import)
from board_registry import register  # noqa: E402
from board_store import append_event  # noqa: E402

S_BUSY, S_IDLE = "e2e0aaaa-1111", "e2e0bbbb-2222"
XSS = '<img src=x onerror="window.__xss=1">'


def seed() -> None:
    root = TMP / "alpha"
    bdir = root / ".claude" / "board"
    for folder in (bdir, TMP / "transcripts", TMP / "skills"):
        folder.mkdir(parents=True, exist_ok=True)
    register(root, bdir)
    add = lambda event: append_event(bdir, event)
    add({"type": "turn_start", "session": S_BUSY})
    add({"type": "turn_start", "session": S_IDLE})
    add({"type": "turn_stop", "session": S_IDLE})
    add({"type": "plan", "mode": "B", "roles": ["developer"]})
    titles = {1: "Build the thing", 2: "Wait for review", 3: "Ship it", 4: XSS}
    for n in range(1, 46):
        add({"type": "task_add", "id": f"T-{n}", "title": titles.get(n, f"Planned task {n}")})
    add({"type": "task_set", "id": "T-1", "status": "running", "note": "in progress"})
    add({"type": "task_session", "session": S_BUSY, "id": "T-1"})
    add({"type": "task_set", "id": "T-2", "status": "waiting", "note": "waiting for review"})
    add({"type": "task_session", "session": S_IDLE, "id": "T-2"})
    add({"type": "task_set", "id": "T-3", "status": "done", "note": "released"})
    add({"type": "task_set", "id": "T-4", "status": "running", "note": "probe"})
    add({"type": "agent_pre", "session": S_BUSY, "tool_use_id": "u-e2e-1", "agent_type": "developer",
         "task": "T-1", "description": "T-1 build", "background": True})
    add({"type": "agent_post", "tool_use_id": "u-e2e-1", "agent_id": "a-e2e-1", "launched": True})


if __name__ == "__main__":
    seed()
    sys.exit(board_server.main(["--port", os.environ["E2E_PORT"]]))
