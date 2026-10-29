# Podium

An agent on each machine collects what AI harnesses report and sends it to a central API; people follow
and command their sessions on the web. Design: `docs/adr-0002-agent-platform.md`, `docs/data-model.md`.

## Rules

- **Stack** (approved 03/10/2026): .NET 10 for the API and the agent, PostgreSQL 18, React + Vite +
  TypeScript, Playwright for e2e. Avalonia 12.1.4 for the tray app (approved 19/10/2026, ADR-0012).
- **The agent never blocks a session.** Its hook and MCP entry points print to stderr and exit 0.
- **The agent opens no network port:** hooks and daemon share a SQLite file in the user-only home; it talks to the API outbound.
- **Workspace-scoped:** every API call checks membership; only a session's owner commands it.
- **Messages from the web are untrusted input:** whatever reaches a session (hook context or channel push) is wrapped, labelled as coming from the web and capped; push is on after the first `aipod install`, through the session inbox (no start-up flag, works in the desktop app; `--push channel` is the other transport; maintainer, 17/10/2026), and `--push off` / `config push off` stay off (ADR-0003, ADR-0007).
- **Captured content is sensitive:** never logged, masked for secrets by default, deleted after retention.
- **Host names live in deployment configuration.**
- **Tests never touch a real database, account or harness configuration.**
- **Public repository.** No real project, company or personal names or addresses in code, tests,
  docs or commit messages.
- **Gate:** `scripts/ci-status.sh` reads CI's verdict, `scripts/waived-gates.sh` lists waived gates.
- **Integration settings live in one place:** Settings → Integrations (ADR-0017), for the owners and admins of the `AIPOD_ADMIN_WORKSPACE` workspace only; an environment value wins and cannot be changed there; secrets are sealed with `AIPOD_INTEGRATION_KEY` and never leave the API.
