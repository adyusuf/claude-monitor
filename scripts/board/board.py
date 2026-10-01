"""CLI Claude uses to write the plan onto the live board (docs/live-board.md).

  B=~/.claude/scripts/board/board.py
  python3 $B plan --mode B --roles analyst,test-writer,doc-writer
  python3 $B add auto "F-063 trusted proxies" --branch fix/f063 --role developer   # prints the new id
  python3 $B add T-1 "F-063 trusted proxies"      # an id that exists is refused
  python3 $B set T-1 --status waiting --note "tests later"
  python3 $B set T-1 --commit 3e2e5e2,5d3067f     # the Merge column reads these from git
  python3 $B set T-2 --status needs_decision --note "Split the PR?" --options "split|keep one"
  python3 $B set T-1 --eta 45 --est-cost 3.5      # the board projects time and cost from these
  python3 $B set T-1 --agent a3f971dd8bd84040e    # this agent's cost counts for T-1
  python3 $B list
Agents are linked to a task by putting "[T-1]" in the Agent tool's description.
"""
from __future__ import annotations

import argparse
import os
import re
import sys

from board_config import (AGENT_ID_PATTERN, AUTO_TASK_ID, CHOICE_MAX, COMMIT_PATTERN, EST_COST_MAX_USD,
                          ETA_MAX_MIN, EVENTS_FILE, ROLE_PATTERN, TASK_ID_PATTERN, TaskStatus, board_dir)
from board_enable import checkout_root, enable
from board_registry import register, register_if_missing
from board_store import append_event, fold, read_control, read_events
from board_tasks import TaskExists, add_task


def _task_id(value: str) -> str:
    if not re.match(TASK_ID_PATTERN, value):
        raise argparse.ArgumentTypeError(f"task id must look like T-1, got {value!r}")
    return value


def _new_task_id(value: str) -> str:
    """`add` takes a T-n id or 'auto' (the next free one)."""
    return value if value == AUTO_TASK_ID else _task_id(value)


def _roles(value: str) -> list[str]:
    roles = [r.strip() for r in value.split(",") if r.strip()]
    bad = [r for r in roles if not re.match(ROLE_PATTERN, r)]
    if bad:
        raise argparse.ArgumentTypeError(f"invalid role name(s): {', '.join(bad)}")
    return roles


def _options(value: str) -> list[str]:
    options = [o.strip() for o in value.split("|") if o.strip()]
    if not options or any(len(o) > CHOICE_MAX for o in options):
        raise argparse.ArgumentTypeError(f"options: 'a|b|c', each 1-{CHOICE_MAX} characters")
    return options


def _commits(value: str) -> list[str]:
    commits = [c.strip().lower() for c in value.split(",") if c.strip()]
    if not commits or not all(re.match(COMMIT_PATTERN, c) for c in commits):
        raise argparse.ArgumentTypeError("commits: 'sha[,sha]', each 7-40 hex characters")
    return commits


def _eta(value: str) -> int:
    try:
        minutes = int(value)
    except ValueError:
        minutes = 0
    if not 0 < minutes <= ETA_MAX_MIN:
        raise argparse.ArgumentTypeError(f"--eta: whole minutes, 1-{ETA_MAX_MIN}")
    return minutes


def _usd(value: str) -> float:
    try:
        usd = float(value)
    except ValueError:
        usd = -1.0
    if not 0 <= usd <= EST_COST_MAX_USD:  # also refuses nan and inf
        raise argparse.ArgumentTypeError(f"--est-cost: dollars, 0-{EST_COST_MAX_USD}")
    return usd


def _agent(value: str) -> str:
    if not re.match(AGENT_ID_PATTERN, value):
        raise argparse.ArgumentTypeError(f"--agent: an agent id, got {value!r}")
    return value


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(prog="board")
    p.add_argument("--init", action="store_true",
                   help="create the board of this repository (refused otherwise when it has none)")
    sub = p.add_subparsers(dest="cmd", required=True)
    plan = sub.add_parser("plan")
    plan.add_argument("--mode", required=True)
    plan.add_argument("--roles", type=_roles, default=[])
    add = sub.add_parser("add")
    add.add_argument("id", type=_new_task_id, help="T-n, or 'auto' for the next free id (printed)")
    add.add_argument("title")
    add.add_argument("--branch", default="")
    add.add_argument("--role", default="")
    add.add_argument("--note", default="")
    add.add_argument("--commit", dest="commits", type=_commits, help="the task's commit(s): 'sha[,sha]'")
    st = sub.add_parser("set")
    st.add_argument("id", type=_task_id)
    st.add_argument("--status", choices=TaskStatus.ALL)
    st.add_argument("--note")
    st.add_argument("--branch")
    st.add_argument("--role")
    st.add_argument("--title")
    st.add_argument("--commit", dest="commits", type=_commits, help="the task's commit(s): 'sha[,sha]'")
    st.add_argument("--options", type=_options, help="choices for a needs_decision task: 'a|b'")
    st.add_argument("--eta", dest="eta_min", type=_eta, help="estimated minutes for the task")
    st.add_argument("--est-cost", dest="est_cost", type=_usd, help="estimated dollars for the task")
    st.add_argument("--agent", type=_agent, help="count this agent's cost for the task")
    sub.add_parser("list")
    sub.add_parser("enable", help="turn the live board on in this repository: hooks, .gitignore, listing")
    return p


def run(argv: list[str]) -> int:
    args = build_parser().parse_args(argv)
    bdir = board_dir()
    # The board is found from the working directory's repository, so a command typed from another
    # project's tree used to create a stray .claude/board/ there. A repository whose hooks are
    # wired already has one (the first hook event creates it); anything else needs --init.
    if (args.cmd not in ("list", "enable") and not args.init and not os.environ.get("BOARD_DIR")
            and not (bdir / EVENTS_FILE).exists()):
        print(f"board: no live board at {bdir}. This repository has no board hooks; run the command "
              "from the repository whose board you mean, or pass --init to create one here.",
              file=sys.stderr)
        return 2
    if args.cmd == "enable":
        root = bdir.parent.parent  # the main checkout: where the board is listed
        try:
            changes = enable(checkout_root(root))  # the checkout you are in: where the files are written
        except ValueError as exc:
            print(f"board: {exc}", file=sys.stderr)
            return 2
        register(root, bdir)
        print("\n".join(changes) if changes else "live board is on already: nothing to change")
        print(f"listed: {root.name}. Commit .claude/settings.json (and .gitignore); a running session picks the hooks "
              "up, SessionStart (auto-start of the server) applies from the next session.")
        return 0
    if args.cmd != "list" and not os.environ.get("BOARD_DIR"):
        # Not through the SessionStart hook alone: ryan had a board full of tasks that no page listed
        # because its repository had no hooks. BOARD_DIR is an override (tests, odd layouts): no project root to list.
        register_if_missing(bdir.parent.parent, bdir)
    if args.cmd == "plan":
        append_event(bdir, {"type": "plan", "mode": args.mode, "roles": args.roles})
    elif args.cmd == "add":
        try:
            task_id = add_task(bdir, {"type": "task_add", "id": args.id, "title": args.title,
                                      "branch": args.branch, "role": args.role, "note": args.note,
                                      **({"commits": args.commits} if args.commits else {})})
        except TaskExists as exc:
            print(f"board add: {exc.task_id} already exists — use `add {AUTO_TASK_ID}` for the next "
                  "free id, or `set` to change that task.", file=sys.stderr)
            return 2
        print(task_id)
    elif args.cmd == "set":
        fields = {k: getattr(args, k) for k in ("status", "note", "branch", "role", "title", "options",
                                               "commits", "eta_min", "est_cost", "agent")
                  if getattr(args, k) is not None}
        if not fields:
            print("board set: nothing to change", file=sys.stderr)
            return 2
        append_event(bdir, {"type": "task_set", "id": args.id, **fields})
    elif args.cmd == "list":
        state = fold(read_events(bdir), read_control(bdir))
        for t in state["tasks"].values():
            print(f"{t['id']}\t{t['status']}\t{t['role']}\t{t['title']}")
    return 0


if __name__ == "__main__":
    sys.exit(run(sys.argv[1:]))
