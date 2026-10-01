"""App mode, the CLI's ETA/cost flags and the new server routes (scripts/board/board_app.py,
board_open.py, board.py, board_server.py; docs/live-board.md §2c-§2e)."""
import io
import json
import os
import struct
import subprocess
import sys
import tempfile
import threading
import unittest
import urllib.error
import urllib.request
import zlib
from contextlib import redirect_stderr, redirect_stdout
from http.server import ThreadingHTTPServer
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "board"))

import board  # noqa: E402
import board_app  # noqa: E402
import board_open  # noqa: E402
import board_server  # noqa: E402
from board_config import APP_ICON_SIZES, APP_THEME_COLOR, ETA_MAX_MIN  # noqa: E402
from board_ensure import Found  # noqa: E402
from board_registry import register  # noqa: E402
from board_store import append_event, fold, read_events  # noqa: E402


def pixels(png: bytes):
    """(size, rows of RGB tuples) from the unfiltered 8-bit RGB PNG board_app draws."""
    assert png[:8] == b"\x89PNG\r\n\x1a\n"
    width, height = struct.unpack(">II", png[16:24])
    idat_len = struct.unpack(">I", png[33:37])[0]
    raw = zlib.decompress(png[41:41 + idat_len])
    stride = 1 + width * 3
    rows = [[tuple(raw[y * stride + 1 + x * 3: y * stride + 4 + x * 3]) for x in range(width)]
            for y in range(height)]
    return width, rows


class AppTests(unittest.TestCase):
    def test_the_manifest_makes_the_board_installable(self):
        m = board_app.manifest()
        self.assertEqual((m["display"], m["start_url"], m["theme_color"]), ("standalone", "/", APP_THEME_COLOR))
        self.assertEqual([i["sizes"] for i in m["icons"]], [f"{n}x{n}" for n in APP_ICON_SIZES] + ["any"])

    def test_the_png_icon_is_the_theme_colour_with_white_columns(self):
        theme = tuple(int(APP_THEME_COLOR[i:i + 2], 16) for i in (1, 3, 5))
        for size in APP_ICON_SIZES:
            width, rows = pixels(board_app.icon_png(size))
            self.assertEqual(width, size)
            self.assertEqual(len(rows), size)
            self.assertEqual(rows[0][0], theme)                       # margin
            mid = size // 2
            self.assertEqual(rows[mid][mid], (255, 255, 255))         # middle column
            self.assertEqual(rows[mid][int(size * 0.3)], (255, 255, 255))  # first column
            self.assertEqual(rows[mid][int(size * 0.39)], theme)      # the gap between columns
            self.assertEqual(rows[int(size * 0.1)][mid], theme)       # above the columns
        with self.assertRaises(ValueError):
            board_app.icon_png(100)

    def test_the_rgba_icon_has_an_alpha_channel_and_the_same_picture(self):
        def pixels(png):
            width = struct.unpack(">I", png[16:20])[0]
            color_type = png[25]
            idat = png[png.index(b"IDAT") + 4:png.index(b"IEND") - 8]
            raw = zlib.decompress(idat)
            step = 4 if color_type == 6 else 3
            return color_type, [raw[1 + y * (1 + width * step):(y + 1) * (1 + width * step)]
                                for y in range(width)], step
        rgb_type, rgb, _ = pixels(board_app.icon_png(192))
        rgba_type, rgba, step = pixels(board_app.icon_png(192, alpha=True))
        self.assertEqual((rgb_type, rgba_type, step), (2, 6, 4))
        self.assertEqual([bytes(r[i] for i in range(len(r)) if i % 4 != 3) for r in rgba], rgb)
        self.assertTrue(all(r[3::4] == b"\xff" * 192 for r in rgba))

    def test_svg_icon_and_icon_paths(self):
        svg = board_app.icon_svg().decode()
        self.assertIn(APP_THEME_COLOR, svg)
        self.assertEqual(svg.count("<rect"), 4)
        self.assertEqual(board_app.icon_size("/icon-192.png"), 192)
        self.assertIsNone(board_app.icon_size("/icon-100.png"))


class OpenTests(unittest.TestCase):
    def test_the_app_window_command_per_platform(self):
        mac = board_open.app_command("http://x", platform="darwin")
        self.assertEqual(mac, ["open", "-na", "Google Chrome", "--args", "--app=http://x"])
        which = {"chromium": "/usr/bin/chromium"}.get
        self.assertEqual(board_open.app_command("http://x", platform="linux", which=which),
                         ["/usr/bin/chromium", "--app=http://x"])
        self.assertIsNone(board_open.app_command("http://x", platform="linux", which=lambda _: None))

    def run_open(self, found=Found.BOARD, run=None, browser=None, cmd=("chrome",)):
        calls = {"browser": []}
        browser = browser or (lambda u: calls["browser"].append(u) or True)
        out, err = io.StringIO(), io.StringIO()
        with mock.patch.object(board_open, "app_command", return_value=list(cmd) if cmd else None), \
                redirect_stdout(out), redirect_stderr(err):
            code = board_open.open_board("/p", run=run or (lambda *a, **k: None), browser=browser,
                                         ensure_fn=lambda cwd: (f"Live board: up for {cwd}", None),
                                         probe_fn=lambda: found)
        return code, calls["browser"], out.getvalue(), err.getvalue()

    def test_a_server_that_does_not_answer_opens_nothing(self):
        code, browser, out, err = self.run_open(found=Found.NOTHING)
        self.assertEqual((code, browser), (1, []))
        self.assertIn("Live board: up for /p", out)
        self.assertIn("not answering", err)

    def test_chrome_opens_the_app_window_and_the_browser_stays_closed(self):
        ran = []
        ok = lambda cmd, **k: ran.append((cmd, k)) or subprocess.CompletedProcess(cmd, 0)
        code, browser, _, _ = self.run_open(run=ok)
        self.assertEqual((code, browser), (0, []))
        self.assertEqual(ran[0][0], ["chrome"])
        self.assertIn("timeout", ran[0][1])

    def test_without_chrome_the_default_browser_opens(self):
        fail = lambda cmd, **k: subprocess.CompletedProcess(cmd, 1)
        code, browser, _, _ = self.run_open(run=fail)
        self.assertEqual((code, len(browser)), (0, 1))

        def boom(cmd, **k):
            raise OSError("no open")
        code, browser, _, err = self.run_open(run=boom)
        self.assertEqual((code, len(browser)), (0, 1))
        self.assertIn("app window failed", err)
        code, browser, _, _ = self.run_open(cmd=None)
        self.assertEqual((code, len(browser)), (0, 1))
        self.assertEqual(self.run_open(cmd=None, browser=lambda u: False)[0], 1)


class CliTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        patcher = mock.patch.dict(os.environ, {"BOARD_DIR": self.tmp.name})
        patcher.start()
        self.addCleanup(patcher.stop)

    def test_eta_estimate_and_agent_link_are_recorded(self):
        board.run(["add", "T-1", "x"])
        board.run(["set", "T-1", "--eta", "45", "--est-cost", "3.5", "--agent", "a3f971dd8bd84040e"])
        st = fold(read_events(Path(self.tmp.name)))
        self.assertEqual((st["tasks"]["T-1"]["eta_min"], st["tasks"]["T-1"]["est_cost"]), (45, 3.5))
        self.assertEqual(st["agent_links"], {"a3f971dd8bd84040e": "T-1"})
        board.run(["set", "T-1", "--eta", str(ETA_MAX_MIN), "--est-cost", "0"])

    def test_bad_values_are_refused(self):
        bad = [["--eta", "0"], ["--eta", str(ETA_MAX_MIN + 1)], ["--eta", "1.5"], ["--eta", "x"],
               ["--est-cost", "-1"], ["--est-cost", "nan"], ["--est-cost", "inf"], ["--est-cost", "abc"],
               ["--est-cost", "10001"], ["--agent", "a b"], ["--agent", "short"]]
        for args in bad:
            with self.assertRaises(SystemExit), redirect_stderr(io.StringIO()):
                board.run(["set", "T-1", *args])


class ServerRouteTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        base = Path(self.tmp.name)
        self.root = base / "alpha"
        self.bdir = self.root / ".claude" / "board"
        self.pid = register(self.root, self.bdir, base / "reg.json")["id"]
        skills = base / "skills" / "plain"
        skills.mkdir(parents=True)
        (skills / "SKILL.md").write_text("x")
        append_event(self.bdir, {"type": "turn_start", "session": "11111111-aaaa"})
        server = ThreadingHTTPServer(("127.0.0.1", 0), board_server.make_handler(
            base / "reg.json", skills_dir=base / "skills"))
        threading.Thread(target=server.serve_forever, daemon=True).start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        self.base = f"http://127.0.0.1:{server.server_address[1]}"

    def get(self, path):
        with urllib.request.urlopen(self.base + path) as res:
            return res.headers.get("Content-Type"), res.read()

    def post(self, body):
        req = urllib.request.Request(self.base + "/api/control", data=json.dumps(body).encode(),
                                     headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req) as res:
                return res.status, json.loads(res.read())
        except urllib.error.HTTPError as exc:
            return exc.code, json.loads(exc.read())

    def test_app_files_are_served(self):
        self.assertEqual(self.get("/manifest.webmanifest")[0], "application/manifest+json")
        self.assertEqual(self.get("/icon-512.png")[0], "image/png")
        self.assertEqual(self.get("/icon.svg")[0], "image/svg+xml")
        for js in ("/sw.js", "/board_ui_text.js", "/board_ui_sessions.js", "/board_ui_tasks.js", "/board_ui_lists.js"):
            self.assertTrue(self.get(js)[0].startswith("text/javascript"))
        self.assertEqual(json.loads(self.get("/api/skills")[1]), ["plain"])
        with self.assertRaises(urllib.error.HTTPError):
            self.get("/icon-100.png")

    def test_state_carries_costs_modes_limits_and_the_undelivered_queue(self):
        self.assertEqual(self.post({"action": "queue_task", "value": "11111111-aaaa", "text": "hi",
                                    "project": self.pid})[0], 200)
        st = json.loads(self.get(f"/api/state?p={self.pid}")[1])
        self.assertEqual(st["modes"], ["A", "B", "C", "D", "E"])
        self.assertIn("stop_wait_s", st)
        self.assertGreater(st["queue_text_max"], 0)
        self.assertEqual(st["costs"]["sessions"][0]["queued"], ["hi"])

    def test_refusals_come_back_as_400_with_the_reason(self):
        self.assertEqual(self.post({"action": "run_skill", "value": "nope", "session": "11111111-aaaa",
                                    "project": self.pid}), (400, {"error": "unknown skill"}))
        self.assertEqual(self.post({"action": "set_mode", "value": "Z", "project": self.pid}),
                         (400, {"error": "invalid value"}))
        self.assertEqual(self.post({"action": "set_mode", "value": "B", "project": self.pid})[0], 200)
        self.assertEqual((self.root / ".claude" / "mode").read_text(), "B\n")


if __name__ == "__main__":
    unittest.main()

class PrintUrlTests(unittest.TestCase):
    def run_url(self, found):
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            code = board_open.print_url(ensure_fn=lambda: (found, False, None))
        return code, out.getvalue(), err.getvalue()

    def test_a_current_server_prints_its_address_and_nothing_else(self):
        code, out, err = self.run_url(Found.BOARD)
        self.assertEqual((code, err), (0, ""))
        self.assertEqual(out, board_open.url() + "\n")

    def test_any_other_state_prints_no_address(self):
        for found in (Found.NOTHING, Found.OTHER, Found.STALE, Found.OLD_BOARD):
            code, out, err = self.run_url(found)
            self.assertEqual((code, out), (1, ""), found)
            self.assertIn(found, err)
