"""What a task cost its ORCHESTRATOR when no agent is linked to it (docs/live-board.md §2c, T-35).

A session that runs `board.py add|set T-n` is recorded against the task (board_hook: task_session). The
task's orchestrator cost is that session's MAIN transcript inside the task's time window — an ESTIMATE:
the session may have done other things in the same hours, so the number says how many other tasks share
the window. The session's subagents are left out: their cost reaches the task through the agent link
(board.py set T-n --agent), and counting them here too would charge it twice.
"""
from __future__ import annotations

from board_config import TASK_SESSIONS_MAX, TaskStatus
from board_cost import epoch, summarize


def task_windows(task: dict, now: float) -> dict:
    """session id -> (start, end) in epoch seconds. A running task's window is still open (until now);
    any other status ends at the session's last touch of the task."""
    spans = task.get("sessions") or {}
    out = {}
    for sid in list(spans)[:TASK_SESSIONS_MAX]:
        first, last = epoch(spans[sid].get("first")), epoch(spans[sid].get("last"))
        if first is None:
            continue
        end = now if task.get("status") == TaskStatus.RUNNING else (last if last is not None else first)
        out[sid] = (first, max(first, end))
    return out


def _overlap(a: tuple, b: tuple) -> bool:
    return a[0] <= b[1] and b[0] <= a[1]


def shared_counts(state: dict, now: float) -> dict:
    """task id -> how many OTHER tasks' windows overlap one of its windows in the same session."""
    wins = {tid: task_windows(t, now) for tid, t in state["tasks"].items()}
    counts = {}
    for tid, mine in wins.items():
        others = {o for o, theirs in wins.items() if o != tid and any(
            sid in theirs and _overlap(w, theirs[sid]) for sid, w in mine.items())}
        counts[tid] = len(others)
    return counts


def orchestration(task: dict, sessions: dict, cache, now: float, shared: int = 0) -> dict | None:
    """{summary, sessions, shared} or None when no session is linked or none has a readable transcript."""
    from board_sessions import session_transcript  # late: board_sessions imports this module
    parts, used = [], []
    for sid, (start, end) in task_windows(task, now).items():
        main = cache.get(session_transcript({"id": sid, **(sessions.get(sid) or {})}))
        if main is None:
            continue
        parts.append(summarize([main], start=start, end=end))
        used.append(sid)
    if not parts:
        return None
    total = {"tokens": {}, "messages": 0, "unpriced": [], "first": None, "cost": 0.0, "window_cost": None}
    for p in parts:
        for k, n in p["tokens"].items():
            total["tokens"][k] = total["tokens"].get(k, 0) + n
        total["messages"] += p["messages"]
        total["unpriced"] = sorted(set(total["unpriced"]) | set(p["unpriced"]))
        total["cost"] = None if total["cost"] is None or p["cost"] is None else total["cost"] + p["cost"]
    if total["cost"] is not None:
        total["cost"] = round(total["cost"], 4)
    return {"summary": total, "sessions": used, "shared": shared}
