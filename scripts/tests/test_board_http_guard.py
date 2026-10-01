"""Host-header guard against DNS rebinding (scripts/board/board_http_guard.py), alone and on the server."""
import http.client
import json
import sys
import tempfile
import threading
import unittest
from contextlib import redirect_stderr
from http.server import ThreadingHTTPServer
from io import StringIO
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "board"))
import board_server  # noqa: E402
from board_http_guard import SECURITY_HEADERS, SERVER_NAME, bind_allowed, host_header_ok  # noqa: E402


class HostHeaderTests(unittest.TestCase):
    def test_loopback_names_with_the_servers_port_are_accepted(self):
        for header in ("127.0.0.1:8765", "localhost:8765", "LOCALHOST:8765", "[::1]:8765", "127.0.0.2:8765"):
            self.assertTrue(host_header_ok(header, 8765), header)

    def test_any_other_name_is_refused_even_when_the_port_matches(self):
        for header in ("evil.example:8765", "localhost.evil.example:8765", "127.0.0.1.evil.example:8765",
                       "192.168.1.5:8765", "0.0.0.0:8765", "localhost.:8765"):
            self.assertFalse(host_header_ok(header, 8765), header)

    def test_a_wrong_missing_or_malformed_port_or_header_is_refused(self):
        for header in (None, "", "127.0.0.1", "127.0.0.1:9999", "127.0.0.1:", "127.0.0.1:87650", "[::1", "[::1]x",
                       "::1:8765", "[::1]:8765:1", "127.0.0.1:8765:1"):
            self.assertFalse(host_header_ok(header, 8765), repr(header))

    def test_no_port_in_the_header_means_port_80(self):
        self.assertTrue(host_header_ok("localhost", 80))
        self.assertFalse(host_header_ok("localhost", 8765))


class _LoopbackServer(unittest.TestCase):
    """A real board server on an ephemeral loopback port; tests talk to it with raw http.client."""

    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), board_server.make_handler(Path(tmp.name) / "projects.json"))
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        self.addCleanup(self.server.server_close)
        self.addCleanup(self.server.shutdown)
        self.port = self.server.server_address[1]

    def send(self, method, path, host, headers=None, body=None):
        conn = http.client.HTTPConnection("127.0.0.1", self.port, timeout=5)
        conn.putrequest(method, path, skip_host=True)
        if host is not None:
            conn.putheader("Host", host)
        for key, value in (headers or {}).items():
            conn.putheader(key, value)
        conn.putheader("Content-Length", str(len(body or b"")))
        conn.endheaders(body)
        res = conn.getresponse()
        data = res.read()
        conn.close()
        return res.status, data


class ServerRefusesForeignHosts(_LoopbackServer):
    def test_a_rebound_name_cannot_read_the_page_or_the_api(self):
        for path in ("/", "/board_ui.js", "/api/info", "/api/projects", "/api/state"):
            status, data = self.send("GET", path, f"evil.example:{self.port}")
            self.assertEqual(status, 403, path)
            self.assertEqual(json.loads(data), {"error": "host not allowed"})

    def test_a_rebound_name_cannot_post_even_with_a_matching_origin(self):
        body = json.dumps({"project": "0123456789", "action": "remove_task", "value": "T-1"}).encode()
        origin = f"http://evil.example:{self.port}"
        status, _ = self.send("POST", "/api/control", f"evil.example:{self.port}",
                              {"Origin": origin, "Content-Type": "application/json"}, body)
        self.assertEqual(status, 403)

    def test_a_request_without_a_host_header_is_refused(self):
        self.assertEqual(self.send("GET", "/api/info", None)[0], 403)

    def test_the_loopback_host_still_works(self):
        for host in (f"127.0.0.1:{self.port}", f"localhost:{self.port}"):
            self.assertEqual(self.send("GET", "/api/info", host)[0], 200, host)


class ResponseHeaders(_LoopbackServer):
    """The same server on its real loopback host; every kind of response carries the security headers."""

    def headers_of(self, method, path, **kw):
        conn = http.client.HTTPConnection("127.0.0.1", self.port, timeout=5)
        conn.request(method, path, **kw)
        res = conn.getresponse()
        res.read()
        headers = {k.lower(): v for k, v in res.getheaders()}
        conn.close()
        return res.status, headers

    def test_page_script_api_error_and_refusal_all_carry_the_security_headers(self):
        for path in ("/", "/board_ui.js", "/api/info", "/api/projects", "/nope"):
            status, headers = self.headers_of("GET", path)
            for name, value in SECURITY_HEADERS:
                self.assertEqual(headers.get(name.lower()), value, f"{path} ({status}) {name}")
        status, headers = self.headers_of("GET", "/api/info", headers={"Host": "evil.example"})
        self.assertEqual(status, 403)
        self.assertEqual(headers.get("x-frame-options"), "DENY")

    def test_the_csp_allows_no_inline_script_and_no_framing(self):
        csp = dict(SECURITY_HEADERS)["Content-Security-Policy"]
        self.assertIn("script-src 'self';", csp)
        self.assertNotIn("unsafe-eval", csp)
        self.assertNotRegex(csp, r"script-src[^;]*unsafe-inline")
        self.assertIn("frame-ancestors 'none'", csp)

    def test_the_server_header_names_the_app_and_not_the_python_version(self):
        _, headers = self.headers_of("GET", "/api/info")
        self.assertEqual(headers["server"], SERVER_NAME)
        self.assertNotIn("Python", headers["server"])


class BindTests(unittest.TestCase):
    def test_only_loopback_addresses_may_be_bound_by_default(self):
        for host in ("127.0.0.1", "localhost", "::1", "127.0.0.2"):
            self.assertTrue(bind_allowed(host, False), host)
        for host in ("0.0.0.0", "", "::", "192.168.1.5", "board.example"):
            self.assertFalse(bind_allowed(host, False), repr(host))

    def test_the_opt_in_allows_any_address_and_any_host_header(self):
        self.assertTrue(bind_allowed("0.0.0.0", True))
        self.assertTrue(host_header_ok("board.example:8765", 8765, allow_remote=True))
        self.assertFalse(host_header_ok("board.example:8765", 8765))

    def test_the_server_refuses_to_start_on_a_public_address_and_binds_nothing(self):
        err = StringIO()
        with mock.patch.object(board_server, "HOST", "0.0.0.0"), mock.patch.object(board_server, "ALLOW_REMOTE", False), \
                mock.patch.object(board_server, "ThreadingHTTPServer") as server, redirect_stderr(err):
            self.assertEqual(board_server.main([]), 2)
        server.assert_not_called()
        self.assertIn("BOARD_ALLOW_REMOTE=1", err.getvalue())

    def test_the_server_starts_on_a_public_address_only_with_the_opt_in(self):
        server = mock.MagicMock()
        server.serve_forever.side_effect = KeyboardInterrupt
        with mock.patch.object(board_server, "HOST", "0.0.0.0"), mock.patch.object(board_server, "ALLOW_REMOTE", True), \
                mock.patch.object(board_server, "ThreadingHTTPServer", return_value=server) as cls:
            self.assertEqual(board_server.main(["--port", "0"]), 0)
        self.assertEqual(cls.call_args[0][0], ("0.0.0.0", 0))

    def test_a_handler_built_with_the_opt_in_accepts_a_foreign_host(self):
        with tempfile.TemporaryDirectory() as tmp:
            srv = ThreadingHTTPServer(("127.0.0.1", 0), board_server.make_handler(Path(tmp) / "p.json", allow_remote=True))
            threading.Thread(target=srv.serve_forever, daemon=True).start()
            self.addCleanup(srv.server_close)
            self.addCleanup(srv.shutdown)
            conn = http.client.HTTPConnection("127.0.0.1", srv.server_address[1], timeout=5)
            conn.request("GET", "/api/info", headers={"Host": "board.example"})
            self.assertEqual(conn.getresponse().status, 200)
            conn.close()


if __name__ == "__main__":
    unittest.main()
