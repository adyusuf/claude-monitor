"""Which sessions a board channel server can reach (docs/live-board.md §5).

A server registers one small file per session. "Reachable" is only claimed when the file is
fresh, its process is alive AND the Claude Code process that started the server was launched with
the channel flag naming this server — without the flag Claude Code silently drops every push, and
the desktop app never passes it. Anything that cannot be verified counts as NOT reachable (#6).
"""
from __future__ import annotations

import json
import os
import re
import subprocess
import time
from pathlib import Path

from board_config import (CHANNEL_DIR, CHANNEL_FLAGS, CHANNEL_SERVER, CHANNEL_TTL_S,
                          SESSION_ID_PATTERN)
from board_store import atomic_write


def _file(bdir: Path, session: str) -> Path:
    return bdir / CHANNEL_DIR / f"{session}.json"


def register(bdir: Path, session: str, pid: int, now: float | None = None) -> None:
    """(Re)writes this server's file; called at start and on every beat."""
    atomic_write(_file(bdir, session), {"session": session, "pid": pid,
                                        "beat": time.time() if now is None else now})


def unregister(bdir: Path, session: str) -> None:
    try:
        _file(bdir, session).unlink()
    except OSError:
        pass  # already gone


def _alive(pid) -> bool:
    if not isinstance(pid, int) or isinstance(pid, bool) or pid <= 0:
        return False
    try:
        os.kill(pid, 0)
    except PermissionError:
        return True   # exists, owned by someone else
    except OSError:
        return False
    return True


def reachable(bdir: Path, now: float | None = None) -> set:
    """Session ids with a fresh, live channel server."""
    stamp = time.time() if now is None else now
    found = set()
    for f in (bdir / CHANNEL_DIR).glob("*.json"):
        try:
            data = json.loads(f.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            continue
        sid, beat = data.get("session"), data.get("beat")
        if (isinstance(sid, str) and re.match(SESSION_ID_PATTERN, sid) and sid == f.stem
                and isinstance(beat, (int, float)) and stamp - beat <= CHANNEL_TTL_S
                and _alive(data.get("pid"))):
            found.add(sid)
    return found


def flagged(command: str) -> bool:
    """True when a Claude Code command line lists `server:<CHANNEL_SERVER>` after a channel flag."""
    args = command.split()
    active = False
    for arg in args:
        if arg.startswith("--"):
            active = arg.split("=", 1)[0] in CHANNEL_FLAGS
            arg = arg.split("=", 1)[1] if "=" in arg and active else ""
        if active and arg == f"server:{CHANNEL_SERVER}":
            return True
    return False


def launched_with_channel(parent_pid: int) -> bool:
    """Reads the parent's command line (`ps`); any failure -> False, never a guess."""
    try:
        out = subprocess.run(["ps", "-o", "command=", "-p", str(parent_pid)], capture_output=True,
                             text=True, timeout=5, check=True).stdout
    except (OSError, subprocess.SubprocessError):
        return False
    return flagged(out)
