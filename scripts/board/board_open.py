"""Open the live board as its own app window (docs/live-board.md §2e).

  python3 ~/.claude/scripts/board/board_open.py
Makes sure the server is up (the SessionStart auto-start's own logic), then opens the page in
a Chrome app window (`--app=<url>`: no tabs, no address bar); without Chrome, in the default
browser. Installing it from the browser's "Install" menu (the board is a web app) works too.
"""
from __future__ import annotations

import os
import shutil
import subprocess
import sys
import webbrowser

from board_config import CHROME_BINARIES, MAC_CHROME_APP, OPEN_TIMEOUT_S
from board_ensure import Found, ensure, probe, url

MAC = "darwin"


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


if __name__ == "__main__":
    sys.exit(open_board(os.getcwd()))
