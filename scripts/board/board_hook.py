"""Claude Code hook entrypoint: records agent lifecycle and enforces board controls.

Wire it for PreToolUse/PostToolUse (matcher "Agent"), SubagentStart, SubagentStop, Stop,
UserPromptSubmit and PostToolUse (matcher "*") — the settings block is in
docs/live-board.md. The Stop hook also waits, for a bounded time, for the
user's answer to a question Claude put on the board (needs_decision). A task or skill the
user queued for a session reaches it after its next main-thread tool call, or at Stop.
"""
from __future__ import annotations

import json
import re
import sys
import time

from board_config import (AGENT_TOOL, DECISION_POLL_S, DECISION_WAIT_S, TASK_TAG_PATTERN,
                          BASH_TOOL, BOARD_CMD_PATTERN, TASK_CREATE_TOOL, TASK_UPDATE_TOOL, TODO_TOOL,
                          ControlAction, TaskStatus, board_dir, boardable, explicit_board_dir)
import board_todos
from board_registry import register_if_missing
from board_channel_ack import confirm as _channel_confirm
from board_channel_ack import peek_changes as _peek_changes
from board_channel_ack import unseen_changes
from board_store import append_event, fold, read_events
from board_control import read_control


def task_tag(text: str) -> str | None:
    m = re.search(TASK_TAG_PATTERN, text or "")
    return m.group(1) if m else None


def deny_reason(agent_type: str, task: str | None, control: dict,
                allowed_roles: list | None) -> str | None:
    """None = no plan declared yet (nothing to enforce); [] = a declared EMPTY set
    (mode A: no agents), which denies every agent — fail-closed (#6)."""
    if agent_type in control["disabled_roles"]:
        return f"The '{agent_type}' agent is switched off on the board by the user."
    if task and task in control["removed_tasks"]:
        return f"Task {task} was removed from the board by the user; do not work on it."
    if allowed_roles is not None and agent_type not in allowed_roles:
        return (f"'{agent_type}' is outside the active mode's role set "
                f"({', '.join(allowed_roles) or 'none'}). Propose a mode change instead.")
    return None


def _for_me(change: dict, session: str) -> bool:
    """A queued task or skill is meant for ONE session; every other change is for all."""
    return change["action"] not in ControlAction.FOR_ONE_SESSION or change.get("session") == session


def _change_text(changes: list[dict], session: str) -> str | None:
    labels = {"remove_task": "removed task", "restore_task": "restored task",
              "disable_role": "switched off agent", "enable_role": "switched on agent"}
    lines = []
    for c in (c for c in changes if _for_me(c, session)):
        if c["action"] in ControlAction.FOR_ONE_SESSION:
            lines.append(f"- NEW TASK from the user, queued on the board for this session: "
                         f"{c.get('text', '')} (do it after the current step; put it on the board)")
        elif c["action"] == ControlAction.SET_MODE:
            lines.append(f"- working mode changed to {c['value']} by the user on the board "
                         f"(.claude/mode written; this selection is the approval, #27) — re-declare "
                         f"the plan: board.py plan --mode {c['value']} --roles <that mode's role set>")
        elif c["action"] == ControlAction.DECIDE:
            note = f" — note: {c['note']}" if c.get("note") else ""
            lines.append(f"- decided {c['value']}: {c.get('choice')}{note} "
                         f"(apply it, then move the task out of needs_decision)")
        else:
            lines.append(f"- {labels.get(c['action'], c['action'])} {c['value']}")
    if not lines:
        return None
    return ("The user changed the live board:\n" + "\n".join(lines) +
            "\nApply this: skip removed tasks, do not start switched-off agents, "
            "and stop any running agent for a removed task (TaskStop).")


def _asks_the_user(bdir) -> bool:
    tasks = fold(read_events(bdir), read_control(bdir))["tasks"].values()
    return any(t["status"] == TaskStatus.NEEDS_DECISION and not t["decision"] for t in tasks)


def wait_for_decision(bdir, session: str, wait_s: float, poll_s: float = DECISION_POLL_S):
    """At the end of a turn: hand over a decision the user already clicked or a task queued
    for THIS session, or, while a question is open on the board, wait up to wait_s for one.
    Either keeps the turn going (Stop is blocked with it as the reason); anything else, or
    the time running out, lets the turn end. Other control changes wait for the next turn."""
    deadline = time.monotonic() + (wait_s if wait_s > 0 and _asks_the_user(bdir) else 0)
    keeps_going = (ControlAction.DECIDE,) + ControlAction.FOR_ONE_SESSION
    while True:
        pending = [c for c in _peek_changes(bdir, session)
                   if c["action"] in keeps_going and _for_me(c, session)]
        if pending:
            changes = unseen_changes(bdir, session)
            append_event(bdir, {"type": "turn_start", "session": session})
            return {"decision": "block", "reason": _change_text(changes, session)}
        if time.monotonic() >= deadline:
            return None
        time.sleep(poll_s)


def _record_todo(bdir, session: str, tool: str, tin: dict, resp) -> None:
    """The main session's todo tools (a subagent's own list is not the session's) — see board_todos.py."""
    if tool == TODO_TOOL:
        items = board_todos.snapshot(tin)
        event = None if items is None else {"type": "todo_sync", "todos": items}
    elif tool == TASK_CREATE_TOOL:
        item = board_todos.created(tin, resp)
        event = None if item is None else {"type": "todo_add", "item": item}
    else:
        change = board_todos.updated(tin)
        event = None if change is None else {"type": "todo_update", "change": change}
    if event:
        append_event(bdir, {"session": session, **event})


def _context(event_name: str, text: str) -> dict:
    return {"hookSpecificOutput": {"hookEventName": event_name, "additionalContext": text}}


def handle(payload: dict) -> dict | None:
    event = payload.get("hook_event_name")
    session = payload.get("session_id", "")
    bdir = board_dir(payload.get("cwd"))
    if not explicit_board_dir() and not boardable(bdir.parent.parent):
        return None  # the hooks run in every session once they are in the user settings (board.py enable --user)
    if not explicit_board_dir():  # a project with only some hooks (no SessionStart) is still listed
        register_if_missing(bdir.parent.parent, bdir)
    tool = payload.get("tool_name")
    tin = payload.get("tool_input") or {}

    if event == "PreToolUse" and tool == AGENT_TOOL:
        agent_type = tin.get("subagent_type") or "general-purpose"
        desc = tin.get("description", "")
        task = task_tag(desc)
        control = read_control(bdir)
        roles = fold(read_events(bdir))["roles"]
        reason = deny_reason(agent_type, task, control, roles)
        base = {"session": session, "tool_use_id": payload.get("tool_use_id"),
                "agent_type": agent_type, "description": desc, "task": task,
                "background": tin.get("run_in_background", True)}
        if reason:
            append_event(bdir, {"type": "agent_denied", "reason": reason, **base})
            return {"hookSpecificOutput": {"hookEventName": "PreToolUse",
                                           "permissionDecision": "deny",
                                           "permissionDecisionReason": reason}}
        append_event(bdir, {"type": "agent_pre", **base})
        return None

    if event == "PostToolUse":
        if tool == AGENT_TOOL:
            resp = payload.get("tool_response") or {}
            resp = resp if isinstance(resp, dict) else {}
            launched = resp.get("status") == "async_launched"
            append_event(bdir, {"type": "agent_post", "session": session,
                                "tool_use_id": payload.get("tool_use_id"),
                                "launched": launched, "agent_id": resp.get("agentId")})
        if tool in (TODO_TOOL, TASK_CREATE_TOOL, TASK_UPDATE_TOOL) and not payload.get("agent_id"):
            _record_todo(bdir, session, tool, tin, payload.get("tool_response"))
        if tool == BASH_TOOL and session and not payload.get("agent_id"):
            # A session that runs `board.py add|set T-n` is the one working on T-n: its own (orchestrator) cost
            # in the task's time window belongs to the task even when no agent is linked to it.
            for tid in dict.fromkeys(re.findall(BOARD_CMD_PATTERN, str(tin.get("command") or ""))):
                append_event(bdir, {"type": "task_session", "session": session, "id": tid})
        if payload.get("agent_id"):
            # A subagent's tool call (modes C/D/E): it must not consume a notice meant
            # for the orchestrator, or the orchestrator would never see the change.
            return None
        text = _change_text(unseen_changes(bdir, session), session)
        return _context("PostToolUse", text) if text else None

    if event == "SubagentStart":
        # Opens a row for agents that never pass through the Agent tool (SendMessage resumes,
        # Workflow agents); an untyped internal subagent has no agent_type and is ignored.
        if payload.get("agent_id") and payload.get("agent_type"):
            append_event(bdir, {"type": "agent_start", "session": session,
                                "agent_id": payload["agent_id"], "agent_type": payload["agent_type"]})
        return None

    if event == "SubagentStop":
        append_event(bdir, {"type": "agent_stop", "session": session,
                            "agent_id": payload.get("agent_id"),
                            "agent_type": payload.get("agent_type"),
                            "agent_transcript": payload.get("agent_transcript_path")})
        return None

    if event == "Stop":
        append_event(bdir, {"type": "turn_stop", "session": session,
                            "transcript": payload.get("transcript_path")})
        _channel_confirm(bdir, session)
        return wait_for_decision(bdir, session, DECISION_WAIT_S)

    if event == "UserPromptSubmit":
        append_event(bdir, {"type": "turn_start", "session": session,
                            "transcript": payload.get("transcript_path")})
        _channel_confirm(bdir, session)
        text = _change_text(unseen_changes(bdir, session), session)
        return _context("UserPromptSubmit", text) if text else None

    return None


def main() -> int:
    try:
        payload = json.load(sys.stdin)
        out = handle(payload)
    except Exception as exc:  # the board must never break a session; log and let the call through
        print(f"board hook error: {exc!r}", file=sys.stderr)
        return 0
    if out:
        print(json.dumps(out))
    return 0


if __name__ == "__main__":
    sys.exit(main())
