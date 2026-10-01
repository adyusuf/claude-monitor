"""The board's POST /api/control: validation (#7) and what each action writes.
Split out of board_server.py (#9). Everything is fail-closed: an unknown action, session,
skill or mode is refused, never guessed (docs/live-board.md §2d)."""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

from board_config import (CHOICE_MAX, COMMAND_SUFFIX, MODE_BY_BOARD, MODE_FILE, MODE_PATTERN,
                          NOTE_MAX, PLUGIN_COMMANDS, PLUGIN_MANIFEST, PLUGIN_SKILLS,
                          PROJECT_ID_PATTERN, QUEUE_TEXT_MAX, ROLE_PATTERN, SESSION_ID_PATTERN,
                          SKILL_FILE, SKILL_PATTERN, TASK_ID_PATTERN, ControlAction)
from board_store import (append_event, apply_control, fold, read_control, read_events,
                         record_change)

VALUE_PATTERNS = {
    ControlAction.REMOVE_TASK: TASK_ID_PATTERN, ControlAction.RESTORE_TASK: TASK_ID_PATTERN,
    ControlAction.DECIDE: TASK_ID_PATTERN, ControlAction.DISABLE_ROLE: ROLE_PATTERN,
    ControlAction.ENABLE_ROLE: ROLE_PATTERN, ControlAction.QUEUE_TASK: SESSION_ID_PATTERN,
    ControlAction.RUN_SKILL: SKILL_PATTERN, ControlAction.SET_MODE: MODE_PATTERN,
}
CONTROL_CHARS = re.compile(r"[\x00-\x1f\x7f]")
TEXT_CONTROL_CHARS = re.compile(r"[\x00-\x08\x0b-\x1f\x7f]")  # a queued task may span lines


def validate_control(body: dict) -> tuple[str, str]:
    action, value = body.get("action"), body.get("value")
    if action not in ControlAction.ALL or not isinstance(value, str):
        raise ValueError("invalid action")
    if not re.match(VALUE_PATTERNS[action], value):
        raise ValueError("invalid value")
    project = body.get("project")
    if project is not None and not (isinstance(project, str) and re.match(PROJECT_ID_PATTERN, project)):
        raise ValueError("invalid project")
    return action, value


def decision_fields(body: dict) -> tuple[str, str]:
    """The user's choice and optional note for a decide action, validated server-side (#7)."""
    choice, note = body.get("choice"), body.get("note", "")
    if not isinstance(choice, str) or not 0 < len(choice.strip()) <= CHOICE_MAX \
            or CONTROL_CHARS.search(choice):
        raise ValueError("invalid choice")
    if not isinstance(note, str) or len(note) > NOTE_MAX or CONTROL_CHARS.search(note):
        raise ValueError("invalid note")
    return choice.strip(), note.strip()


def list_skills(root: Path) -> list:
    """Skill names as the user types them: <dir> for a plain skill, <plugin>:<name> for a
    plugin's skills and commands (the adyusuf plugin -> /adyusuf:<name>)."""
    names = set()
    if not root.is_dir():
        return []
    for d in sorted(root.iterdir()):
        if (d / SKILL_FILE).is_file():
            names.add(d.name)
            continue
        manifest = d / PLUGIN_MANIFEST
        if not manifest.is_file():
            continue
        try:
            plugin = json.loads(manifest.read_text(encoding="utf-8")).get("name") or d.name
        except (OSError, ValueError, AttributeError) as exc:
            print(f"board: unreadable plugin manifest {manifest}: {exc}", file=sys.stderr)
            continue
        names.update(f"{plugin}:{s.parent.name}" for s in (d / PLUGIN_SKILLS).glob(f"*/{SKILL_FILE}"))
        names.update(f"{plugin}:{c.stem}" for c in (d / PLUGIN_COMMANDS).glob(f"*{COMMAND_SUFFIX}"))
    return sorted(n for n in names if re.match(SKILL_PATTERN, n))


def _session_request(action: str, value: str, body: dict, skills_dir: Path) -> tuple[str, str]:
    """(target session, the text that reaches it) for queue_task / run_skill."""
    if action == ControlAction.QUEUE_TASK:
        text = body.get("text")
        if not isinstance(text, str) or not 0 < len(text.strip()) <= QUEUE_TEXT_MAX \
                or TEXT_CONTROL_CHARS.search(text):
            raise ValueError("invalid text")
        return value, text.strip()
    session = body.get("session")
    if not isinstance(session, str) or not re.match(SESSION_ID_PATTERN, session):
        raise ValueError("invalid session")
    if value not in list_skills(skills_dir):
        raise ValueError("unknown skill")
    return session, f"invoke the /{value} skill"


def set_mode(entry: dict, mode: str) -> dict:
    """Writes <project root>/.claude/mode and records that the USER chose it (#27)."""
    path = Path(entry["root"]) / MODE_FILE
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(mode + "\n", encoding="utf-8")
    bdir = Path(entry["dir"])
    append_event(bdir, {"type": "mode_set", "mode": mode, "by": MODE_BY_BOARD})
    return record_change(bdir, read_control(bdir), {"action": ControlAction.SET_MODE, "value": mode})


def apply(entry: dict, body: dict, skills_dir: Path) -> dict:
    """Validates one control body and applies it to the project's board. ValueError = refused."""
    action, value = validate_control(body)
    bdir = Path(entry["dir"])
    if action == ControlAction.DECIDE:
        choice, note = decision_fields(body)
        return apply_control(bdir, action, value, choice, note)
    if action in ControlAction.FOR_ONE_SESSION:
        session, text = _session_request(action, value, body, skills_dir)
        if session not in fold(read_events(bdir))["sessions"]:
            raise ValueError("unknown session")
        return record_change(bdir, read_control(bdir), {"action": action, "value": value,
                                                        "session": session, "text": text})
    if action == ControlAction.SET_MODE:
        return set_mode(entry, value)
    return apply_control(bdir, action, value)
