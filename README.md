# claude-monitor

Follow your AI coding sessions from the web. A small agent on each machine (macOS and Windows) collects
what [Claude Code](https://claude.com/claude-code) and other harnesses report and sends it to a central
API; you and your team follow the sessions live in the browser and can send them a prompt or stop them.

**Status: being rebuilt.** The design is decided ([ADR-0002](docs/adr-0002-agent-platform.md),
[data model](docs/data-model.md)); the code arrives in phases. The earlier local live board (a Python
server and a desktop window on one machine) is removed; its last release is the tag `archive/board-final`.

## Layout

| Path | What |
|---|---|
| `docs/adr-0002-agent-platform.md` | The architecture: agent, central API, web, phases |
| `docs/data-model.md` | The PostgreSQL data model |
| `docs/release.md`, `docs/security.md` | Promotion and rollback; security notes |
| `scripts/gate-core.sh`, `scripts/merge-gate.sh` | The merge gate: tests, line coverage ≥ 80% per codebase, secret scan, SAST |

## The gate

```bash
bash scripts/merge-gate.sh dev      # everything CI runs, locally
```

Setup: [`SETUP.md`](SETUP.md).

## License

[MIT](LICENSE)
