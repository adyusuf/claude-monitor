# Security

The design is [ADR-0002](adr-0002-agent-platform.md). The OWASP Top 10 mapping below is written against the code and
reviewed before every promotion to `prod` (global #19); a row that names a gap is a known gap, not a pass.

## What is at stake

The agent forwards everything a harness exposes: prompts, tool inputs and outputs, file contents. The central
database therefore holds users' code and anything their tools printed. The web can also send a prompt, a stop or a
permission answer to a session running on someone's machine. Those two facts drive the controls below.

## Controls decided in ADR-0002

| Area | Control |
|---|---|
| Access | Every API call is scoped to a workspace and checked in the API, fail-closed. Only a session's owner may command it or answer its permission requests. |
| Accounts | Passwords hashed (ASP.NET Core Identity hasher); e-mail verification; GitHub/Google sign-in never silently joins an existing account; export and deletion of one's own data. |
| Web session | Opaque token in an `HttpOnly`, `SameSite=Lax` cookie (`Secure` on https, which is required outside development), stored hashed, revocable; an anti-forgery header on unsafe methods. |
| Agent | Device authorisation (RFC 8628); short access tokens and single-use rotating refresh tokens, stored hashed by the API and in the OS credential store on the machine; reuse of a replaced refresh token revokes the agent. |
| Local surface | The agent opens no network port: hooks and the daemon share a SQLite file in the user-only home directory. Commands arrive over the agent's own outbound connection. |
| Captured content | Secret patterns masked by the agent by default; never written to logs; size-capped per event; archived and removed from the database after the workspace's retention period. |
| Commands and permission answers | Owner only; expire; audited; report their outcome. |
| Audit | `audit_events` for sign-in and its failures, linking, invitations, roles, device approvals, revocations, settings, commands, permission answers and account deletion. |
| Transport | HTTPS only (Cloudflare to the server with an Origin certificate); the API is served under the web's own host (global #17). |

## OWASP Top 10 (2021) mapping — reviewed 03/10/2026 at `dev`

| # | Risk | Controls in the code | Known gaps |
|---|---|---|---|
| A01 | Broken access control | `Security/Access.cs`: membership checked per call, fail-closed (an unknown role ranks lowest); a workspace one cannot see answers 404. Owner-only commands and permission answers (`CommandEndpoints`), admin-only settings, audit and invitations, owners protected (`WorkspaceEndpoints`). Agents act only on their own sessions and commands (`AgentEndpoints`). CSRF: `X-CSRF` header and same-origin check on cookie calls (`Startup.Csrf`). Tests: `WorkspaceTests`, `CommandTests`, `DeviceTests`. | None known. |
| A02 | Cryptographic failures | TLS end to end; cookies `Secure` on https; every token (login, mail, agent, device, invitation) is 256 random bits stored only as SHA-256 (`Security/Secrets.cs`); passwords with ASP.NET Core Identity's PBKDF2 hasher; backups AES-256-CBC + HMAC-SHA256 (`deploy/windows/BackupCrypto.psm1`, tested). | Encryption at rest is the server's BitLocker (runbook), not the application's. |
| A03 | Injection | All SQL through EF Core parameters; of the three raw statements, two are interpolated (parameterised) and one creates a partition whose name is built from dates only (`Background/Housekeeper.cs`); search terms escaped for `LIKE` (`Text/SearchText.ContainsPattern`); the web renders text through React (no `dangerouslySetInnerHTML`); CSP `script-src 'self'` and `style-src 'self'`. The agent masks payloads as JSON values, never by editing JSON text. | None known. |
| A04 | Insecure design | Threat model above; commands and permission answers only by the machine's owner; the agent has no inbound port; device approval binds a machine to one workspace; refresh reuse revokes; export and deletion for the user. | Permission answers from the web are powerful by design (owner only, expiring, audited). |
| A05 | Security misconfiguration | Production refuses to start without its required settings or with a non-https origin (`ApiConfig.From`); security headers on every response (CSP, `nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy`, `Permissions-Policy`); no stack traces to clients (`UseExceptionHandler`); COEP/COOP/CORP; `/api` answers `no-store`; forwarded headers only from `MONITOR_PROXY_NETWORKS` (Cloudflare); OWASP ZAP baseline at the prod gate (`scripts/zap-baseline.sh`); the SPA never answers `/api/*`; secrets only in the server's environment file readable by the site's app pool. | ZAP has run against the local stack only (0 FAIL, 1 intended WARN); against the test environment it runs at the first prod gate. |
| A06 | Vulnerable and outdated components | The gate fails on high/critical advisories: `dotnet list package --vulnerable`, `npm audit` for the web; CodeQL (C#, TypeScript, Python) and ShellCheck; CI actions pinned to commit SHAs; gitleaks on every commit and in the gate. | Windows agent binaries are not code-signed yet. |
| A07 | Identification and authentication failures | Verified e-mail before sign-in; generic failure messages (no account enumeration on sign-up, sign-in or reset); password reset revokes all sessions; single-use, expiring mail tokens; rate limit on sign-in, sign-up, reset, provider sign-in, device codes, agent token refresh and account deletion (`RateLimitTests`); a 15-minute lock after 5 wrong passwords or codes per account; optional two-step sign-in with an authenticator app (`MfaTests`), for password and provider sign-in alike; server-side session revocation on sign-out. | Two-step sign-in (TOTP) is optional per user, not enforced per workspace. |
| A08 | Software and data integrity failures | macOS agent signed (Developer ID, hardened runtime) and notarised; release packages built from a clean commit and verified on the server by `/api/version` before traffic moves (`deploy.ps1` rolls back otherwise); additive-only migrations checked by `scripts/backcompat_scan.py`; idempotent agent batches. | Windows agent unsigned; no automatic update channel yet, so no update signing to review. |
| A09 | Security logging and monitoring failures | `audit_events` for every security-relevant action, with a hashed IP; sign-in failures audited; captured content and tokens never logged; backups and restore drills write to the Windows event log, and an hourly check mails an alarm on a failed or missing backup, a missed drill, an API that does not answer or a module error (`check-health.ps1`). | No central log collection beyond the Windows event log. |
| A10 | Server-side request forgery | The API calls out only to fixed endpoints: the OAuth providers (`ProviderEndpoints`) and the configured SMTP server. No user-supplied URL is ever fetched. | None known. |

## Triaged findings

| Tool | Finding | Why it stays |
|---|---|---|
| CodeQL `cs/sensitive-data-transmission` (4.3, below the blocking band) | `Mail/Mailer.cs`: the mail body carries a token | It is the verification, reset or invitation link: sending that token to the account's own mailbox is the design. The token is single-use, expires, and is stored only hashed. |
| ESLint `react-hooks/set-state-in-effect` (switched off in `web/eslint.config.js`) | pages load from the API in an effect | State is set only after the response arrives (an async callback), React's documented fetch pattern; the rule cannot see the `await`. |

## Reporting a problem

Open an issue that says only that you found one; the details are exchanged privately.
