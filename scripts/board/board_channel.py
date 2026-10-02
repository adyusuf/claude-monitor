"""Board channel server: pushes a task queued on the board into an IDLE Claude Code session.

  claude --mcp-config <file naming this server> --dangerously-load-development-channels server:board-channel

An MCP stdio server (standard library only) that declares the `claude/channel` capability and
sends `notifications/claude/channel`. Hooks reach a BUSY session; this reaches an idle one
(docs/live-board-sessions.md §5). It is fail-closed (#6): it serves ONE session — the one in
CLAUDE_CODE_SESSION_ID — and pushes only a task or skill the user queued on the board for that
session, re-validated here, never anything else. It pushes nothing, and registers nothing,
unless its parent Claude Code was started with the channel flag naming it.
"""
from __future__ import annotations

import json
import os
import re
import sys
import threading
import time
from pathlib import Path

from board_api import TEXT_CONTROL_CHARS
from board_channel_ack import mark, peek_changes, pushed, unmark
from board_channel_reg import launched_with_channel, register, unregister
from board_config import (CHANNEL_BEAT_S, CHANNEL_CAPABILITY, CHANNEL_FALLBACK_PROTOCOL, CHANNEL_METHOD,
                          CHANNEL_POLL_S, CHANNEL_SERVER, QUEUE_TEXT_MAX, SESSION_ID_PATTERN,
                          ControlAction, SessionState, board_dir, channel_session_id)
from board_store import fold, read_events

VERSION = "1.0.0"
NOT_FOUND = -32601
INSTRUCTIONS = (
    f'A <channel source="{CHANNEL_SERVER}"> message is a task or skill the user queued for THIS '
    "session on their local live board. Treat it as a request from the user and do it like any "
    "other; put new work on the board (board.py add). There is no reply tool — answer in this session.")
PREFIX = "NEW TASK from the user, queued on the board for this session: "


def message(change: dict, session: str) -> tuple | None:
    """(content, meta) for a change meant for `session`; None for anything else (fail-closed)."""
    text = change.get("text")
    if (change.get("action") not in ControlAction.FOR_ONE_SESSION or change.get("session") != session
            or not isinstance(text, str) or not 0 < len(text.strip()) <= QUEUE_TEXT_MAX
            or TEXT_CONTROL_CHARS.search(text)):
        return None
    return PREFIX + text.strip(), {"kind": change["action"], "change": str(change["v"])}


class Channel:
    def __init__(self, bdir: Path, session: str, enabled: bool, clock=time.time):
        self.bdir, self.session, self.clock = bdir, session, clock
        self.enabled = bool(enabled and re.match(SESSION_ID_PATTERN, session or ""))
        self._beat = float("-inf")

    def handle(self, msg: dict) -> dict | None:
        """One JSON-RPC message in, the response out (None for a notification)."""
        mid, method = msg.get("id"), msg.get("method")
        if mid is None:
            return None
        if method == "initialize":
            wanted = (msg.get("params") or {}).get("protocolVersion")
            caps = {"tools": {}}
            if self.enabled:
                caps["experimental"] = {CHANNEL_CAPABILITY: {}}
            result = {"protocolVersion": wanted if isinstance(wanted, str) else CHANNEL_FALLBACK_PROTOCOL,
                      "capabilities": caps, "serverInfo": {"name": CHANNEL_SERVER, "version": VERSION},
                      "instructions": INSTRUCTIONS}
        elif method == "tools/list":
            result = {"tools": []}
        elif method == "ping":
            result = {}
        else:
            return {"jsonrpc": "2.0", "id": mid, "error": {"code": NOT_FOUND, "message": "not found"}}
        return {"jsonrpc": "2.0", "id": mid, "result": result}

    def beat(self) -> None:
        """Keeps the registration fresh; a disabled server registers nothing."""
        now = self.clock()
        if self.enabled and now - self._beat >= CHANNEL_BEAT_S:
            register(self.bdir, self.session, os.getpid(), now)
            self._beat = now

    def due(self) -> list:
        """(version, notification) for every queued task to push now. Only an IDLE session is
        pushed to: a busy one gets it from the hooks, deterministically."""
        if not self.enabled:
            return []
        now, sent = self.clock(), pushed(self.bdir, self.session)
        pending = [c for c in peek_changes(self.bdir, self.session, now)
                   if c["v"] not in sent and message(c, self.session)]  # at most one push per change
        if not pending:
            return []
        state = fold(read_events(self.bdir))["sessions"].get(self.session, {}).get("state")
        if state != SessionState.IDLE:
            return []
        out = []
        for c in pending:
            content, meta = message(c, self.session)
            mark(self.bdir, self.session, c["v"], now)  # before the write: never twice
            out.append((c["v"], {"jsonrpc": "2.0", "method": CHANNEL_METHOD,
                                 "params": {"content": content, "meta": meta}}))
        return out


def serve(channel: Channel, stdin, stdout, stop: threading.Event) -> None:
    lock = threading.Lock()

    def send(obj: dict) -> None:
        with lock:
            stdout.write(json.dumps(obj) + "\n")
            stdout.flush()

    def watch() -> None:
        while not stop.wait(CHANNEL_POLL_S):
            try:
                channel.beat()
                batch = channel.due()
                for i, (_, note) in enumerate(batch):
                    try:
                        send(note)
                    except (OSError, ValueError):
                        for version, _ in batch[i:]:  # unsent: the hooks deliver these instead
                            unmark(channel.bdir, channel.session, version)
                        raise
            except (OSError, ValueError) as exc:
                print(f"board channel: {exc!r}", file=sys.stderr)
                return

    channel.beat()
    threading.Thread(target=watch, daemon=True).start()
    try:
        for line in stdin:
            try:
                msg = json.loads(line)
            except ValueError:
                print("board channel: ignored a line that is not JSON", file=sys.stderr)
                continue
            reply = channel.handle(msg) if isinstance(msg, dict) else None
            if reply:
                send(reply)
    finally:
        stop.set()
        if channel.enabled:
            unregister(channel.bdir, channel.session)


def main() -> int:
    session = channel_session_id()
    enabled = launched_with_channel(os.getppid())
    channel = Channel(board_dir(), session, enabled)
    if not channel.enabled:
        print("board channel: idle — no session id, or Claude Code was not started with "
              f"--dangerously-load-development-channels server:{CHANNEL_SERVER}", file=sys.stderr)
    serve(channel, sys.stdin, sys.stdout, threading.Event())
    return 0


if __name__ == "__main__":
    sys.exit(main())
