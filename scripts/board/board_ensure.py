"""SessionStart hook: register this project and make sure the live board server is up
(docs/live-board.md §2). Prints one line, which Claude Code adds to the
session context. Never blocks a session: any error goes to stderr and it exits 0.
"""
from __future__ import annotations

import json
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

from board_config import API_VERSION, HOST, PORT, REGISTRY, board_dir, boardable
from board_registry import register

SERVER = Path(__file__).with_name("board_server.py")
LOG_FILE = "server.log"
START_WAIT_S = 3.0


class Found:
    NOTHING = "nothing"      # the port does not answer
    BOARD = "board"          # a current board server
    OLD_BOARD = "old_board"  # a board server from before one-server-for-all (no /api/info)
    OTHER = "other"          # something else holds the port
    ALL = (NOTHING, BOARD, OLD_BOARD, OTHER)


def url() -> str:
    return f"http://{HOST}:{PORT}"


def _get(path: str, timeout: float):
    with urllib.request.urlopen(url() + path, timeout=timeout) as res:
        return json.loads(res.read())


def probe(timeout: float = 1.0) -> str:
    try:
        info = _get("/api/info", timeout)
        return Found.BOARD if info.get("version", 0) >= API_VERSION else Found.OLD_BOARD
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


def ensure(cwd: str | None, wait: float = START_WAIT_S):
    """Returns (the line for the session, the server process if this call started one)."""
    bdir = board_dir(cwd)
    if not boardable(bdir.parent.parent):
        return "", None  # ~ and / are not projects: no listing, and no server started on their account
    entry = register(bdir.parent.parent, bdir)
    found = probe()
    if found == Found.BOARD:
        return f"Live board: {url()} (running; project '{entry['name']}' is on it)", None
    if found == Found.OLD_BOARD:
        return (f"Live board: an OLDER board server holds {url()} and shows one project only — "
                f"stop it and start a new session to get every project on one page"), None
    if found == Found.OTHER:
        return f"Live board NOT started: something else holds {url()} (set BOARD_PORT)", None
    proc = start(REGISTRY.parent)
    deadline = time.monotonic() + wait
    while time.monotonic() < deadline:
        if probe(timeout=0.3) == Found.BOARD:
            return f"Live board: {url()} (started; project '{entry['name']}' is on it)", proc
        time.sleep(0.1)
    return f"Live board did NOT come up on {url()} — see {REGISTRY.parent / LOG_FILE}", proc


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
