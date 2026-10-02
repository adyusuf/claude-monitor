"""The sessions panel and the cost / ETA figures the board shows (docs/live-board-sessions.md §2c).

Pure functions over the folded state plus a board_cost.Cache: which sessions are busy or
idle and since when, what each session and each task has cost, how full the context is,
and the projections — every projection is an ESTIMATE and carries the numbers it rests on.
"""
from __future__ import annotations

import re
import time
from pathlib import Path

from board_config import (BURN_WINDOW_S, AgentStatus, CONTEXT_WARN_RATIO, SESSION_HIDE_AFTER_S,
                          SESSION_ID_PATTERN, SUBAGENT_DIR, SUBAGENT_PREFIX, TRANSCRIPT_LOOKUP_TTL_S,
                          TRANSCRIPT_SUFFIX, TRANSCRIPTS_ROOT, TaskStatus, TodoStatus)
from board_cost import context_use, epoch, summarize
from board_task_cost import orchestration, shared_counts

CLOSED = (TaskStatus.DONE, TaskStatus.FAILED, TaskStatus.REMOVED)
SECONDS_PER_HOUR = 3600
SECONDS_PER_MIN = 60
MIN_PER_HOUR = 60

_LOOKUPS: dict = {}  # session id -> (looked up at, path | None)


def find_transcript(session_id: str, root: Path | None = None, now: float | None = None) -> str | None:
    """<root>/<project>/<session-id>.jsonl, for a session whose events carried no transcript path.
    The id is checked against SESSION_ID_PATTERN first (it becomes part of a glob); a miss is
    remembered for TRANSCRIPT_LOOKUP_TTL_S so an unmeasurable session is not searched on every poll."""
    if not re.fullmatch(SESSION_ID_PATTERN, session_id or ""):
        return None
    root, now = root or TRANSCRIPTS_ROOT, time.time() if now is None else now
    hit = _LOOKUPS.get((str(root), session_id))
    if hit and (hit[1] or now - hit[0] < TRANSCRIPT_LOOKUP_TTL_S):
        return hit[1]
    found = next(iter(sorted(root.glob(f"*/{session_id}{TRANSCRIPT_SUFFIX}"))), None) if root.is_dir() else None
    _LOOKUPS[(str(root), session_id)] = (now, str(found) if found else None)
    return str(found) if found else None


def session_transcript(sess: dict) -> str | None:
    return sess.get("transcript") or find_transcript(sess.get("id", ""))


def subagent_files(transcript: str | None) -> list:
    """<session>.jsonl keeps its subagents in <session>/subagents/agent-<id>.jsonl."""
    if not transcript:
        return []
    folder = Path(transcript).with_suffix("") / SUBAGENT_DIR
    if not folder.is_dir():
        return []
    return sorted(folder.glob(f"{SUBAGENT_PREFIX}*{TRANSCRIPT_SUFFIX}"))


def agent_paths(state: dict) -> dict:
    """agent id -> transcript path: every session's subagent folder, then the paths the
    SubagentStop hook reported (they win)."""
    paths = {}
    for sess in state["sessions"].values():
        for f in subagent_files(session_transcript(sess)):
            paths[f.name[len(SUBAGENT_PREFIX):-len(TRANSCRIPT_SUFFIX)]] = str(f)
    paths.update(state.get("agent_transcripts") or {})
    return paths


def task_agents(state: dict) -> dict:
    """task id -> the agent ids whose cost is that task's: the [T-n] tag in the Agent call's
    description, overridden by an explicit `board.py set T-n --agent <id>` link."""
    owner = {a["agent_id"]: a["task"] for a in state["agents"].values()
             if a.get("agent_id") and a.get("task")}
    owner.update(state.get("agent_links") or {})
    out: dict = {}
    for aid, tid in owner.items():
        out.setdefault(tid, []).append(aid)
    return out


def remaining_min(task: dict, now: float) -> float | None:
    """The task's own --eta minus the time since it started; None without an ETA."""
    if task.get("eta_min") is None:
        return None
    started = epoch(task.get("started"))
    elapsed = (now - started) / SECONDS_PER_MIN if started else 0.0
    return max(0.0, task["eta_min"] - elapsed)


def task_cost(task: dict, agent_ids: list, paths: dict, cache, now: float, sessions: dict | None = None,
              shared: int = 0) -> dict:
    trs = [t for t in (cache.get(paths.get(a)) for a in agent_ids) if t is not None]
    spent = summarize(trs) if trs else None
    left = remaining_min(task, now)
    # The task's $/h runs from its first spend or its start, whichever came first: agents
    # that ran before the task was marked running must not make the rate look huge.
    begun = [x for x in (epoch(task.get("started")), spent and spent["first"]) if x]
    elapsed_h = (now - min(begun)) / SECONDS_PER_HOUR if begun else 0.0
    projected = None
    if spent and spent["cost"] is not None and left is not None and elapsed_h > 0:
        projected = round(spent["cost"] + spent["cost"] / elapsed_h * left / MIN_PER_HOUR, 4)
    est, cost = task.get("est_cost"), spent["cost"] if spent else None
    return {"agents": len(agent_ids), "measured_agents": len(trs), "spent": spent,
            "orchestration": orchestration(task, sessions or {}, cache, now, shared),  # an estimate, see board_task_cost
            "remaining_min": None if left is None else round(left, 1),
            "projected": projected,  # basis: the task's own $/h x its remaining ETA
            "est_left": None if est is None or cost is None else round(max(0.0, est - cost), 4),
            "over_estimate": bool(est is not None and cost is not None and cost > est)}


def open_eta(state: dict, now: float) -> dict:
    """The remaining hours of the open tasks that have an ETA — what a projection multiplies."""
    open_tasks = [t for t in state["tasks"].values() if t["status"] not in CLOSED]
    lefts = [remaining_min(t, now) for t in open_tasks]
    known = [m for m in lefts if m is not None]
    return {"remaining_h": round(sum(known) / MIN_PER_HOUR, 3), "eta_tasks": len(known),
            "no_eta_tasks": len(lefts) - len(known), "window_s": BURN_WINDOW_S}


def todo_summary(todos: list | None) -> dict | None:
    """done/total and what is being worked on now; None = this session never sent a todo list."""
    if not todos:
        return None
    now = next((t["active"] for t in todos if t["status"] == TodoStatus.IN_PROGRESS), None)
    return {"done": sum(1 for t in todos if t["status"] == TodoStatus.COMPLETED),
            "total": len(todos), "current": now}


def session_view(sess: dict, cache, basis: dict, now: float) -> dict:
    path = session_transcript(sess)
    main = cache.get(path)
    subs = [t for t in (cache.get(str(f)) for f in subagent_files(path)) if t]
    since = now - BURN_WINDOW_S
    total = summarize(([main] if main else []) + subs, since)
    rate = None
    if total["window_cost"] is not None:
        rate = round(total["window_cost"] * SECONDS_PER_HOUR / BURN_WINDOW_S, 4)
    ctx = context_use(main)
    return {"id": sess["id"], "state": sess.get("state"), "since": sess.get("since"),
            "last": sess.get("last"), "has_transcript": main is not None, "title": main.title if main else None,
            "orchestration": summarize([main]) if main else None,
            "agents": summarize(subs), "subagents": len(subs), "total": total,
            "rate_per_h": rate, "context": ctx, "todos": todo_summary(sess.get("todos")),
            "context_warn": bool(ctx and ctx["ratio"] is not None
                                 and ctx["ratio"] >= CONTEXT_WARN_RATIO),
            # basis: rate over the last BURN_WINDOW_S x the open tasks' remaining ETA
            "projected": None if rate is None or total["cost"] is None
            else round(total["cost"] + rate * basis["remaining_h"], 4)}


def agent_costs(state: dict, cache) -> dict:
    """Per Agent-activity row (keyed like state["agents"]) what its transcript has cost so far —
    a running agent's transcript is read incrementally. A row whose agent id or transcript is not
    known yet is `measured: False` (the page says "cannot be measured yet"). `total` covers
    the measured rows only; `by_type` is the same per agent type (the roles panel)."""
    paths = agent_paths(state)
    rows, measured, by_type = {}, [], {}
    for key, a in state["agents"].items():
        tr = cache.get(paths.get(a.get("agent_id") or ""))
        rows[key] = {"measured": tr is not None, "summary": summarize([tr]) if tr else None}
        if tr:
            measured.append(tr)
            by_type.setdefault(a["type"], []).append(tr)
    return {"rows": rows, "total": summarize(measured),
            # a DENIED agent never ran: it has nothing to measure, so it is not "not measured yet"
            "unmeasured": sum(1 for k, r in rows.items()
                              if not r["measured"] and state["agents"][k]["status"] != AgentStatus.DENIED),
            "by_type": {t: summarize(trs) for t, trs in by_type.items()}}


def board_costs(state: dict, cache, now: float) -> dict:
    """Everything cost-related /api/state adds: per session, per task, and the basis."""
    basis = open_eta(state, now)
    shown = [s for s in state["sessions"].values()
             if (epoch(s.get("last")) or 0) >= now - SESSION_HIDE_AFTER_S]
    shown.sort(key=lambda s: s.get("last") or "", reverse=True)
    paths, owners, shared = agent_paths(state), task_agents(state), shared_counts(state, now)
    return {"sessions": [session_view(s, cache, basis, now) for s in shown],
            "tasks": {tid: task_cost(t, owners.get(tid, []), paths, cache, now, state["sessions"], shared.get(tid, 0))
                      for tid, t in state["tasks"].items()},
            "agent_rows": agent_costs(state, cache),
            "basis": basis, "warn_ratio": CONTEXT_WARN_RATIO}
