"""The "Ask status" button end to end (docs/live-board.md §2i), nothing mocked between the parts:
a real server, the page's own modules (run by Node), the real hook process and `board.py set`.

  session runs `board.py add/set` -> task is `waiting` -> the page renders the button for that session
  -> the click's message is POSTed -> the session's next tool call receives it -> the session answers
  with `board.py set` -> the board shows the real status and the button is gone.

The unit tests cover each half on its own; this one pins that the halves agree (the id the page sends,
the text the hook delivers, the status the board ends on).
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import unittest
import urllib.request
from http.server import ThreadingHTTPServer
from pathlib import Path

BOARD = Path(__file__).resolve().parent.parent / "board"
sys.path.insert(0, str(BOARD))

import board_server  # noqa: E402
from board_registry import register  # noqa: E402

SID = "5e5510a1-aaaa"
PAGE = r"""
const tasks = require(process.argv[1] + "/board_ui_tasks.js");
const text = require(process.argv[1] + "/board_ui_text.js");
const t = text.makeT("en");
const task = JSON.parse(require("fs").readFileSync(0, "utf8"));
const html = tasks.askHtml(task, t);
const m = /data-ask-task="([^"]+)" data-ask-session="([^"]+)"/.exec(html);
console.log(JSON.stringify(m ? { id: m[1], sid: m[2], text: text.fill(t("askStatusMsg"), { id: m[1] }) } : null));
"""


@unittest.skipUnless(shutil.which("node"), "Node is needed to run the page's own modules")
class AskStatusFlow(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        base = Path(tmp.name)
        self.root = base / "alpha"
        self.bdir = self.root / ".claude" / "board"
        self.pid = register(self.root, self.bdir, base / "reg.json")["id"]
        server = ThreadingHTTPServer(("127.0.0.1", 0), board_server.make_handler(base / "reg.json"))
        threading.Thread(target=server.serve_forever, daemon=True).start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        self.base = f"http://127.0.0.1:{server.server_address[1]}"
        self.env = {**os.environ, "BOARD_DIR": str(self.bdir)}

    def board(self, *args):
        done = subprocess.run([sys.executable, str(BOARD / "board.py"), *args], env=self.env,
                              capture_output=True, text=True)
        self.assertEqual(done.returncode, 0, done.stderr)
        return done.stdout.strip()

    def hook(self, **payload):
        done = subprocess.run([sys.executable, str(BOARD / "board_hook.py")], env=self.env, text=True,
                              input=json.dumps({"session_id": SID, **payload}), capture_output=True)
        self.assertEqual(done.returncode, 0, done.stderr)
        return json.loads(done.stdout) if done.stdout.strip() else None

    def state(self):
        with urllib.request.urlopen(f"{self.base}/api/state?p={self.pid}") as res:
            return json.loads(res.read())

    def post(self, body):
        req = urllib.request.Request(f"{self.base}/api/control", data=json.dumps({**body, "project": self.pid}).encode(),
                                     headers={"Content-Type": "application/json"})
        with urllib.request.urlopen(req) as res:
            return res.status

    def page_click(self, task):
        out = subprocess.run(["node", "-e", PAGE, str(BOARD)], input=json.dumps(task), capture_output=True,
                             text=True, check=True).stdout
        return json.loads(out)

    def test_the_question_reaches_the_session_and_its_answer_reaches_the_board(self):
        tid = self.board("add", "auto", "Write the release notes")
        self.board("set", tid, "--status", "waiting", "--note", "waiting for what?")
        # the session that ran `board.py set` is the one the page asks
        self.hook(hook_event_name="PostToolUse", tool_name="Bash",
                  tool_input={"command": f'python3 board.py set {tid} --status waiting'})
        task = self.state()["tasks"][tid]
        self.assertIn(SID, task["sessions"])

        click = self.page_click(task)
        self.assertEqual((click["id"], click["sid"]), (tid, SID))
        self.assertEqual(self.post({"action": "queue_task", "value": click["sid"], "text": click["text"]}), 200)

        # the asked session sees it on its next tool call; another session would not
        out = self.hook(hook_event_name="PostToolUse", tool_name="Bash", tool_input={"command": "ls"})
        context = out["hookSpecificOutput"]["additionalContext"]
        self.assertIn(f"board.py set {tid} --status", context)
        self.assertIsNone(self.hook(hook_event_name="PostToolUse", tool_name="Bash",
                                    tool_input={"command": "ls"}))  # delivered once

        # the session answers; the board changes only now
        self.assertEqual(self.state()["tasks"][tid]["status"], "waiting")
        self.board("set", tid, "--status", "done", "--note", "released")
        done = self.state()["tasks"][tid]
        self.assertEqual((done["status"], done["note"]), ("done", "released"))
        self.assertIsNone(self.page_click(done))   # a finished task has no button

    def test_a_task_no_session_touched_has_no_one_to_ask(self):
        tid = self.board("add", "auto", "Orphan")
        self.board("set", tid, "--status", "waiting")
        self.assertIsNone(self.page_click(self.state()["tasks"][tid]))


if __name__ == "__main__":
    unittest.main()
