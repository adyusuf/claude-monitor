"""Single config module for the live board (#2). Every other board file imports from here.

The board is described in docs/live-board.md.
"""
from __future__ import annotations

import hashlib
import os
import subprocess
from pathlib import Path

# DEV fallbacks live here and nowhere else.
_DEFAULT_HOST = "127.0.0.1"
_DEFAULT_PORT = 8765
_BOARD_SUBDIR = Path(".claude") / "board"
# Machine-wide list of projects that have a board — runtime data, outside every repository.
_DEFAULT_REGISTRY = Path.home() / ".cache" / "claude-board" / "projects.json"

HOST = os.environ.get("BOARD_HOST", _DEFAULT_HOST)
# The board has no authentication, so it binds to loopback only unless this is set to "1" on purpose.
ALLOW_REMOTE = os.environ.get("BOARD_ALLOW_REMOTE") == "1"
PORT = int(os.environ.get("BOARD_PORT", _DEFAULT_PORT))
REGISTRY = Path(os.environ.get("BOARD_REGISTRY", _DEFAULT_REGISTRY))
PROJECT_ID_PATTERN = r"^[0-9a-f]{10}$"

# A decision asked on the board: how long the Stop hook waits for the user's click.
# 0 turns the wait off. Capped below the Stop hook's 900 s timeout in the settings block.
_DEFAULT_DECISION_WAIT_S = 180
_MAX_DECISION_WAIT_S = 840


def _int_env(name: str, default: int) -> int:
    try:
        return int(os.environ.get(name, default))
    except ValueError:
        return default  # a typo in the env must not break every hook call


DECISION_WAIT_S = max(0, min(_int_env("BOARD_DECISION_WAIT", _DEFAULT_DECISION_WAIT_S),
                             _MAX_DECISION_WAIT_S))
DECISION_POLL_S = 1.0
CHOICE_MAX = 64
NOTE_MAX = 500
CHANGES_KEPT = 50  # control.json keeps this many changes for sessions not told yet

# The Merge column: where a task's commits have landed, read from the project's git.
MERGE_REMOTE = "origin"
MERGE_BRANCHES = ("dev", "test", "prod")
MERGE_TIP_TTL_S = 10.0  # branch tips are re-read at most this often (the page polls every 1.5 s)
COMMIT_PATTERN = r"^[0-9a-f]{7,40}$"
API_VERSION = 2  # 2 = one server for every registered project
BUILD_SUFFIXES = (".py", ".html", ".js")  # the files whose content makes up the build fingerprint


def code_build(directory: Path | None = None) -> str:
    """A fingerprint of the application's own files. The server reports the one it started with
    (/api/info) and the auto-start compares it with what is on disk now, so a server that has been
    running while the code changed is noticed and replaced instead of silently serving old behaviour."""
    directory = directory or Path(__file__).resolve().parent
    digest = hashlib.sha1()
    for path in sorted(p for p in directory.iterdir() if p.is_file() and p.suffix in BUILD_SUFFIXES):
        digest.update(path.name.encode("utf-8") + b"\0" + path.read_bytes() + b"\0")
    return digest.hexdigest()[:12]


def project_root(start: str) -> Path:
    """The MAIN checkout of the repository `start` is in, so every worktree of a
    project writes to one board. Outside a repository, `start` itself."""
    try:
        common = subprocess.run(
            ["git", "-C", start, "rev-parse", "--path-format=absolute", "--git-common-dir"],
            capture_output=True, text=True, timeout=5, check=True).stdout.strip()
    except (OSError, subprocess.SubprocessError):
        return Path(start)
    return Path(common).parent


def explicit_board_dir() -> str:
    """BOARD_DIR as set in the environment ("" when unset), read at call time so a test can set it."""
    return os.environ.get("BOARD_DIR", "")


def board_dir(cwd: str | None = None) -> Path:
    """BOARD_DIR wins; otherwise <main checkout>/.claude/board.
    CLAUDE_PROJECT_DIR is set for hooks; the CLI falls back to the current directory."""
    explicit = explicit_board_dir()
    if explicit:
        return Path(explicit)
    start = os.environ.get("CLAUDE_PROJECT_DIR") or cwd or os.getcwd()
    return project_root(start) / _BOARD_SUBDIR


def boardable(root: Path) -> bool:
    """False for the home directory and the filesystem root. With the hooks in the USER settings every
    session fires them, including one started in ~ — whose board would land in ~/.claude, the live
    configuration, and a root-level board is never what anyone meant."""
    root = root.resolve()
    return root not in (Path.home().resolve(), Path(root.anchor))


EVENTS_FILE = "events.jsonl"
CONTROL_FILE = "control.json"
ACK_FILE = "control_ack.json"

AGENT_TOOL = "Agent"
BASH_TOOL = "Bash"
# `board.py add|set T-n ...` typed in a session links that session to the task (cost attribution, T-35)
BOARD_CMD_PATTERN = r"board\.py\s+(?:--init\s+)?(?:add|set)\s+(T-\d+)"
TASK_SESSIONS_MAX = 5
TODO_TOOL = "TodoWrite"  # its input is {todos: [{content, status, activeForm}]}, the whole list each time
TASK_CREATE_TOOL = "TaskCreate"  # the incremental task tools; shapes in board_todos.py
TASK_UPDATE_TOOL = "TaskUpdate"
TODO_MAX_ITEMS = 50
TODO_TEXT_MAX = 200
TASK_TAG_PATTERN = r"\[(T-\d+)\]"
TASK_ID_PATTERN = r"^T-\d+$"
AUTO_TASK_ID = "auto"  # `board.py add auto ...`: the next free id, taken under a lock
TASK_LOCK_FILE = "tasks.lock"
ROLE_PATTERN = r"^[a-z0-9][a-z0-9:_-]{0,63}$"
AGENT_ID_PATTERN = r"^[0-9A-Za-z_-]{6,64}$"
ETA_MAX_MIN = 7 * 24 * 60   # board.py set --eta: minutes, 1..this
EST_COST_MAX_USD = 10_000   # board.py set --est-cost: dollars, 0..this


class TaskStatus:
    PLANNED = "planned"
    RUNNING = "running"
    AGENT_DONE = "agent_done"   # the agent returned; the orchestrator has not closed the task yet
    WAITING = "waiting"
    DONE = "done"
    FAILED = "failed"
    REMOVED = "removed"
    NEEDS_DECISION = "needs_decision"  # Claude asked the user; the board shows the choices
    ALL = (PLANNED, RUNNING, AGENT_DONE, WAITING, DONE, FAILED, REMOVED, NEEDS_DECISION)


class AgentStatus:
    STARTING = "starting"
    RUNNING = "running"
    DONE = "done"
    DENIED = "denied"


class ControlAction:
    REMOVE_TASK = "remove_task"
    RESTORE_TASK = "restore_task"
    DISABLE_ROLE = "disable_role"
    ENABLE_ROLE = "enable_role"
    DECIDE = "decide"
    QUEUE_TASK = "queue_task"  # value = session id; the change carries the text
    RUN_SKILL = "run_skill"    # value = skill name; the change carries the session
    SET_MODE = "set_mode"      # value = mode letter
    ALL = (REMOVE_TASK, RESTORE_TASK, DISABLE_ROLE, ENABLE_ROLE, DECIDE, QUEUE_TASK, RUN_SKILL,
           SET_MODE)
    FOR_ONE_SESSION = (QUEUE_TASK, RUN_SKILL)


class DecisionChoice:
    """The two choices a decision offers when Claude named no options of its own."""
    CONTINUE = "continue"
    REJECT = "reject"
    DEFAULTS = (CONTINUE, REJECT)


# ---- Sessions, cost, context (docs/live-board-sessions.md §2c) ----

class TodoStatus:
    PENDING = "pending"
    IN_PROGRESS = "in_progress"
    COMPLETED = "completed"
    DELETED = "deleted"  # TaskUpdate only
    ALL = (PENDING, IN_PROGRESS, COMPLETED)


class SessionState:
    BUSY = "busy"        # UserPromptSubmit seen, no Stop yet
    IDLE = "idle"        # the turn ended
    UNKNOWN = "unknown"  # no turn event recorded for this session yet
    ALL = (BUSY, IDLE, UNKNOWN)


SESSION_ID_PATTERN = r"^[0-9A-Za-z-]{8,64}$"
# Where Claude Code keeps <project>/<session-id>.jsonl — the fallback for a session whose events never
# carried a transcript path (recorded before the hook did, or hooks that do not send it).
TRANSCRIPTS_ROOT = Path(os.environ.get("BOARD_TRANSCRIPTS_ROOT", Path.home() / ".claude" / "projects"))
TRANSCRIPT_LOOKUP_TTL_S = 30.0    # a lookup that found nothing is not repeated on every poll
# A session's name as Claude Code writes it into the transcript: entry type -> the field holding it,
# in the order they win (a title set by the user or the app beats an agent's own name).
TITLE_ENTRIES = {"custom-title": "customTitle", "agent-name": "agentName"}
TITLE_MAX = 80
SESSION_HIDE_AFTER_S = 24 * 3600  # the panel leaves out sessions silent for longer than this
SUBAGENT_DIR = "subagents"        # <session transcript without .jsonl>/subagents/agent-<id>.jsonl
SUBAGENT_PREFIX = "agent-"
TRANSCRIPT_SUFFIX = ".jsonl"

# $/MTok: (input, output, cache read). Source: the claude-api skill, cached 25/09/2026.
# A model not listed here is NOT priced: its cost is "cannot be measured", never a guess.
PRICING_USD_PER_MTOK = {
    "claude-opus-5-5": (4.0, 20.0, 0.20),
    "claude-sonnet-5-5": (2.0, 10.0, 0.20),
    "claude-haiku-4-5": (1.0, 5.0, 0.10),
}
CACHE_WRITE_5M_X = 1.25  # cache write = input price x this (5-minute TTL)
CACHE_WRITE_1H_X = 2.0   # ... x this (1-hour TTL)
TOKENS_PER_MTOK = 1_000_000
CONTEXT_WINDOW = {"claude-opus-5-5": 1_000_000, "claude-sonnet-5-5": 1_000_000,
                  "claude-haiku-4-5": 200_000}
CONTEXT_WARN_RATIO = 0.8          # at or above: "compact suggested" (/compact is the user's)
BURN_WINDOW_S = 3600              # the $/h behind a session projection is measured over this
MODEL_SUFFIX_CHARS = "-[@"        # "claude-haiku-4-5-20251001" still prices as claude-haiku-4-5

# ---- Controls that send work to a session (docs/live-board.md §2d) ----
QUEUE_TEXT_MAX = 1000
MODE_PATTERN = r"^[A-E]$"
MODES = ("A", "B", "C", "D", "E")
MODE_FILE = Path(".claude") / "mode"
SKILL_PATTERN = r"^[a-z0-9][a-z0-9_-]{0,63}(:[a-z0-9][a-z0-9_-]{0,63})?$"
SKILLS_DIR = Path(os.environ.get("BOARD_SKILLS_DIR", Path.home() / ".claude" / "skills"))
SKILL_FILE = "SKILL.md"
PLUGIN_MANIFEST = Path(".claude-plugin") / "plugin.json"
PLUGIN_SKILLS = "skills"      # <plugin>/skills/<name>/SKILL.md -> /<plugin>:<name>
PLUGIN_COMMANDS = "commands"  # <plugin>/commands/<name>.md    -> /<plugin>:<name>
COMMAND_SUFFIX = ".md"
MODE_BY_BOARD = "board"       # the mode_set event's "by": the user picked it on the board

# ---- App mode (docs/live-board.md §2e) ----
APP_NAME = "Claude Monitor"
APP_SHORT_NAME = "Monitor"
APP_THEME_COLOR = "#2f6fdb"
APP_BACKGROUND = "#f7f7f5"
APP_ICON_SIZES = (192, 512)
MAC_CHROME_APP = "Google Chrome"
CHROME_BINARIES = ("google-chrome", "google-chrome-stable", "chromium", "chromium-browser")
OPEN_TIMEOUT_S = 15

# ---- Channel: pushing a queued task into an IDLE session (docs/live-board-sessions.md §5) ----
CHANNEL_SERVER = "board-channel"        # the MCP server name; `server:<this>` in the start flag
CHANNEL_CAPABILITY = "claude/channel"   # experimental capability, and the notification's method prefix
CHANNEL_METHOD = "notifications/claude/channel"
CHANNEL_SESSION_ENV = "CLAUDE_CODE_SESSION_ID"  # set by Claude Code for its MCP children (measured 30/09/2026)


def channel_session_id() -> str:
    """The id of the Claude Code session that started this channel server ("" when it did not pass one)."""
    return os.environ.get(CHANNEL_SESSION_ENV, "")
CHANNEL_FLAGS = ("--dangerously-load-development-channels", "--channels")
CHANNEL_DIR = "channels"                # <board dir>/channels/<session>.json — one file per live server
CHANNEL_DELIVERED_FILE = "channel_delivered.json"
CHANNEL_POLL_S = 1.0
CHANNEL_BEAT_S = 5.0     # a live server rewrites its file this often
CHANNEL_TTL_S = 20.0     # a file older than this is a crashed server: the session is not reachable
CHANNEL_CONFIRM_S = 30.0  # a pushed task the hooks hold back this long; unconfirmed after it -> hooks deliver it
CHANNEL_DELIVERED_KEPT = 200
CHANNEL_FALLBACK_PROTOCOL = "2024-11-05"
