# Security

The repository is being rebuilt ([ADR-0002](adr-0002-agent-platform.md)); no service runs from it yet. This file
holds the design's security controls now, and the OWASP Top 10 mapping is written against the code from phase 1
and reviewed before every promotion to `prod` (global #19).

## What is at stake

The agent forwards everything a harness exposes: prompts, tool inputs and outputs, file contents. The central
database therefore holds users' code and anything their tools printed. The web can also send a prompt to a
session running on someone's machine. Those two facts drive the controls below.

## Controls decided in ADR-0002

| Area | Control |
|---|---|
| Access | Every API call is scoped to a workspace and checked in the API, fail-closed. Only a session's owner may command it. |
| Accounts | Passwords hashed (ASP.NET Core Identity hasher); e-mail verification; GitHub/Google sign-in never silently joins an existing account. |
| Web session | Opaque token in an `HttpOnly`, `Secure`, `SameSite=Lax` cookie, stored hashed, revocable; an anti-forgery header on unsafe methods. |
| Agent | Device authorisation (RFC 8628); short access tokens and single-use rotating refresh tokens, stored hashed by the API and in the OS credential store on the machine; reuse of a replaced refresh token revokes the agent. |
| Local surface | The agent opens no network port: hooks and the daemon share a SQLite file in the user-only home directory. Commands arrive over the agent's own outbound connection. |
| Captured content | Secret patterns masked by the agent by default; never written to logs; deleted after the workspace's retention period; size-capped per event. |
| Commands | Expire, are audited, and report their outcome. Approving tool permissions from the web is out of v1. |
| Audit | `audit_events` for sign-in, linking, invitations, roles, device approvals, revocations, settings and every command. |
| Transport | HTTPS only; the API is served under the web's own host (global #17). |

## Triaged findings

| Tool | Finding | Why it stays |
|---|---|---|
| CodeQL `cs/sensitive-data-transmission` (4.3, below the blocking band) | `Mail/Mailer.cs`: the mail body carries a token | It is the verification, reset or invitation link: sending that token to the account's own mailbox is the design. The token is single-use, expires, and is stored only hashed. |

## Reporting a problem

Open an issue that says only that you found one; the details are exchanged privately.
