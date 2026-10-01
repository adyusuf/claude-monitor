"""The board channel server (scripts/board/board_channel.py, docs/live-board.md §5):
the MCP handshake, what it will push (only a board-queued task for ITS session, only while that
session is idle, at most once per change) — the real process and the served state are in
test_board_channel_process.py."""
import io
import json
import sys
import threading
import time
import unittest
from contextlib import redirect_stderr
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "board"))

import board_channel as bc  # noqa: E402
import board_channel_ack as ack  # noqa: E402
import board_channel_reg as reg  # noqa: E402
from board_config import (CHANNEL_CAPABILITY, CHANNEL_CONFIRM_S, CHANNEL_METHOD, CHANNEL_SERVER,  # noqa: E402
                          QUEUE_TEXT_MAX)
from board_control import read_control, record_change  # noqa: E402
from channel_case import S1, S2, ServerCase, request  # noqa: E402


class HandshakeTests(ServerCase):
    def test_initialize_echoes_the_clients_protocol_and_declares_the_channel(self):
        out = self.channel().handle(request("initialize", protocolVersion="2025-11-25"))
        res = out["result"]
        self.assertEqual((out["id"], res["protocolVersion"]), (1, "2025-11-25"))
        self.assertEqual(res["capabilities"]["experimental"], {CHANNEL_CAPABILITY: {}})
        self.assertEqual(res["serverInfo"]["name"], CHANNEL_SERVER)
        self.assertIn(CHANNEL_SERVER, res["instructions"])

    def test_initialize_without_a_protocol_falls_back_and_a_disabled_server_declares_no_channel(self):
        res = self.channel(enabled=False).handle({"jsonrpc": "2.0", "id": 7, "method": "initialize"})["result"]
        self.assertEqual(res["protocolVersion"], bc.CHANNEL_FALLBACK_PROTOCOL)
        self.assertNotIn("experimental", res["capabilities"])
        res = self.channel().handle(request("initialize", protocolVersion=5))["result"]
        self.assertEqual(res["protocolVersion"], bc.CHANNEL_FALLBACK_PROTOCOL)

    def test_tools_ping_unknown_and_notifications(self):
        ch = self.channel()
        self.assertEqual(ch.handle(request("tools/list"))["result"], {"tools": []})
        self.assertEqual(ch.handle(request("ping"))["result"], {})
        self.assertEqual(ch.handle(request("resources/list", mid=9))["error"]["code"], bc.NOT_FOUND)
        self.assertIsNone(ch.handle({"jsonrpc": "2.0", "method": "notifications/initialized"}))

    def test_a_bad_session_id_disables_the_server(self):
        for bad in ("", None, "short", "../../etc"):
            self.assertFalse(self.channel(bad).enabled, bad)
        self.assertTrue(self.channel().enabled)


class MessageTests(ServerCase):
    def change(self, **over):
        return {"v": 4, "action": "queue_task", "session": S1, "text": "  write notes ", **over}

    def test_a_board_queued_task_becomes_a_user_request(self):
        content, meta = bc.message(self.change(), S1)
        self.assertEqual(content, bc.PREFIX + "write notes")
        self.assertEqual(meta, {"kind": "queue_task", "change": "4"})
        self.assertTrue(all(isinstance(v, str) for v in meta.values()))

    def test_a_skill_request_is_pushed_too(self):
        content, meta = bc.message(self.change(action="run_skill", text="invoke the /x skill"), S1)
        self.assertEqual((content, meta["kind"]), (bc.PREFIX + "invoke the /x skill", "run_skill"))

    def test_anything_else_is_refused(self):
        bad = [self.change(session=S2), self.change(action="remove_task"), self.change(action="decide"),
               self.change(text=""), self.change(text="   "), self.change(text=None), self.change(text=5),
               self.change(text="x" * (QUEUE_TEXT_MAX + 1)), self.change(text="bell\x07"),
               {"v": 1, "action": "queue_task", "text": "no session"}]
        for change in bad:
            self.assertIsNone(bc.message(change, S1), change)
        self.assertIsNotNone(bc.message(self.change(text="x" * QUEUE_TEXT_MAX), S1))
        self.assertIsNotNone(bc.message(self.change(text="two\nlines"), S1))


class DueTests(ServerCase):
    def test_an_idle_session_gets_its_queued_task_once(self):
        self.state(S1, "turn_stop")
        v = self.queue(S1)
        ch = self.channel()
        (version, note), = ch.due()
        self.assertEqual(version, v)
        self.assertEqual((note["method"], note["params"]["meta"]["change"]), (CHANNEL_METHOD, str(v)))
        self.assertEqual(ch.due(), [])  # marked: the next poll does not push it again
        self.assertEqual(ack.held_back(self.bdir, S1, now=self.now), {v})

    def test_a_busy_or_unknown_session_is_left_to_the_hooks(self):
        self.queue(S1)
        self.assertEqual(self.channel().due(), [])  # no turn event: unknown
        self.state(S1, "turn_start")
        self.assertEqual(self.channel().due(), [])
        self.assertEqual(ack.held_back(self.bdir, S1, now=self.now), set())

    def test_another_sessions_task_and_general_changes_are_never_pushed(self):
        self.state(S1, "turn_stop")
        self.queue(S2)
        record_change(self.bdir, read_control(self.bdir), {"action": "remove_task", "value": "T-1"})
        record_change(self.bdir, read_control(self.bdir), {"action": "queue_task", "value": S1, "session": S1,
                                                           "text": "a\x07b"})  # hand-edited control file
        self.assertEqual(self.channel().due(), [])

    def test_a_disabled_server_pushes_nothing_and_registers_nothing(self):
        self.state(S1, "turn_stop")
        self.queue(S1)
        ch = self.channel(enabled=False)
        self.assertEqual(ch.due(), [])
        ch.beat()
        self.assertEqual(reg.reachable(self.bdir, now=self.now), set())
        self.assertEqual(ack.held_back(self.bdir, S1, now=self.now), set())

    def test_several_tasks_go_out_in_queue_order(self):
        self.state(S1, "turn_stop")
        first, second = self.queue(S1, "one"), self.queue(S1, "two")
        got = self.channel().due()
        self.assertEqual([v for v, _ in got], [first, second])

    def test_beat_registers_at_most_once_per_interval(self):
        ch = self.channel()
        with mock.patch.object(bc, "register") as register:
            ch.beat()
            ch.beat()
            self.now += bc.CHANNEL_BEAT_S
            ch.beat()
        self.assertEqual(register.call_count, 2)

    def test_a_change_is_never_pushed_twice_even_after_the_grace_expired(self):
        self.state(S1, "turn_stop")
        self.queue(S1)
        ch = self.channel()
        self.assertEqual(len(ch.due()), 1)
        self.now += 10 * CHANNEL_CONFIRM_S
        self.assertEqual(ch.due(), [])

    def test_a_push_nothing_confirmed_is_handed_back_to_the_hooks(self):
        self.state(S1, "turn_stop")
        v = self.queue(S1)
        self.channel().due()
        self.assertEqual([c["v"] for c in ack.peek_changes(self.bdir, S1, now=self.now)], [])
        self.assertEqual([c["v"] for c in ack.peek_changes(self.bdir, S1, now=self.now + CHANNEL_CONFIRM_S)], [v])


class ServeTests(ServerCase):
    def run_serve(self, channel, lines, until):
        stop, done = threading.Event(), threading.Event()
        out = io.StringIO()

        def stdin():
            yield from lines
            done.wait(5)

        worker = threading.Thread(target=bc.serve, args=(channel, stdin(), out, stop))
        with mock.patch.object(bc, "CHANNEL_POLL_S", 0.02):
            worker.start()
            deadline = time.time() + 5
            while time.time() < deadline and not until(out.getvalue()):
                time.sleep(0.02)
            done.set()
            worker.join(5)
        return [json.loads(x) for x in out.getvalue().splitlines()]

    def test_it_answers_requests_pushes_a_queued_task_and_unregisters_on_exit(self):
        self.state(S1, "turn_stop")
        v = self.queue(S1, "ship the thing")
        ch = bc.Channel(self.bdir, S1, True)
        err = io.StringIO()
        with redirect_stderr(err):
            msgs = self.run_serve(ch, [json.dumps(request("initialize", protocolVersion="2025-11-25")) + "\n",
                                       "not json\n", "[1]\n"], lambda o: CHANNEL_METHOD in o)
        self.assertIn("not JSON", err.getvalue())
        self.assertEqual(msgs[0]["id"], 1)
        push = [m for m in msgs if m.get("method") == CHANNEL_METHOD][0]
        self.assertIn("ship the thing", push["params"]["content"])
        self.assertEqual(push["params"]["meta"]["change"], str(v))
        self.assertEqual(reg.reachable(self.bdir), set())  # unregistered after stdin closed

    def test_a_failed_write_hands_every_unsent_task_back_to_the_hooks(self):
        self.state(S1, "turn_stop")
        v = self.queue(S1)
        ch = bc.Channel(self.bdir, S1, True)

        class Broken(io.StringIO):
            def write(self, _):
                raise OSError("pipe closed")

        stop, done = threading.Event(), threading.Event()

        def lines():
            done.wait(5)
            yield from ()

        worker = threading.Thread(target=bc.serve, args=(ch, lines(), Broken(), stop))
        err = io.StringIO()
        with mock.patch.object(bc, "CHANNEL_POLL_S", 0.02), mock.patch.object(sys, "stderr", err):
            worker.start()
            deadline = time.time() + 5
            while time.time() < deadline and "pipe closed" not in err.getvalue():
                time.sleep(0.02)
            done.set()
            worker.join(5)
        self.assertIn("pipe closed", err.getvalue())
        self.assertEqual(ack.held_back(self.bdir, S1, now=time.time()), set())
        self.assertEqual([c["v"] for c in ack.peek_changes(self.bdir, S1)], [v])


if __name__ == "__main__":
    unittest.main()
