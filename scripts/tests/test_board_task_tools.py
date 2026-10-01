"""TaskCreate / TaskUpdate mirrored into the session's todo list (T-2). Shapes come from the CLI
binary: TaskCreate -> {task: {id, subject}} or "Task #<id> created successfully"; TaskUpdate input
{taskId, status, subject?, activeForm?}. Anything else records nothing."""
import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "board"))

import board_hook  # noqa: E402
import board_todos  # noqa: E402
from board_store import fold, read_events  # noqa: E402


def create(subject, resp, active=None, session="s1", **extra):
    tin = {"subject": subject, "description": "d"}
    if active:
        tin["activeForm"] = active
    return {"hook_event_name": "PostToolUse", "session_id": session, "tool_name": "TaskCreate",
            "tool_input": tin, "tool_response": resp, **extra}


def update(task_id, session="s1", **fields):
    return {"hook_event_name": "PostToolUse", "session_id": session, "tool_name": "TaskUpdate",
            "tool_input": {"taskId": task_id, **fields}, "tool_response": {"success": True}}


class TaskToolsTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        env = mock.patch.dict(os.environ, {"BOARD_DIR": self.tmp.name})
        env.start()
        self.addCleanup(env.stop)
        self.bdir = Path(self.tmp.name)

    def todos(self, sid="s1"):
        return fold(read_events(self.bdir))["sessions"][sid]["todos"]

    def test_create_adds_a_pending_item_from_the_dict_result(self):
        board_hook.handle(create("Write tests", {"task": {"id": "3", "subject": "Write tests"}}, "Writing tests"))
        self.assertEqual(self.todos(), [{"id": "3", "content": "Write tests", "status": "pending",
                                         "active": "Write tests"}])

    def test_create_reads_the_id_from_the_text_result_too(self):
        board_hook.handle(create("Ship", "Task #7 created successfully: Ship"))
        self.assertEqual(self.todos()[0]["id"], "7")

    def test_create_without_an_id_or_subject_records_nothing(self):
        board_hook.handle(create("x", {}))
        board_hook.handle(create("x", "something else"))
        board_hook.handle(create("", {"task": {"id": "1"}}))
        board_hook.handle(create("x", {"task": {"id": ""}}))
        self.assertEqual([e for e in read_events(self.bdir) if e["type"].startswith("todo")], [])

    def test_update_moves_status_and_uses_active_form_while_running(self):
        board_hook.handle(create("Fix", {"task": {"id": "1"}}, "Fixing"))
        board_hook.handle(update("1", status="in_progress", activeForm="Fixing the bug"))
        t = self.todos()[0]
        self.assertEqual((t["status"], t["active"]), ("in_progress", "Fixing the bug"))
        board_hook.handle(update("1", status="completed"))
        self.assertEqual(self.todos()[0]["status"], "completed")

    def test_update_renames_and_delete_removes(self):
        board_hook.handle(create("Old", {"task": {"id": "1"}}))
        board_hook.handle(create("Keep", {"task": {"id": "2"}}))
        board_hook.handle(update("1", subject="New"))
        self.assertEqual(self.todos()[0]["content"], "New")
        self.assertEqual(self.todos()[0]["active"], "New")
        board_hook.handle(update("1", status="deleted"))
        self.assertEqual([t["id"] for t in self.todos()], ["2"])

    def test_update_of_an_unknown_task_or_unknown_status_changes_nothing(self):
        board_hook.handle(create("A", {"task": {"id": "1"}}))
        board_hook.handle(update("99", status="completed"))
        board_hook.handle(update("1", status="blocked"))  # not a documented status: dropped, no fields left
        self.assertEqual(self.todos()[0]["status"], "pending")
        self.assertEqual(len(self.todos()), 1)

    def test_update_without_an_id_records_nothing(self):
        self.assertIsNone(board_todos.updated({"status": "completed"}))
        self.assertIsNone(board_todos.updated({"taskId": ""}))

    def test_a_subagents_task_call_is_not_the_sessions_list(self):
        board_hook.handle(create("A", {"task": {"id": "1"}}, agent_id="ag123456"))
        self.assertEqual(read_events(self.bdir), [])

    def test_recreating_an_id_replaces_it_and_a_todowrite_replaces_everything(self):
        board_hook.handle(create("A", {"task": {"id": "1"}}))
        board_hook.handle(create("A2", {"task": {"id": "1"}}))
        self.assertEqual([t["content"] for t in self.todos()], ["A2"])
        board_hook.handle({"hook_event_name": "PostToolUse", "session_id": "s1", "tool_name": "TodoWrite",
                           "tool_input": {"todos": [{"content": "Z", "status": "pending", "activeForm": "Zing"}]}})
        self.assertEqual([t["content"] for t in self.todos()], ["Z"])

    def test_lists_stay_capped(self):
        for i in range(60):
            board_hook.handle(create(f"t{i}", {"task": {"id": str(i)}}))
        self.assertLessEqual(len(self.todos()), 50)
        self.assertEqual(self.todos()[-1]["id"], "59")

    def test_lists_are_per_session(self):
        board_hook.handle(create("A", {"task": {"id": "1"}}, session="s1"))
        board_hook.handle(create("B", {"task": {"id": "1"}}, session="s2"))
        self.assertEqual((self.todos("s1")[0]["content"], self.todos("s2")[0]["content"]), ("A", "B"))


if __name__ == "__main__":
    unittest.main()
