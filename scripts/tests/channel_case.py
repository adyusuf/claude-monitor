"""Shared base for the board channel tests: a temporary board and small helpers."""
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "board"))

import board_channel as bc  # noqa: E402
from board_config import CHANNEL_SERVER, ControlAction  # noqa: E402
from board_store import append_event  # noqa: E402
from board_control import read_control, record_change  # noqa: E402

S1, S2 = "11111111-aaaa", "22222222-bbbb"
FLAG = f"--dangerously-load-development-channels server:{CHANNEL_SERVER}"


def request(method, mid=1, **params):
    return {"jsonrpc": "2.0", "id": mid, "method": method, "params": params}


class ServerCase(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.bdir = Path(self.tmp.name) / ".claude" / "board"
        self.now = 1000.0

    def channel(self, session=S1, enabled=True):
        return bc.Channel(self.bdir, session, enabled, clock=lambda: self.now)

    def state(self, session, kind):
        append_event(self.bdir, {"type": kind, "session": session})

    def queue(self, session, text="do the thing", action=ControlAction.QUEUE_TASK, **extra):
        change = {"action": action, "value": session, "session": session, "text": text, **extra}
        return record_change(self.bdir, read_control(self.bdir), change)["version"]
