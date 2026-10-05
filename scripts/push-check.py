#!/usr/bin/env python3
"""End-to-end check of the push into an idle session (ADR-0003), with timestamps.

REAL: the API and PostgreSQL (start them with e2e/serve-local.sh), the agent daemon and its stream, `cm-agent hook`, `cm-agent mcp`.
STAND-IN: Claude Code. A scripted MCP client starts `cm-agent mcp` the way Claude Code does, sends initialize and only listens; a
transcript line is appended by hand where Claude Code would write one. Nothing here proves what Claude Code's own screen does.

  E2E_EMAIL_BASE=<account>@gmail.com CM_AGENT_BIN=<published cm-agent> python3 scripts/push-check.py

Needs: the local e2e stack up, a published agent (dotnet publish src/ClaudeMonitor.Agent -c Release -r <rid>), Python 3.
On macOS the ReadyToRun build may crash the daemon (see the follow-up task); set DOTNET_ReadyToRun=0 for this run if it does.
Everything lives in a throw-away agent home; the user's own agent, credentials and Claude Code configuration are not touched.
"""
import datetime, http.cookiejar, json, os, re, secrets, shutil, subprocess, sys, tempfile, threading, time, urllib.error, urllib.parse, urllib.request

PORT = os.environ.get("E2E_PORT", "5190")
ORIGIN = f"http://localhost:{PORT}"                       # the API's public origin in the local stack
BASE = f"http://127.0.0.1:{PORT}"
MAIL = os.environ.get("E2E_MAILPIT_URL", "http://127.0.0.1:8125")
EMAIL_BASE = os.environ.get("E2E_EMAIL_BASE", "")          # <account>@gmail.com -> <account>+<tag>@gmail.com (global #30)
AGENT_BIN = os.environ.get("CM_AGENT_BIN", "")
PASSWORD = secrets.token_urlsafe(24) + "-Aa1"             # random per run, for a throw-away local account; never stored
SESSION = "push-check-session"


def ts():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%H:%M:%S.%f")[:-3] + "Z"


def say(message):
    print(f"{ts()} [check] {message}", flush=True)


jar = http.cookiejar.CookieJar()
opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))


def call(method, path, body=None):
    request = urllib.request.Request(BASE + path, method=method, data=None if body is None else json.dumps(body).encode(),
                                     headers={"Content-Type": "application/json", "X-CSRF": "1", "Origin": ORIGIN})
    try:
        with opener.open(request, timeout=20) as response:
            raw = response.read()
            return response.status, (json.loads(raw) if raw else None)
    except urllib.error.HTTPError as error:
        return error.code, error.read()


def sign_up(email):
    call("POST", "/api/auth/register", {"email": email, "password": PASSWORD, "displayName": "Push Check"})
    token = None
    for _ in range(40):
        found = json.load(urllib.request.urlopen(f"{MAIL}/api/v1/search?query=" + urllib.parse.quote(f'to:"{email}"')))["messages"]
        if found:
            text = json.load(urllib.request.urlopen(f"{MAIL}/api/v1/message/{found[0]['ID']}"))["Text"]
            if match := re.search(r"token=(\S+)", text):
                token = match.group(1)
                break
        time.sleep(0.5)
    assert token, "no verification mail in Mailpit"
    call("POST", "/api/auth/verify-email", {"token": token})
    assert call("POST", "/api/auth/login", {"email": email, "password": PASSWORD})[0] == 204, "sign-in failed"
    return call("GET", "/api/me")[1]["workspaces"][0]["id"]


def main():
    if not EMAIL_BASE or "@" not in EMAIL_BASE or not os.path.isfile(AGENT_BIN):
        sys.exit("set E2E_EMAIL_BASE (<account>@gmail.com) and CM_AGENT_BIN (a published cm-agent); see the docstring")
    work = tempfile.mkdtemp(prefix="push-check-")
    home = os.path.join(work, "home")
    env = dict(os.environ, CM_AGENT_HOME=home, CM_CREDENTIALS="file", DOTNET_SYSTEM_NET_DISABLEIPV6="1", CLAUDE_CODE_SESSION_ID=SESSION)
    shim = os.path.join(work, "shim"); os.makedirs(shim)  # a `claude` that does nothing: `install` must not register anything in the real Claude Code
    open(os.path.join(shim, "claude"), "w").write("#!/bin/sh\nexit 0\n"); os.chmod(os.path.join(shim, "claude"), 0o755)
    agent = lambda *args, **kw: subprocess.run([AGENT_BIN, *args], text=True, capture_output=True, env=env, timeout=60, **kw)
    mcp = None
    try:
        local, domain = EMAIL_BASE.split("@", 1)
        workspace = sign_up(f"{local}+push{os.getpid()}@{domain}")
        login = subprocess.Popen([AGENT_BIN, "login", "--server", BASE], env=env, stdout=subprocess.PIPE, text=True)
        code = None
        for line in login.stdout:
            if match := re.search(r"[A-Z]{4}-[A-Z]{4}", line):
                code = match.group(0)
                break
        assert code and call("POST", "/api/device/approve", {"userCode": code, "workspaceId": workspace})[0] == 204, "device approval failed"
        login.wait(timeout=60)
        install = subprocess.run([AGENT_BIN, "install", "--push", "on"], text=True, capture_output=True, timeout=60,
                                 env=dict(env, PATH=shim + os.pathsep + env["PATH"]))
        assert "Push is ON" in install.stdout, install.stdout + install.stderr
        subprocess.run(["pkill", "-f", os.path.join(home, "bin")])  # the login started a daemon; the hooks below start the one under test
        time.sleep(1)
        installed = os.path.join(home, "bin", os.path.basename(AGENT_BIN))
        transcript = os.path.join(work, "transcript.jsonl"); open(transcript, "w").close()

        def hook(event, extra=None):
            payload = {"session_id": SESSION, "cwd": work, "transcript_path": transcript, "hook_event_name": event, **(extra or {})}
            assert subprocess.run([installed, "hook", event], input=json.dumps(payload), text=True, capture_output=True, env=env).returncode == 0

        hook("SessionStart"); hook("UserPromptSubmit", {"prompt": "first prompt typed at the terminal"}); hook("Stop")
        say("a first turn ran and finished: the session is IDLE (the hooks started the daemon)")
        session_id = None
        for _ in range(40):
            rows = (call("GET", f"/api/workspaces/{workspace}/sessions")[1] or {}).get("items", [])
            if rows:
                session_id = rows[0]["id"]
                break
            time.sleep(0.5)
        assert session_id, "the session never reached the API"

        mcp = subprocess.Popen([installed, "mcp"], stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, env=env)
        seen = []
        send = lambda o: (mcp.stdin.write(json.dumps(o) + "\n"), mcp.stdin.flush())

        def listen():
            for line in mcp.stdout:
                m = json.loads(line)
                if m.get("id") == 1:
                    say(f"initialize ok; server declares {list(m['result']['capabilities'].get('experimental', {}))}")
                    send({"jsonrpc": "2.0", "method": "notifications/initialized"})
                elif m.get("method") == "notifications/claude/channel":
                    say(f"CHANNEL NOTIFICATION {json.dumps(m['params']['meta'])} content={m['params']['content']!r}")
                    seen.append(m)
                elif m.get("id") == 9:
                    say("monitor_status -> " + m["result"]["content"][0]["text"])
        threading.Thread(target=listen, daemon=True).start()
        send({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2025-06-18", "capabilities": {},
                                                                              "clientInfo": {"name": "claude-code-standin", "version": "1"}}})
        time.sleep(5)
        send({"jsonrpc": "2.0", "id": 9, "method": "tools/call", "params": {"name": "monitor_status", "arguments": {}}})
        time.sleep(1.5)
        say("idle with the channel connected; NOW the web sends a message")
        started = time.time()
        status, _ = call("POST", f"/api/sessions/{session_id}/commands", {"kind": "prompt", "body": "Reply with exactly: ACK 42"})
        say(f"POST /api/sessions/{{id}}/commands -> {status}")
        assert status == 201
        while not seen and time.time() - started < 15:
            time.sleep(0.05)
        assert seen, "no channel notification within 15 s"
        arrived = time.time() - started
        message_id = seen[0]["params"]["meta"]["message_id"]
        say(f"the notification was written {arrived:.2f} s after the POST")
        open(transcript, "a").write(json.dumps({"type": "user", "message": {"content": seen[0]["params"]["content"]}}) + "\n")
        say("appended the line Claude Code would write to the transcript")
        for _ in range(60):
            row = call("GET", f"/api/sessions/{session_id}/commands")[1][0]
            if row["status"] == "applied":
                break
            time.sleep(0.25)
        say(f"API: id={row['id']} status={row['status']} created={row['createdAt']} delivered={row['deliveredAt']} applied={row['appliedAt']}")
        assert row["id"] == message_id and row["status"] == "applied" and len(seen) == 1, "not applied exactly once"
        say("PASS: delivered into an idle session, once, and applied only after it showed in the transcript")
    finally:
        if mcp:
            mcp.kill()
        subprocess.run(["pkill", "-f", os.path.join(home, "bin")])
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    main()
