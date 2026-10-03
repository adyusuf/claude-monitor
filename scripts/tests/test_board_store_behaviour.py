"""board_store behaviours that the mutation check found nothing was checking
(scripts/mutation_check.py on board_store.py: 27 of 60 changes survived).

Each test below names the change that used to survive it. They pin rules that are easy to lose:
a decision counts only for the question it answered, the EARLIEST start wins, a plain event never moves a
session's state, a resumed agent and the call that launched it are ONE row, and the board stays out of
`git status` without ever touching a project that already ignores it.
"""
import io
import subprocess
import sys
import tempfile
import unittest
from contextlib import redirect_stderr
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "board"))

from board_config import AgentStatus, SessionState, TaskStatus  # noqa: E402
from board_store import append_event, fold  # noqa: E402


def task_set(tid, ts, **fields):
    return {"type": "task_set", "id": tid, "ts": ts, **fields}


class Decisions(unittest.TestCase):
    ASKED = [{"type": "task_add", "id": "T-1", "ts": "2026-10-02T10:00:00Z"}]

    def decide(self, ts, events=None):
        return fold(events or self.ASKED, {"decisions": {"T-1": {"ts": ts, "value": "go"}}})["tasks"]["T-1"]["decision"]

    def test_an_answer_after_the_question_shows(self):                       # <=  (LtE -> Gt)
        self.assertEqual(self.decide("2026-10-02T11:00:00Z")["value"], "go")

    def test_an_answer_at_the_same_second_shows(self):
        self.assertEqual(self.decide("2026-10-02T10:00:00Z")["value"], "go")

    def test_an_answer_older_than_the_latest_question_does_not(self):
        asked_again = self.ASKED + [task_set("T-1", "2026-10-02T12:00:00Z", status="needs_decision")]
        self.assertIsNone(self.decide("2026-10-02T11:00:00Z", asked_again))

    def test_a_decision_for_a_task_the_board_never_saw_is_ignored(self):    # and -> or
        state = fold([], {"decisions": {"T-9": {"ts": "2026-10-02T11:00:00Z", "value": "go"}}})
        self.assertEqual(state["tasks"], {})


class Sessions(unittest.TestCase):
    def test_turns_move_the_state_and_other_events_do_not(self):             # Or -> And, And -> Or
        ev = lambda kind, ts: {"type": kind, "session": "s1", "ts": ts, "id": "T-1"}
        self.assertEqual(fold([ev("task_set", "t1")])["sessions"]["s1"]["state"], SessionState.UNKNOWN)
        state = fold([ev("turn_start", "t1"), ev("task_set", "t2")])["sessions"]["s1"]
        self.assertEqual((state["state"], state["since"]), (SessionState.BUSY, "t1"))
        state = fold([ev("turn_start", "t1"), ev("turn_stop", "t2"), ev("task_set", "t3")])["sessions"]["s1"]
        self.assertEqual((state["state"], state["since"], state["turn_open"]), (SessionState.IDLE, "t2", True))

    def test_an_event_with_no_type_does_not_break_the_session(self):
        state = fold([{"session": "s1", "ts": "t1"}])["sessions"]["s1"]
        self.assertEqual(state["state"], SessionState.UNKNOWN)

    def test_the_session_that_touched_a_task_is_recorded_only_by_task_session(self):   # Eq -> NotEq
        state = fold([task_set("T-1", "t1", session="s1", status="running"),
                      {"type": "task_session", "id": "T-1", "session": "s2", "ts": "t2"},
                      {"type": "task_session", "id": "T-1", "session": "s2", "ts": "t3"}])
        self.assertEqual(state["tasks"]["T-1"]["sessions"], {"s2": {"first": "t2", "last": "t3"}})


class StartedAt(unittest.TestCase):
    def test_the_earliest_start_wins_even_when_it_arrives_late(self):          # Lt -> GtE
        state = fold([task_set("T-1", "2026-10-02T10:30:00Z", status="running"),
                      task_set("T-1", "2026-10-02T10:00:00Z", status="running"),
                      task_set("T-1", "2026-10-02T11:00:00Z", status="running")])
        self.assertEqual(state["tasks"]["T-1"]["started"], "2026-10-02T10:00:00Z")

    def test_only_running_starts_the_clock(self):                              # Eq -> NotEq
        state = fold([task_set("T-1", "t1", status="done"), task_set("T-2", "t1", status="waiting")])
        self.assertIsNone(state["tasks"]["T-1"]["started"])
        self.assertIsNone(state["tasks"]["T-2"]["started"])


def agent_events(**start):
    return [{"type": "agent_start", "session": "s1", "ts": "t1", **start}]


class ResumedAgents(unittest.TestCase):
    def test_an_agent_start_needs_both_an_id_and_a_type(self):                # Or -> And, `not` removed
        for partial in ({"agent_id": "a1"}, {"agent_type": "dev"}, {}):
            self.assertEqual(fold(agent_events(**partial))["agents"], {}, partial)
        row = fold(agent_events(agent_id="a1", agent_type="dev"))["agents"]["agent:a1"]
        self.assertEqual((row["status"], row["background"], row["task"]), (AgentStatus.RUNNING, True, None))
        self.assertEqual(row["description"], "(resumed or workflow agent)")

    def test_stopping_an_agent_that_has_no_task_changes_no_task(self):         # Or -> And in the refresh guard
        state = fold(agent_events(agent_id="a1", agent_type="dev")
                     + [{"type": "agent_stop", "agent_id": "a1", "ts": "t2"}])
        self.assertEqual((state["agents"]["agent:a1"]["status"], state["tasks"]), (AgentStatus.DONE, {}))

    def test_an_agent_resumed_after_it_finished_runs_again_in_its_own_row(self):   # NotEq -> Eq
        pre = {"type": "agent_pre", "session": "s1", "tool_use_id": "u1", "agent_type": "dev", "ts": "t1"}
        post = {"type": "agent_post", "tool_use_id": "u1", "agent_id": "a1", "launched": False, "ts": "t2"}
        done = fold([pre, post])["agents"]["u1"]
        self.assertEqual(done["status"], AgentStatus.DONE)
        again = fold([pre, post, {"type": "agent_start", "agent_id": "a1", "agent_type": "dev", "ts": "t3"}])
        self.assertEqual(again["agents"]["u1"]["status"], AgentStatus.RUNNING)
        self.assertIsNone(again["agents"]["u1"]["ended"])

    def test_the_resumed_row_and_the_call_that_launched_it_are_one_row(self):  # NotEq -> Eq on the prior key
        events = agent_events(agent_id="a1", agent_type="dev") + [
            {"type": "agent_pre", "session": "s1", "tool_use_id": "u1", "agent_type": "dev", "ts": "t2"},
            {"type": "agent_post", "tool_use_id": "u1", "agent_id": "a1", "launched": True, "ts": "t3"}]
        agents = fold(events)["agents"]
        self.assertEqual(list(agents), ["u1"])
        self.assertEqual((agents["u1"]["agent_id"], agents["u1"]["status"]), ("a1", AgentStatus.RUNNING))

    def test_a_call_is_running_only_when_it_launched_and_reported_an_id(self):   # And -> Or
        pre = {"type": "agent_pre", "session": "s1", "tool_use_id": "u1", "agent_type": "dev", "ts": "t1"}
        run = lambda **post: fold([pre, {"type": "agent_post", "tool_use_id": "u1", "ts": "t2", **post}])["agents"]["u1"]["status"]
        self.assertEqual(run(launched=True, agent_id="a1"), AgentStatus.RUNNING)
        self.assertEqual(run(launched=True), AgentStatus.DONE)
        self.assertEqual(run(agent_id="a1"), AgentStatus.DONE)


class HidingTheBoardFromGit(unittest.TestCase):
    def repo(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        root = Path(tmp.name)
        subprocess.run(["git", "init", "-q", str(root)], check=True)
        return root

    def excluded(self, root):
        path = root / ".git" / "info" / "exclude"
        return path.read_text() if path.exists() else ""

    def test_a_new_board_is_kept_out_of_git_status_once(self):                # `not` removed, True -> False
        root = self.repo()
        bdir = root / ".claude" / "board"
        append_event(bdir, {"type": "plan"})
        self.assertEqual(self.excluded(root).count(".claude/board/"), 1)      # at the FIRST write, not a later one
        append_event(bdir, {"type": "plan"})
        self.assertEqual(self.excluded(root).count(".claude/board/"), 1)
        status = subprocess.run(["git", "-C", str(root), "status", "--porcelain"], capture_output=True, text=True)
        self.assertEqual(status.stdout.strip(), "")

    def test_a_project_that_already_ignores_the_board_is_left_alone(self):    # check-ignore Eq -> NotEq
        root = self.repo()
        (root / ".gitignore").write_text(".claude/board/\n")
        append_event(root / ".claude" / "board", {"type": "plan"})
        self.assertNotIn(".claude/board/", self.excluded(root))

    def test_another_layout_is_not_touched(self):                             # Or -> And, NotEq -> Eq
        root = self.repo()
        for odd in (root / "elsewhere" / "board", root / ".claude" / "data", root / "x" / ".claude"):
            append_event(odd, {"type": "plan"})
        self.assertNotIn("board", self.excluded(root))

    def test_a_folder_that_is_not_a_repository_is_fine_and_leaves_nothing_behind(self):   # check=True -> False
        import os
        with tempfile.TemporaryDirectory() as tmp, tempfile.TemporaryDirectory() as cwd:
            bdir = Path(tmp) / ".claude" / "board"
            err, before = io.StringIO(), os.getcwd()
            os.chdir(cwd)
            try:
                with redirect_stderr(err):
                    append_event(bdir, {"type": "plan"})
            finally:
                os.chdir(before)
            self.assertTrue((bdir / "events.jsonl").exists())
            self.assertEqual(err.getvalue(), "")
            self.assertEqual(os.listdir(cwd), [])           # a failed git call must not write ./info/exclude


if __name__ == "__main__":
    unittest.main()
