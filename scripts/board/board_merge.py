"""Where a task's commits have landed — dev / test / prod — read from the project's git
(docs/live-board.md). As of the project's last `git fetch`: the board never fetches.
"""
from __future__ import annotations

import subprocess
import time
from functools import lru_cache

from board_config import MERGE_BRANCHES, MERGE_REMOTE, MERGE_TIP_TTL_S

_tips: dict = {}  # (root, branch) -> (read at, tip sha | None)


def _git(root: str, *args: str):
    try:
        return subprocess.run(["git", "-C", root, *args], capture_output=True, text=True, timeout=5)
    except (OSError, subprocess.SubprocessError):
        return None


def branch_tip(root: str, branch: str, now: float | None = None) -> str | None:
    """The remote branch's commit, cached for MERGE_TIP_TTL_S; None if the branch does not exist."""
    now = time.monotonic() if now is None else now
    hit = _tips.get((root, branch))
    if hit and now - hit[0] < MERGE_TIP_TTL_S:
        return hit[1]
    r = _git(root, "rev-parse", "--verify", "-q", f"{MERGE_REMOTE}/{branch}")
    tip = r.stdout.strip() if r and r.returncode == 0 and r.stdout.strip() else None
    _tips[(root, branch)] = (now, tip)
    return tip


@lru_cache(maxsize=4096)
def _contains(root: str, sha: str, tip: str) -> bool:
    # Keyed by the tip: when the branch moves, the answer is asked again.
    r = _git(root, "merge-base", "--is-ancestor", sha, tip)
    return bool(r) and r.returncode == 0


def merged(root: str, commits: list[str]) -> dict:
    """{branch: True | False | None} for a task's commits — True only when EVERY commit is in the
    branch; None when the branch does not exist. An unknown commit counts as not merged."""
    if not commits:
        return {}
    result = {}
    for branch in MERGE_BRANCHES:
        tip = branch_tip(root, branch)
        result[branch] = None if tip is None else all(_contains(root, c, tip) for c in commits)
    return result
