"""Local board server — ONE server for every project registered on this machine.

  python3 ~/.claude/scripts/board/board_server.py [--port N] [--dir <project>/.claude/board]
Serves board.html, the project list, each project's folded state, and takes the
user's controls. Binds to loopback only (BOARD_ALLOW_REMOTE=1 opts out). Standard library, no dependencies.
Started automatically by board_ensure.py (SessionStart hook, docs/live-board.md).
"""
from __future__ import annotations

import argparse
import json
import sys
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlsplit

from board_api import apply, decision_fields, list_skills, validate_control  # noqa: F401 (re-export)
from board_app import SVG_ICON_PATH, icon_png, icon_size, icon_svg, manifest
from board_channel_ack import peek_changes
from board_channel_reg import reachable
from board_config import (ALLOW_REMOTE, API_VERSION, CHANNEL_SERVER, DECISION_WAIT_S, HOST, MODES, PORT, QUEUE_TEXT_MAX, REGISTRY,
                          SKILLS_DIR, ControlAction, DecisionChoice, code_build)
from board_cost import Cache
from board_http_guard import SECURITY_HEADERS, SERVER_NAME, bind_allowed, host_header_ok
from board_merge import merged
from board_registry import load, project_id, summary
from board_sessions import board_costs
from board_store import fold, read_events
from board_control import read_control

JS = "text/javascript; charset=utf-8"
STATIC = {"/": ("board.html", "text/html; charset=utf-8"),
          "/board_ui.js": ("board_ui.js", JS), "/board_ui_text.js": ("board_ui_text.js", JS),
          "/board_ui_sessions.js": ("board_ui_sessions.js", JS), "/sw.js": ("sw.js", JS)}
MAX_BODY = 4096
BUILD = code_build()  # the code this process was started with (see board_ensure: a stale server is replaced)
TRANSCRIPTS = Cache()  # parsed incrementally, shared by every request thread


def boards(registry: Path, extra_dir: Path | None) -> dict:
    """Registered boards, plus the one named with --dir (kept for backward compatibility)."""
    found = load(registry)
    if extra_dir is not None:
        root = extra_dir.resolve().parent.parent
        entry = {"id": project_id(root), "name": root.name, "root": str(root),
                 "dir": str(extra_dir.resolve()), "registered": None}
        found.setdefault(entry["id"], entry)
    return found


def pick(found: dict, wanted: str | None) -> dict | None:
    """The requested board; without one, the most recently active (the pre-v2 behaviour)."""
    if wanted:
        return found.get(wanted)
    if not found:
        return None
    return max(found.values(), key=lambda e: summary(e)["last_event"] or "")


def queued(bdir: Path, session: str) -> list:
    """Tasks and skills queued for a session that its hooks have not delivered yet."""
    return [c.get("text") for c in peek_changes(bdir, session)
            if c["action"] in ControlAction.FOR_ONE_SESSION and c.get("session") == session]


def project_state(entry: dict, now: float | None = None) -> dict:
    bdir = Path(entry["dir"])
    control = read_control(bdir)
    state = fold(read_events(bdir), control)
    state["control"] = {k: control[k] for k in ("removed_tasks", "disabled_roles")}
    state["project"] = entry["id"]
    state["decision_defaults"] = list(DecisionChoice.DEFAULTS)
    state["modes"] = list(MODES)
    state["stop_wait_s"] = DECISION_WAIT_S
    state["queue_text_max"] = QUEUE_TEXT_MAX
    for task in state["tasks"].values():
        task["merged"] = merged(entry["root"], task.get("commits") or [])
    state["costs"] = board_costs(state, TRANSCRIPTS, time.time() if now is None else now)
    state["channel_server"] = CHANNEL_SERVER
    reach = reachable(bdir)
    for sess in state["costs"]["sessions"]:
        sess["queued"] = queued(bdir, sess["id"])
        sess["channel"] = sess["id"] in reach  # a live board-channel server, started with the channel flag
    return state


def make_handler(registry: Path | None = None, extra_dir: Path | None = None,
                 skills_dir: Path | None = None, allow_remote: bool = ALLOW_REMOTE):
    skills_dir = skills_dir or SKILLS_DIR
    class Handler(BaseHTTPRequestHandler):
        def version_string(self) -> str:
            return SERVER_NAME  # not "BaseHTTP/0.6 Python/x.y.z"

        def _send(self, code: int, body: bytes, ctype: str) -> None:
            self.send_response(code)
            self.send_header("Content-Type", ctype)
            self.send_header("Cache-Control", "no-store")
            for name, value in SECURITY_HEADERS:
                self.send_header(name, value)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def _json(self, code: int, data) -> None:
            self._send(code, json.dumps(data, ensure_ascii=False).encode("utf-8"),
                       "application/json; charset=utf-8")

        def _host_refused(self) -> bool:
            """DNS rebinding: a page whose own name points at 127.0.0.1 passes the Origin check, so the Host
            has to be a loopback name too. Answers 403 and returns True when it is not."""
            if host_header_ok(self.headers.get("Host"), self.server.server_address[1], allow_remote):
                return False
            self._json(403, {"error": "host not allowed"})
            return True

        def do_GET(self):
            if self._host_refused():
                return
            url = urlsplit(self.path)
            query = parse_qs(url.query)
            if url.path in STATIC:
                name, ctype = STATIC[url.path]
                return self._send(200, Path(__file__).with_name(name).read_bytes(), ctype)
            if url.path == "/api/info":
                return self._json(200, {"version": API_VERSION, "build": BUILD})
            if url.path == "/manifest.webmanifest":
                return self._send(200, json.dumps(manifest()).encode("utf-8"),
                                  "application/manifest+json")
            if url.path == SVG_ICON_PATH:
                return self._send(200, icon_svg(), "image/svg+xml")
            if icon_size(url.path):
                return self._send(200, icon_png(icon_size(url.path)), "image/png")
            if url.path == "/api/skills":
                return self._json(200, list_skills(skills_dir))
            found = boards(registry, extra_dir)
            if url.path == "/api/projects":
                items = sorted((summary(e) for e in found.values()),
                               key=lambda s: s["last_event"] or "", reverse=True)
                return self._json(200, items)
            if url.path == "/api/state":
                entry = pick(found, (query.get("p") or [None])[0])
                if entry is None:
                    return self._json(404, {"error": "unknown project"})
                return self._json(200, project_state(entry))
            return self._json(404, {"error": "not found"})

        def do_POST(self):
            if self._host_refused():
                return
            if urlsplit(self.path).path != "/api/control":
                return self._json(404, {"error": "not found"})
            # Same-origin only: a foreign page cannot send JSON here without a preflight we never answer.
            origin = self.headers.get("Origin")
            if origin and origin != f"http://{self.headers.get('Host')}":
                return self._json(403, {"error": "cross-origin request refused"})
            if not self.headers.get("Content-Type", "").startswith("application/json"):
                return self._json(415, {"error": "json required"})
            length = int(self.headers.get("Content-Length") or 0)
            if length <= 0 or length > MAX_BODY:
                return self._json(400, {"error": "bad body size"})
            try:
                body = json.loads(self.rfile.read(length))
                validate_control(body if isinstance(body, dict) else {})
            except (ValueError, json.JSONDecodeError) as exc:
                return self._json(400, {"error": str(exc)})
            entry = pick(boards(registry, extra_dir), body.get("project"))
            if entry is None:
                return self._json(404, {"error": "unknown project"})
            try:
                ctl = apply(entry, body, skills_dir)
            except ValueError as exc:
                return self._json(400, {"error": str(exc)})
            self._json(200, {"version": ctl["version"]})

        def log_message(self, fmt, *args):  # keep request noise out; errors still go to stderr
            if args and str(args[1]).startswith(("4", "5")):
                super().log_message(fmt, *args)

    return Handler


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--port", type=int, default=PORT)
    p.add_argument("--dir", help="also show this board directory (optional)")
    args = p.parse_args(argv)
    if not bind_allowed(HOST, ALLOW_REMOTE):
        print(f"board: refusing to listen on '{HOST}': the board has no authentication, so it binds to loopback only. "
              "Set BOARD_HOST=127.0.0.1, or BOARD_ALLOW_REMOTE=1 to expose it on purpose.", file=sys.stderr)
        return 2
    extra = Path(args.dir) if args.dir else None
    server = ThreadingHTTPServer((HOST, args.port), make_handler(REGISTRY, extra))
    print(f"board: http://{HOST}:{args.port}  (registry: {REGISTRY})", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
