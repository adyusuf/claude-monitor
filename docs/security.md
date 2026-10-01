# Security

The board is a single-user tool: a Python standard-library server on the loopback address, showing the user's own
Claude Code sessions. It has **no authentication and no accounts**. The controls below keep it reachable only by
that user's own machine and pages; they are reviewed against the OWASP Top 10 before each promotion to `prod`.

## Assumptions

- One person uses the machine. `127.0.0.1` is shared by every OS account, so on a multi-user machine another
  account can reach the board: do not run it there.
- `BOARD_ALLOW_REMOTE=1` removes the loopback guard and the Host check on purpose. With it the board is open to
  anyone who can reach the port.
- The data on the page is the user's own (task titles, notes, session metadata). The page never shows a secret.

## OWASP Top 10 mapping (reviewed 01/10/2026 at `dev` `0ed1de9`)

| # | Risk | Status here, and the control |
|---|---|---|
| A01 | Broken access control | **No authentication, by design.** Reachable only through loopback: the server refuses to bind a non-loopback address (`board_server.main`), refuses any request whose `Host` is not a loopback name with its own port (DNS rebinding, `board_http_guard.py`), and refuses a cross-origin `POST`. Every control value is checked against an allowlist pattern (task id, role, skill, mode). IDOR does not apply: a project id is a hash and every registered project is the same user's. Other OS users on the machine are out of scope (see Assumptions). |
| A02 | Cryptographic failures | No TLS (loopback `http`). No credential is read, stored or sent. Runtime files (`events.jsonl`, `control.json`, the registry) are created `0600`. |
| A03 | Injection | No shell: no `shell=True`, `os.system`, `eval`, `exec` or `pickle` anywhere in `scripts/board`; subprocesses (`git`, `ps`, `lsof`) get argument lists. The page puts values into HTML only through one escaping helper (`esc`), with tests that markup in a task title stays inert; CSP allows scripts from `'self'` only. |
| A04 | Insecure design | This document is the threat model: a local, single-user, unauthenticated tool, fail-closed on the bind address and the Host. No rate limiting (local). |
| A05 | Security misconfiguration | Security headers on every response (CSP, `nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy`, `Permissions-Policy`, CORP); `Server` names the app, not the Python version; no debug endpoint, no directory listing (static files come from a fixed allowlist, `../` returns 404); no default credentials. The CSP allows inline **styles** (one `<style>` block), not scripts. |
| A06 | Vulnerable and outdated components | No third-party runtime dependency: the Python standard library, and Node's built-in test runner for the page's tests. Nothing to lock or audit. CI runs gitleaks and CodeQL (Python, JavaScript) and ShellCheck on every push. |
| A07 | Identification and authentication failures | Not applicable: no accounts, passwords or sessions. |
| A08 | Software and data integrity failures | CI actions are pinned to a commit SHA and gitleaks is checksum-verified. The hooks run the code of the clone on the machine; its integrity is the user's checkout. `events.jsonl` is append-only; nothing is deserialised with an unsafe loader. |
| A09 | Logging and monitoring failures | The server logs 4xx and 5xx responses to `server.log`; control changes are numbered in `control.json`. There is no audit log and no alerting, and none is needed for a local tool. |
| A10 | Server-side request forgery | The server makes no outbound request. The one HTTP client call (`board_ensure.py`) asks the board's own loopback port for `/api/info`. |

## Reporting a problem

Open an issue that says only that you found one; the details are exchanged privately.
