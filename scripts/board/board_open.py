"""Open the live board as its own app window (docs/live-board.md §2e).

  python3 ~/.claude/scripts/board/board_open.py
Makes sure the server is up (the SessionStart auto-start's own logic), then opens the page in
a Chrome app window (`--app=<url>`: no tabs, no address bar); without Chrome, in the default
browser. Installing it from the browser's "Install" menu (the board is a web app) works too.

  python3 ~/.claude/scripts/board/board_open.py --url
Only makes sure the server is up and prints its address. The desktop window (desktop/) asks
for it this way: it owns no URL or port.
"""
from __future__ import annotations

import os
import shutil
import subprocess
import sys
import webbrowser

from board_config import CHROME_BINARIES, MAC_CHROME_APP, OPEN_TIMEOUT_S
from board_ensure import Found, ensure, ensure_server, probe, url

MAC = "darwin"
URL_FLAG = "--url"


def app_command(target: str, platform: str = sys.platform, which=shutil.which) -> list | None:
    """The command that opens `target` as a Chrome app window, or None without Chrome."""
    if platform == MAC:
        return ["open", "-na", MAC_CHROME_APP, "--args", f"--app={target}"]
    for exe in CHROME_BINARIES:
        path = which(exe)
        if path:
            return [path, f"--app={target}"]
    return None


def open_board(cwd: str, run=subprocess.run, browser=webbrowser.open, ensure_fn=ensure,
               probe_fn=probe) -> int:
    print(ensure_fn(cwd)[0])
    if probe_fn() != Found.BOARD:
        print("board_open: the board server is not answering; nothing to open", file=sys.stderr)
        return 1
    target = url()
    cmd = app_command(target)
    if cmd:
        try:
            if run(cmd, capture_output=True, timeout=OPEN_TIMEOUT_S).returncode == 0:
                return 0
        except (OSError, subprocess.SubprocessError) as exc:
            print(f"board_open: app window failed ({exc!r}); using the default browser",
                  file=sys.stderr)
    return 0 if browser(target) else 1


def print_url(ensure_fn=ensure_server) -> int:
    """stdout carries the address and nothing else, so a caller can read it as one line."""
    found = ensure_fn()[0]
    if found != Found.BOARD:
        print(f"board_open: no current board server answers on {url()} ({found})", file=sys.stderr)
        return 1
    print(url())
    return 0


if __name__ == "__main__":
    sys.exit(print_url() if URL_FLAG in sys.argv[1:] else open_board(os.getcwd()))
