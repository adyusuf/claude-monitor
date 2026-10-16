# Handover: ADR-0008 phases 0 and 1 (written 16/10/2026)

## Where things are

- Worktree: `.claude/worktrees/daemon-machine-awareness-6eb6c8` (inside the repository checkout)
- Branch: `feature/machine-awareness-splits`, cut from `origin/dev` (`4702c16`), **21 commits ahead**, working tree clean.
- Mode: C, defined models (`.claude/mode` = C). The plan: `docs/adr-0008-machine-awareness-script-runs-and-cross-machine-help.md` (§7 phases, §9 decisions).

## Done on the branch

1. ADR-0008 (three qa passes clean) as the first commit.
2. Phase 0: ten behaviour-free splits (agent, contracts, web i18n `remote`, `typesMachines.ts`, the machines test).
3. Phase 1: contracts (`MachineRoles.cs`, appended `AgentProfile`/`MachineView`), API (agent columns, CHECKs, migration
   `20261016080559_MachineRoles`, `MachineFacts`, admin-only `PUT /api/agents/{id}/role` with re-auth on lowering and a
   compare-and-set, the move clears the role), agent (`ToolInventory`, `PresenceProbe`, profile on change, `cm-agent config role`,
   `exec.json` claim, `MachineCache` + `MachineContext` block, `hook SessionStart --context`, third SessionStart entry in the plugin,
   `monitor_machines` line), web (badges, role editor, tools card, `machine` live event, en+tr), docs (`.env.example`, `SETUP.md`,
   `data-model.md` §3).
4. Tests: agent 1493 passed, API 868 passed, web 323 passed (97 % lines), plugin script tests 5 passed; tsc, eslint,
   `dotnet format --verify-no-changes` clean; `scripts/backcompat_scan.py --base origin/dev` additive.
5. First qa pass on the code: 14 findings, all fixed in `ed1e11f` (+ `07f11e7`).

## Not finished (pick up here)

1. **Second qa pass arrived after this file was first written (16/10/2026):** 13 of the 14 findings are closed; three things remain,
   and this was the last hand-back under the ceiling, so they are reported as open findings:
   - **R1 (rule #9):** `src/ClaudeMonitor.Agent/Exec/ToolInventory.cs` is 306 lines. Move `VersionIfRunnable`, `ShortVersion`, `Stamp`
     and `ReadVersion` into `ToolInventory.Version.cs` (the class is already `partial`).
   - **N2 (test must fail without the fix):** `tests/ClaudeMonitor.Api.Tests/MachineFactsViewTests.cs`,
     `Two_admins_cannot_both_win_a_race…` passes even if the compare-and-set conditions in `AgentRoleEndpoints.cs` are removed. Make
     the stale write happen (change the row through `api.Db()` between the read and the update, or extract the compare-and-set and call
     it with a stale "before") and assert 409 with the role unchanged; show the red run without the conditions.
   - **N1 and 14 (a decision):** after `ed1e11f` `SelfOwned` refuses any binary under a group- or other-writable folder, so on a standard
     Apple-silicon Homebrew (`/opt/homebrew/bin` is `rwxrwxr-x user admin`) every Homebrew tool's version is "unknown"; and `#!/usr/bin/env
     node` shims (Homebrew/nvm `npm`, npm-installed `claude`) resolve into `lib/node_modules/...`, where `node` is not beside them, so the
     PATH addition of finding 14 does not help. Either amend ADR §3a′ ("versions under group-writable folders and of script shims are
     unknown; the tool is still listed") or relax `SelfOwned` to the file's owner only and record the swap window as an accepted risk.
   After these: re-run the filtered agent/API/web sets, the full API suite, then the gate.
2. **Gate core passed as a dry run at `07f11e7`** (16/10/2026, every step green: format, lint, tsc, builds, tests, coverage ≥ 80 % per
   codebase, gitleaks, CVE audits, CodeQL, backward compatibility, document checks). After the three open findings above, run the promotion: `bash scripts/merge-gate.sh dev` (merges the branch into `dev` and pushes `dev`; the `prod`
   push stays the maintainer's). Docker must be up (API tests use Testcontainers); CodeQL takes a while.
3. **Not measured:** the ADR's phase 1 "done when" manual check (a role set on the web appears in `monitor_machines` and in a new
   session's start block within 5 minutes) needs a connected agent; this Mac's agent is not connected. Linux/Windows probe paths
   and the Windows named pipe never ran on a host.
4. **Known deviations from the ADR, left open:** the machines cache is refreshed only by its 5-minute loop (not after each profile
   send); the `SessionContext` 1-second timeout branch has no test; two test commits (`82f55eb`, `37851af`) sit before the product
   commits in history (harmless for the batch merge, do not rebase).
5. **Maintainer decisions still open (ADR §9):** 13 (TOTP also for shell/argv approvals on strict targets; asked, unanswered),
   12 (Windows logon task console window), 16 (parsed test summaries), 17 (request 4 wording), 18 (mobile app: ADR-0009).

## Next phases (each its own branch off `dev`, after this batch is on `dev`)

- 1b `feature/machine-facts`: tags, admin note, listening services (opt-in), `machine_projects` + run `project`/`kind`, `monitor_machine`.
- 2 `feature/script-runs` (after 1b): mode `script`, 32 768-char cap, the three level lines, TOTP on strict targets, output cap and
  completeness, scratch folder, `monitor_run_script`.
- 3 `feature/machine-asks` (after 2): same-user asks through the inbox push, no auth token on Unix, `permission_mode` gate on Windows.
- 4 `feature/standalone-agent` (after 1): `install --standalone`, user-level autostart, `Requests/` layer, CLI verbs.
- 5 docs; 6 `docs/adr-0009-mobile-app` (planning only).

## Cost of the old session (agents, measured tokens)

product-manager 89,723 · analyst 161,373 · architect 229,785 (two runs) · qa on the ADR 187,278 (three passes) · qa on the code
203,307 (pass 1; pass 2 pending) · test-writers 80,729 + 143,283 + 61,646. About 1.16 M subagent tokens, roughly $6–25 at the
calibration of `modes/role-selection.md`; the orchestrator's own context is the larger share.
