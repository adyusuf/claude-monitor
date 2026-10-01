"""Append-only event log + pure fold into board state, plus the control file."""
from __future__ import annotations

import json
import os
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

from board_todos import TODO_EVENTS, apply as apply_todo
from board_config import (ACK_FILE, CHANGES_KEPT, CONTROL_FILE, EVENTS_FILE, AgentStatus,
                          ControlAction, SessionState, TaskStatus)


def now_iso() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


def _hide_from_git(bdir: Path) -> None:
    """A board created by the hooks alone (they are in the USER settings, so no project ever ran
    `enable`) must not show up in `git status`. The repository's own .git/info/exclude is local:
    nothing to commit, nothing to review, and a project that ignores the folder already is left alone."""
    if bdir.name != "board" or bdir.parent.name != ".claude":
        return  # BOARD_DIR override: not our layout
    root = bdir.parent.parent
    try:
        if subprocess.run(["git", "-C", str(root), "check-ignore", "-q", ".claude/board/events.jsonl"],
                          capture_output=True, timeout=5).returncode == 0:
            return
        common = subprocess.run(["git", "-C", str(root), "rev-parse", "--path-format=absolute", "--git-common-dir"],
                                capture_output=True, text=True, timeout=5, check=True).stdout.strip()
        exclude = Path(common) / "info" / "exclude"
        exclude.parent.mkdir(parents=True, exist_ok=True)
        with exclude.open("a", encoding="utf-8") as f:
            f.write(".claude/board/\n")
    except subprocess.CalledProcessError:
        return  # not a git repository: nothing to hide it from
    except (OSError, subprocess.TimeoutExpired) as exc:
        print(f"board: could not hide {bdir} from git: {exc}", file=sys.stderr)


def append_event(bdir: Path, event: dict) -> None:
    fresh = not bdir.exists()
    bdir.mkdir(parents=True, exist_ok=True)
    if fresh:
        _hide_from_git(bdir)
    event = {"ts": now_iso(), **event}
    line = (json.dumps(event, ensure_ascii=False) + "\n").encode("utf-8")
    # One O_APPEND write per event keeps parallel sessions from interleaving lines.
    fd = os.open(bdir / EVENTS_FILE, os.O_WRONLY | os.O_CREAT | os.O_APPEND, 0o600)
    try:
        os.write(fd, line)
    finally:
        os.close(fd)


def read_events(bdir: Path) -> list[dict]:
    path = bdir / EVENTS_FILE
    if not path.exists():
        return []
    events = []
    with path.open(encoding="utf-8") as f:
        for n, line in enumerate(f, 1):
            line = line.strip()
            if not line:
                continue
            try:
                events.append(json.loads(line))
            except json.JSONDecodeError as exc:
                print(f"board: skipping corrupt event line {n}: {exc}", file=sys.stderr)
    return events


def _task(state: dict, tid: str) -> dict:
    return state["tasks"].setdefault(tid, {
        "id": tid, "title": tid, "branch": "", "role": "", "note": "",
        "status": TaskStatus.PLANNED, "agents": [], "updated": None,
        "options": [], "decision": None, "commits": [],
        "eta_min": None, "est_cost": None, "started": None, "sessions": {},
    })


def _mark_started(task: dict, ts: str | None) -> None:
    if ts and (task["started"] is None or ts < task["started"]):
        task["started"] = ts


def _session(state: dict, sid: str, kind: str | None, ev: dict) -> None:
    """busy from a turn_start until the turn_stop; `since` = when that state began."""
    ts = ev.get("ts")
    sess = state["sessions"].setdefault(sid, {"id": sid, "turn_open": True, "last": ts,
                                              "state": SessionState.UNKNOWN, "since": ts,
                                              "transcript": None})
    sess["last"] = ts
    sess["turn_open"] = kind != "turn_stop"
    if ev.get("transcript"):
        sess["transcript"] = ev["transcript"]
    turn = {"turn_start": SessionState.BUSY, "turn_stop": SessionState.IDLE}.get(kind or "")
    if turn and turn != sess["state"]:
        sess["state"], sess["since"] = turn, ts


def _refresh_task_after_agent(state: dict, tid: str | None, ts: str) -> None:
    if not tid or tid not in state["tasks"]:
        return
    task = state["tasks"][tid]
    if task["status"] in (TaskStatus.DONE, TaskStatus.FAILED, TaskStatus.REMOVED):
        return
    live = [a for a in task["agents"]
            if state["agents"][a]["status"] in (AgentStatus.STARTING, AgentStatus.RUNNING)]
    task["status"] = TaskStatus.RUNNING if live else TaskStatus.AGENT_DONE
    task["updated"] = ts


def fold(events: list[dict], control: dict | None = None) -> dict:
    state = {"mode": None, "roles": None, "tasks": {}, "agents": {}, "sessions": {},
             "last_event": None, "mode_by": None, "agent_links": {}, "agent_transcripts": {}}
    by_agent_id: dict[str, str] = {}
    for ev in events:
        kind, ts = ev.get("type"), ev.get("ts")
        state["last_event"] = ts
        sid = ev.get("session")
        if sid:
            _session(state, sid, kind, ev)
        if kind == "plan":
            state["mode"] = ev.get("mode")
            state["roles"] = list(ev.get("roles", []))
            state["mode_by"] = None
        elif kind in TODO_EVENTS and sid:  # the session's todo list (board_todos.py)
            apply_todo(state["sessions"][sid], kind, ev)
        elif kind == "mode_set":  # the user picked the mode on the board (#27)
            state["mode"], state["mode_by"] = ev.get("mode"), ev.get("by")
        elif kind == "task_add":
            t = _task(state, ev["id"])
            for k in ("title", "branch", "role", "note", "commits"):
                if ev.get(k) is not None:
                    t[k] = ev[k]
            t["updated"] = ts
        elif kind == "task_session" and ev.get("id") and sid:  # the session that worked on the task: first/last touch
            span = _task(state, ev["id"])["sessions"].setdefault(sid, {"first": ts, "last": ts})
            span["last"] = ts
        elif kind == "task_set":
            t = _task(state, ev["id"])
            for k in ("status", "note", "branch", "role", "title", "options", "commits",
                      "eta_min", "est_cost"):
                if ev.get(k) is not None:
                    t[k] = ev[k]
            if ev.get("status") == TaskStatus.RUNNING:
                _mark_started(t, ts)
            if ev.get("agent"):  # board.py set T-n --agent <id>: the agent's cost is this task's
                state["agent_links"][ev["agent"]] = ev["id"]
            t["updated"] = ts
        elif kind in ("agent_pre", "agent_denied"):
            key = ev["tool_use_id"]
            denied = kind == "agent_denied"
            state["agents"][key] = {
                "key": key, "agent_id": None, "type": ev.get("agent_type", ""),
                "task": ev.get("task"), "description": ev.get("description", ""),
                "background": bool(ev.get("background")), "session": sid,
                "status": AgentStatus.DENIED if denied else AgentStatus.STARTING,
                "reason": ev.get("reason"), "started": ts, "ended": ts if denied else None,
            }
            if ev.get("task") and not denied:
                t = _task(state, ev["task"])
                t["agents"].append(key)
                if not t["role"]:
                    t["role"] = ev.get("agent_type", "")
                _mark_started(t, ts)
                _refresh_task_after_agent(state, ev["task"], ts)
        elif kind == "agent_start":
            # SubagentStart: an agent that did not pass through the Agent tool's PreToolUse (resumed
            # with SendMessage, or a Workflow agent) has no row yet. Untyped internal subagents are
            # not agents the user picked and are ignored.
            aid = ev.get("agent_id")
            if not aid or not ev.get("agent_type"):
                continue
            key = by_agent_id.get(aid)
            if key:  # already known: it runs (again) in its own row
                a = state["agents"][key]
                if a["status"] != AgentStatus.DENIED:
                    a["status"], a["ended"] = AgentStatus.RUNNING, None
                    _refresh_task_after_agent(state, a["task"], ts)
                continue
            key = f"agent:{aid}"
            by_agent_id[aid] = key
            state["agents"][key] = {
                "key": key, "agent_id": aid, "type": ev["agent_type"], "task": None,
                "description": "(resumed or workflow agent)", "background": True, "session": sid,
                "status": AgentStatus.RUNNING, "reason": None, "started": ts, "ended": None,
            }
        elif kind == "agent_post":
            a = state["agents"].get(ev["tool_use_id"])
            if not a or a["status"] == AgentStatus.DENIED:  # a denied spawn never comes alive
                continue
            if ev.get("agent_id"):  # a foreground call reports its agentId too
                prior = by_agent_id.get(ev["agent_id"])
                if prior and prior.startswith("agent:") and prior != a["key"]:
                    del state["agents"][prior]  # SubagentStart got here first: one row per agent
                a["agent_id"] = ev["agent_id"]
                by_agent_id[ev["agent_id"]] = a["key"]
            if ev.get("launched") and ev.get("agent_id"):
                a["status"] = AgentStatus.RUNNING
            else:
                a["status"], a["ended"] = AgentStatus.DONE, ts
            _refresh_task_after_agent(state, a["task"], ts)
        elif kind == "agent_stop":
            if ev.get("agent_id") and ev.get("agent_transcript"):
                state["agent_transcripts"][ev["agent_id"]] = ev["agent_transcript"]
            key = by_agent_id.get(ev.get("agent_id", ""))
            if key:
                a = state["agents"][key]
                a["status"], a["ended"] = AgentStatus.DONE, ts
                _refresh_task_after_agent(state, a["task"], ts)
    for tid in (control or {}).get("removed_tasks", []):
        if tid in state["tasks"]:
            state["tasks"][tid]["status"] = TaskStatus.REMOVED
    for tid, decision in (control or {}).get("decisions", {}).items():
        # A decision counts only for the question it answered: once Claude asks
        # again (a later task_set), the old answer no longer shows as the reply.
        task = state["tasks"].get(tid)
        if task and (task["updated"] or "") <= decision.get("ts", ""):
            task["decision"] = decision
    return state


# ---- control file (written by the board server, read by the hook) ----

def empty_control() -> dict:
    return {"version": 0, "removed_tasks": [], "disabled_roles": [], "changes": [], "decisions": {}}


def read_control(bdir: Path) -> dict:
    path = bdir / CONTROL_FILE
    if not path.exists():
        return empty_control()
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        print(f"board: unreadable control file, treating as empty: {exc}", file=sys.stderr)
        return empty_control()
    return {**empty_control(), **data}


def atomic_write(path: Path, data: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(path.suffix + ".tmp")
    fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as f:
        f.write(json.dumps(data, ensure_ascii=False, indent=2))
    os.replace(tmp, path)


def apply_control(bdir: Path, action: str, value: str,
                  choice: str | None = None, note: str | None = None) -> dict:
    ctl = read_control(bdir)
    if action == ControlAction.DECIDE:
        return _decide(bdir, ctl, value, choice, note)
    lists = {ControlAction.REMOVE_TASK: ("removed_tasks", True),
             ControlAction.RESTORE_TASK: ("removed_tasks", False),
             ControlAction.DISABLE_ROLE: ("disabled_roles", True),
             ControlAction.ENABLE_ROLE: ("disabled_roles", False)}
    if action not in lists:
        raise ValueError(f"unknown action: {action}")
    field, add = lists[action]
    items = set(ctl[field])
    items.add(value) if add else items.discard(value)
    ctl[field] = sorted(items)
    return record_change(bdir, ctl, {"action": action, "value": value})


def record_change(bdir: Path, ctl: dict, change: dict) -> dict:
    """Numbers a change, keeps the last CHANGES_KEPT, and writes the control file."""
    ctl["version"] += 1
    ctl["changes"] = (ctl["changes"] + [{"v": ctl["version"], "ts": now_iso(), **change}])[-CHANGES_KEPT:]
    atomic_write(bdir / CONTROL_FILE, ctl)
    return ctl


def _decide(bdir: Path, ctl: dict, tid: str, choice: str | None, note: str | None) -> dict:
    if not choice:
        raise ValueError("a decision needs a choice")
    decision = {"choice": choice, "note": note or "", "ts": now_iso(), "v": ctl["version"] + 1}
    ctl["decisions"][tid] = decision
    return record_change(bdir, ctl, {"action": ControlAction.DECIDE, "value": tid,
                                     "choice": choice, "note": note or "", "ts": decision["ts"]})


def _acks(bdir: Path) -> dict:
    ack_path = bdir / ACK_FILE
    try:
        return json.loads(ack_path.read_text(encoding="utf-8")) if ack_path.exists() else {}
    except (OSError, json.JSONDecodeError):
        return {}


def peek_changes(bdir: Path, session: str) -> list[dict]:
    """Control changes this session has not been told about yet — without marking them."""
    seen = _acks(bdir).get(session, 0)
    return [c for c in read_control(bdir)["changes"] if c["v"] > seen]


def unseen_changes(bdir: Path, session: str) -> list[dict]:
    """Control changes this session has not been told about yet; marks them as seen."""
    fresh = peek_changes(bdir, session)
    if fresh:
        acks = _acks(bdir)
        acks[session] = max(c["v"] for c in fresh)
        atomic_write(bdir / ACK_FILE, acks)
    return fresh
