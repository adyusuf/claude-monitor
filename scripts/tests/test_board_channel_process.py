"""The board channel server as the page and the OS see it (scripts/board/board_channel.py,
board_server.py, docs/live-board.md §5): the state the page is served and the real stdio
process, started by a parent whose command line carries (or lacks) the channel flag."""
import json
import os
import subprocess
import sys
import time
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "board"))

import board_channel_ack as ack  # noqa: E402
import board_channel_reg as reg  # noqa: E402
import board_server  # noqa: E402
from board_config import CHANNEL_CAPABILITY, CHANNEL_METHOD, CHANNEL_SERVER  # noqa: E402
from channel_case import FLAG, S1, S2, ServerCase, request  # noqa: E402

BOARD = Path(__file__).resolve().parent.parent / "board"


class StateTests(ServerCase):
    """What the page is told: which sessions a channel server can reach."""

    def served(self):
        entry = {"id": "0123456789", "root": self.tmp.name, "dir": str(self.bdir)}
        return board_server.project_state(entry)

    def test_only_a_session_with_a_live_channel_server_is_marked_reachable(self):
        for sid in (S1, S2):
            self.state(sid, "turn_stop")
        reg.register(self.bdir, S1, os.getpid())
        st = self.served()
        flags = {s["id"]: s["channel"] for s in st["costs"]["sessions"]}
        self.assertEqual(flags, {S1: True, S2: False})
        self.assertEqual(st["channel_server"], CHANNEL_SERVER)

    def test_a_task_the_channel_pushed_no_longer_counts_as_queued(self):
        self.state(S1, "turn_stop")
        v = self.queue(S1, "pushed one")
        self.queue(S1, "waiting one")
        ack.mark(self.bdir, S1, v, now=time.time())
        (sess,) = self.served()["costs"]["sessions"]
        self.assertEqual(sess["queued"], ["waiting one"])


class ProcessTests(ServerCase):
    """The real stdio process, started by a parent whose command line carries the flag — the
    only way main() can be told the channel is on (there is deliberately no override)."""

    SPAWN = ("import os,subprocess,sys;"
             "p=subprocess.Popen([sys.executable,sys.argv[1]],stdin=subprocess.PIPE,stdout=subprocess.PIPE,"
             "text=True,env=dict(os.environ));"
             "p.stdin.write(sys.argv[2]+'\\n');p.stdin.flush();"
             "print(p.stdout.readline().strip(),flush=True);"
             "print(p.stdout.readline().strip() if sys.argv[3]=='1' else '',flush=True);"
             "p.stdin.close();p.wait(10)")

    def run_process(self, flagged, expect_push=False, session=S1):
        env = {**os.environ, "BOARD_DIR": str(self.bdir), "CLAUDE_CODE_SESSION_ID": session}
        argv = [sys.executable, "-c", self.SPAWN, str(BOARD / "board_channel.py"),
                json.dumps(request("initialize", protocolVersion="2025-11-25")), "1" if expect_push else "0"]
        if flagged:
            argv += FLAG.split()
        proc = subprocess.run(argv, capture_output=True, text=True, timeout=30, env=env)
        return proc

    def test_started_with_the_flag_it_declares_the_channel_and_pushes(self):
        self.state(S1, "turn_stop")
        self.queue(S1, "from the board")
        proc = self.run_process(True, expect_push=True)
        first, second = (json.loads(x) for x in proc.stdout.splitlines()[:2])
        self.assertEqual(first["result"]["capabilities"]["experimental"], {CHANNEL_CAPABILITY: {}})
        self.assertEqual(second["method"], CHANNEL_METHOD)
        self.assertIn("from the board", second["params"]["content"])
        self.assertEqual(reg.reachable(self.bdir), set())  # gone once the process exited

    def test_started_without_the_flag_it_is_idle_and_says_why(self):
        self.state(S1, "turn_stop")
        self.queue(S1, "from the board")
        proc = self.run_process(False)
        first = json.loads(proc.stdout.splitlines()[0])
        self.assertNotIn("experimental", first["result"]["capabilities"])
        self.assertIn("idle", proc.stderr)
        self.assertEqual(ack.held_back(self.bdir, S1, now=time.time()), set())

    def test_without_a_session_id_it_is_idle_even_when_flagged(self):
        proc = self.run_process(True, session="")
        first = json.loads(proc.stdout.splitlines()[0])
        self.assertNotIn("experimental", first["result"]["capabilities"])


if __name__ == "__main__":
    unittest.main()
