"""`board.py enable`: turn the live board on in the repository you are in (docs/live-board.md §2).

Merges the hook block into <root>/.claude/settings.json, adds .claude/board/ to .gitignore and lists the
project — the three manual steps that were skipped in ryan (no settings.json, so no SessionStart, so
no registration and no agent/cost activity). Idempotent: a hook that is already there is left alone, and
everything else in the settings file (other hooks, permissions, ...) is kept.
"""
from __future__ import annotations

import json
import subprocess
from pathlib import Path

SETTINGS = Path(".claude") / "settings.json"
GITIGNORE_LINE = ".claude/board/"
_HOOK = 'python3 "$HOME/.claude/scripts/board/{script}" || true'
STOP_TIMEOUT_S = 900  # the Stop hook waits for a board decision (BOARD_DECISION_WAIT, at most 840 s)

# event -> (matcher | None, script, timeout | None) — one place; the doc block in docs/live-board.md is checked against it
HOOKS = {
    "SessionStart": (None, "board_ensure.py", None),
    "PreToolUse": ("Agent", "board_hook.py", None),
    "PostToolUse": ("*", "board_hook.py", None),
    "SubagentStart": (None, "board_hook.py", None),
    "SubagentStop": (None, "board_hook.py", None),
    "Stop": (None, "board_hook.py", STOP_TIMEOUT_S),
    "UserPromptSubmit": (None, "board_hook.py", None),
}


def checkout_root(fallback: Path) -> Path:
    """The checkout you are IN: settings.json and .gitignore are committed files of this worktree, so in a
    linked worktree they belong to it — not to the main checkout, where the board data lives."""
    try:
        out = subprocess.run(["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True, check=True)
        return Path(out.stdout.strip())
    except (OSError, subprocess.CalledProcessError):
        return fallback


def hook_block() -> dict:
    out = {}
    for event, (matcher, script, timeout) in HOOKS.items():
        cmd = {"type": "command", "command": _HOOK.format(script=script)}
        if timeout:
            cmd["timeout"] = timeout
        entry = {"hooks": [cmd]}
        if matcher:
            entry = {"matcher": matcher, **entry}
        out[event] = [entry]
    return {"hooks": out}


def _has_board_hook(entries: list, script: str) -> bool:
    return any(script in h.get("command", "") for e in entries if isinstance(e, dict)
               for h in e.get("hooks", []) if isinstance(h, dict))


def merge(settings: dict) -> tuple[dict, list[str]]:
    """The settings with the board hooks added, and the events that were added."""
    added, hooks = [], settings.setdefault("hooks", {})
    if not isinstance(hooks, dict):
        raise ValueError("settings.json: 'hooks' is not an object")
    for event, entries in hook_block()["hooks"].items():
        current = hooks.setdefault(event, [])
        if not isinstance(current, list):
            raise ValueError(f"settings.json: hooks.{event} is not a list")
        if not _has_board_hook(current, HOOKS[event][1]):
            current.extend(entries)
            added.append(event)
    return settings, added


def enable(root: Path) -> list[str]:
    """Returns human-readable lines of what changed (empty = it was on already)."""
    changes = []
    path = root / SETTINGS
    try:
        settings = json.loads(path.read_text(encoding="utf-8")) if path.exists() else {}
    except (OSError, json.JSONDecodeError) as exc:
        raise ValueError(f"{path} is not valid JSON, not touching it: {exc}") from exc
    if not isinstance(settings, dict):
        raise ValueError(f"{path}: top level is not an object, not touching it")
    settings, added = merge(settings)
    if added:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(settings, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        changes.append(f"{SETTINGS}: added hooks {', '.join(added)}")
    ignore = root / ".gitignore"
    lines = ignore.read_text(encoding="utf-8").splitlines() if ignore.exists() else []
    if GITIGNORE_LINE not in (ln.strip() for ln in lines):
        text = ignore.read_text(encoding="utf-8") if ignore.exists() else ""
        ignore.write_text(text + ("" if text.endswith("\n") or not text else "\n") + GITIGNORE_LINE + "\n",
                          encoding="utf-8")
        changes.append(f".gitignore: added {GITIGNORE_LINE}")
    return changes
