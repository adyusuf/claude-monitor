"""The main session's TodoWrite list on the board (T-2): the hook records the whole list, the fold
keeps the latest one per session, the sessions view summarises it. A payload that is not the
documented shape records nothing."""
import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "board"))

import board_hook  # noqa: E402
from board_sessions import todo_summary  # noqa: E402
from board_store import fold, read_events  # noqa: E402


def todo_write(todos, session="s1", **extra):
    return {"hook_event_name": "PostToolUse", "session_id": session, "tool_name": "TodoWrite",
            "tool_input": {"todos": todos}, "tool_response": {}, **extra}


def item(content, status, active=None):
    return {"content": content, "status": status, "activeForm": active or content + "ing"}


class TodoSyncTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        env = mock.patch.dict(os.environ, {"BOARD_DIR": self.tmp.name})
        env.start()
        self.addCleanup(env.stop)
        self.bdir = Path(self.tmp.name)

    def sessions(self):
        return fold(read_events(self.bdir))["sessions"]

    def test_records_the_list_and_the_in_progress_label(self):
        board_hook.handle(todo_write([item("Fix", "completed"), item("Test", "in_progress", "Testing"),
                                      item("Ship", "pending")]))
        todos = self.sessions()["s1"]["todos"]
        self.assertEqual([t["status"] for t in todos], ["completed", "in_progress", "pending"])
        self.assertEqual(todos[1]["active"], "Testing")
        self.assertEqual(todos[0]["active"], "Fix")  # only the running item uses activeForm

    def test_latest_list_replaces_the_previous_one(self):
        board_hook.handle(todo_write([item("A", "pending"), item("B", "pending")]))
        board_hook.handle(todo_write([item("A", "completed")]))
        self.assertEqual(len(self.sessions()["s1"]["todos"]), 1)

    def test_lists_are_per_session(self):
        board_hook.handle(todo_write([item("A", "pending")], session="s1"))
        board_hook.handle(todo_write([item("B", "pending"), item("C", "pending")], session="s2"))
        s = self.sessions()
        self.assertEqual((len(s["s1"]["todos"]), len(s["s2"]["todos"])), (1, 2))

    def test_a_subagents_todo_call_is_not_the_sessions_list(self):
        board_hook.handle(todo_write([item("A", "pending")], agent_id="ag123456"))
        self.assertEqual(read_events(self.bdir), [])

    def test_undocumented_shapes_record_nothing(self):
        for bad in (None, "x", {"a": 1}):
            board_hook.handle({"hook_event_name": "PostToolUse", "session_id": "s1",
                               "tool_name": "TodoWrite", "tool_input": {"todos": bad}})
        board_hook.handle({"hook_event_name": "PostToolUse", "session_id": "s1",
                           "tool_name": "TodoWrite", "tool_input": None})
        self.assertEqual([e for e in read_events(self.bdir) if e["type"] == "todo_sync"], [])

    def test_unknown_status_and_junk_entries_are_dropped_and_lists_are_capped(self):
        many = [item(f"t{i}", "pending") for i in range(80)]
        board_hook.handle(todo_write([item("ok", "completed"), item("x", "blocked"), "junk", None] + many))
        todos = self.sessions()["s1"]["todos"]
        self.assertEqual(todos[0]["content"], "ok")
        self.assertNotIn("x", [t["content"] for t in todos])
        self.assertLessEqual(len(todos), 50)

    def test_long_text_is_cut(self):
        board_hook.handle(todo_write([item("y" * 900, "in_progress", "z" * 900)]))
        t = self.sessions()["s1"]["todos"][0]
        self.assertEqual((len(t["content"]), len(t["active"])), (200, 200))

    def test_an_empty_list_clears_it(self):
        board_hook.handle(todo_write([item("A", "pending")]))
        board_hook.handle(todo_write([]))
        self.assertEqual(self.sessions()["s1"]["todos"], [])
        self.assertIsNone(todo_summary(self.sessions()["s1"]["todos"]))


class TodoSummaryTest(unittest.TestCase):
    def test_counts_and_current(self):
        s = todo_summary([{"status": "completed", "active": "a"}, {"status": "in_progress", "active": "Doing b"},
                          {"status": "pending", "active": "c"}])
        self.assertEqual(s, {"done": 1, "total": 3, "current": "Doing b"})

    def test_no_running_item_means_no_current(self):
        self.assertIsNone(todo_summary([{"status": "pending", "active": "a"}])["current"])

    def test_no_list_is_none(self):
        self.assertIsNone(todo_summary(None))


if __name__ == "__main__":
    unittest.main()
