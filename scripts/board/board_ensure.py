"""SessionStart hook: register this project and make sure the live board server is up
(docs/live-board.md §2). Prints one line, which Claude Code adds to the
session context. Never blocks a session: any error goes to stderr and it exits 0.
"""
from __future__ import annotations

import fcntl
import json
import os
import signal
import subprocess
import sys
import time
import urllib.error
import urllib.request
from contextlib import contextmanager
from pathlib import Path

from board_config import API_VERSION, HOST, PORT, REGISTRY, board_dir, boardable, code_build
from board_registry import register

SERVER = Path(__file__).with_name("board_server.py")
LOG_FILE = "server.log"
RESTART_LOCK = "restart.lock"
START_WAIT_S = 3.0
STOP_WAIT_S = 3.0
SERVER_NAME = "board_server.py"  # a process is only ever stopped if its command line names this


class Found:
    NOTHING = "nothing"      # the port does not answer
    BOARD = "board"          # a current board server
    OLD_BOARD = "old_board"  # a board server from before one-server-for-all (no /api/info)
    STALE = "stale"          # a current board server running older code than is on disk now (T-32)
    OTHER = "other"          # something else holds the port
    ALL = (NOTHING, BOARD, OLD_BOARD, STALE, OTHER)


def url() -> str:
    return f"http://{HOST}:{PORT}"


def _get(path: str, timeout: float):
    with urllib.request.urlopen(url() + path, timeout=timeout) as res:
        return json.loads(res.read())


def probe(timeout: float = 1.0) -> str:
    try:
        info = _get("/api/info", timeout)
        if info.get("version", 0) < API_VERSION:
            return Found.OLD_BOARD
        return Found.BOARD if info.get("build") == code_build() else Found.STALE
    except urllib.error.HTTPError:
        pass  # it answered, but not as a current board — look closer below
    except (OSError, ValueError, AttributeError):
        return Found.NOTHING
    try:
        _get("/api/state", timeout)
        return Found.OLD_BOARD
    except (OSError, ValueError):
        return Found.OTHER


def start(log_dir: Path) -> subprocess.Popen:
    log_dir.mkdir(parents=True, exist_ok=True)
    with open(log_dir / LOG_FILE, "ab") as log:
        return subprocess.Popen([sys.executable, str(SERVER), "--port", str(PORT)],
                                stdout=log, stderr=log, stdin=subprocess.DEVNULL,
                                start_new_session=True)


def _listeners() -> list[int]:
    """The processes listening on the board's port; empty when `lsof` is missing or finds none."""
    try:
        out = subprocess.run(["lsof", "-nP", f"-iTCP:{PORT}", "-sTCP:LISTEN", "-t"],
                             capture_output=True, text=True, timeout=5)
    except (OSError, subprocess.TimeoutExpired):
        return []
    return [int(word) for word in out.stdout.split() if word.isdigit()]


def _is_board_server(pid: int) -> bool:
    try:
        out = subprocess.run(["ps", "-p", str(pid), "-o", "command="], capture_output=True, text=True, timeout=5)
    except (OSError, subprocess.TimeoutExpired):
        return False
    return SERVER_NAME in out.stdout


def stop_server() -> bool:
    """Stop the server holding the board's port. Fail-closed: only when EVERY listener is a board
    server (its command line names board_server.py) — a foreign service on the port is never signalled.
    True when the port is free afterwards."""
    pids = _listeners()
    if not pids or not all(_is_board_server(pid) for pid in pids):
        return False
    for pid in pids:
        try:
            os.kill(pid, signal.SIGTERM)  # its state is on disk (events.jsonl, control.json): nothing is lost
        except ProcessLookupError:
            pass  # already gone
    deadline = time.monotonic() + STOP_WAIT_S
    while time.monotonic() < deadline:
        if probe(timeout=0.3) == Found.NOTHING:
            return True
        time.sleep(0.1)
    return False


@contextmanager
def _restart_lock(log_dir: Path):
    """Two sessions starting together must not stop each other's fresh server."""
    log_dir.mkdir(parents=True, exist_ok=True)
    with open(log_dir / RESTART_LOCK, "a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        try:
            yield
        finally:
            fcntl.flock(lock, fcntl.LOCK_UN)


def ensure(cwd: str | None, wait: float = START_WAIT_S):
    """Returns (the line for the session, the server process if this call started one)."""
    bdir = board_dir(cwd)
    if not boardable(bdir.parent.parent):
        return "", None  # ~ and / are not projects: no listing, and no server started on their account
    entry = register(bdir.parent.parent, bdir)
    found = probe()
    replaced = False
    if found in (Found.STALE, Found.OLD_BOARD):
        # T-32: a server started before the code changed kept running and serving the old behaviour
        # (and an empty page once its files moved). Replace it, once, under a lock.
        with _restart_lock(REGISTRY.parent):
            found = probe()  # another session may have done it while this one waited
            if found in (Found.STALE, Found.OLD_BOARD) and stop_server():
                found, replaced = Found.NOTHING, True
    if found == Found.BOARD:
        return f"Claude Monitor: {url()} (running; project '{entry['name']}' is on it)", None
    if found == Found.OLD_BOARD:
        return (f"Claude Monitor: an OLDER board server holds {url()} and shows one project only — "
                f"stop it and start a new session to get every project on one page"), None
    if found == Found.STALE:
        return (f"Claude Monitor: a server running OLDER code holds {url()} and could not be stopped from here — "
                f"stop it and start a new session"), None
    if found == Found.OTHER:
        return f"Claude Monitor NOT started: something else holds {url()} (set BOARD_PORT)", None
    proc = start(REGISTRY.parent)
    deadline = time.monotonic() + wait
    while time.monotonic() < deadline:
        if probe(timeout=0.3) == Found.BOARD:
            how = "restarted: it was running older code" if replaced else "started"
            return f"Claude Monitor: {url()} ({how}; project '{entry['name']}' is on it)", proc
        time.sleep(0.1)
    return f"Claude Monitor did NOT come up on {url()} — see {REGISTRY.parent / LOG_FILE}", proc


def main() -> int:
    try:
        raw = sys.stdin.read()
        payload = json.loads(raw) if raw.strip() else {}
        line = ensure(payload.get("cwd"))[0]
        if line:
            print(line)
    except Exception as exc:  # a board problem must never stop a session from starting
        print(f"board ensure error: {exc!r}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
