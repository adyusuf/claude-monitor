# ADR-0019: Approval rules, the approval band and the approvals page

**Status:** Asked for by the maintainer (08/11/2026); built on `feat/approval-rules`.
**Extends:** ADR-0002 (permission requests from a session, answered on the web), ADR-0008 (strict machines).

## 1. Problem

A Claude session asks for a tool permission. The agent sends the request to the API and the session owner answers allow or
deny on the session page. Each request is answered on its own. A session that runs the same command ten times asks ten
times, and the owner sees a request only on the page of that one session.

The maintainer asked for:
- a band at the top of every page with a short line about what is waiting, and a details dialog;
- approval for once, 5, 10 or 30 minutes, 1 hour, or always;
- an approvals page in the sidebar.

## 2. Decisions

| # | Decision |
|---|---|
| 1 | **An approval rule** (`approval_rules`) is the owner's standing "allow" for one machine (agent), one project (the session's repository, `repo_id`; a session with none matches only rules with none) and one tool. **Only an allowlist of tools can have one, each with its own scope**; every other tool (an MCP tool, `Task`, `WebFetch`, an unknown name) is answered once. The owner is the agent's user when the rule is made, and a rule matches only while the agent still belongs to that user. |
| 1a | **Scopes.** `Bash`: the exact command, and only for a call with no flag that changes how it runs (any key other than `command`, `description` and `timeout` with a value, such as `dangerouslyDisableSandbox` or `run_in_background`, makes it one-off). `Edit`, `Write`, `MultiEdit`, `NotebookEdit`: the exact file path, never one under `.claude`, `.git`, `.github`, `.husky`, `.vscode` or `.idea`, never one with `..`, and never a file that runs code later (shell and PowerShell scripts and profiles, `.env*`, `.mcp.json`, `settings*.json`, `package.json`, `Makefile`, `.npmrc`, `.envrc`). `Read`, `Glob`, `Grep`, `LS`, `WebSearch`: any input. |
| 1b | **Only plain text.** The agent masks secrets before it sends a request, so a masked text (`[masked:…]`) can stand for many commands. Such a request, and one whose command or path carries control characters (other than newline and tab), bidirectional overrides or zero-width characters, gets no rule. The dialog shows the full command with its newlines, and any other keys of a `Bash` call beside it. |
| 2 | **Durations:** `once` (no rule, as today), `5m`, `10m`, `30m`, `1h` and `always`. A time-limited rule ends at its `expires_at`. An `always` rule has no end and lasts until it is revoked. |
| 3 | **A rule is made only with an allow,** from the answer to a real open request (`POST /permission-requests/{id}/answer`, field `remember`). The rule's scope is copied from that request, never typed. |
| 4 | **Never on a strict machine.** A production or database machine (ADR-0008) gets no rule: the answer is refused with `rule_not_allowed`, and once allows nothing more. A rule made before the machine became strict stops matching. |
| 5 | **Never for a question or a plan.** `AskUserQuestion` needs the owner's answers and `ExitPlanMode` approves one plan, so neither is in the allowlist. A command or path longer than 2,000 characters cannot have a rule either. |
| 6 | **`always` needs re-authentication** (ADR-0005, the 5-minute code window of ADR-0008 §9.13a counts). A time-limited rule does not. |
| 7 | **Where a rule acts:** when the agent creates a request, the API looks for a live matching rule. If it finds one, it answers allow at once. The request is stored as answered by the rule's owner, with `answered_by_rule` set, and audited as `permission.auto_allowed`. The answer goes back in the create response. |
| 8 | **Only agents that read the answer in the response get one.** An older agent stores the request id only after the response, so an answer it hears on its stream before then would be lost. `PermissionRequestCreate` therefore gains `InlineAnswer` (default false); the API applies a rule only when it is true. An older agent keeps asking as today. |
| 9 | **Caps:** 200 live rules per user. Answering with a rule that already exists for the same scope replaces its end; it does not add a row. |
| 10 | **Revoking:** the owner revokes a rule on the approvals page (`DELETE /approval-rules/{id}`). Revoking is audited and takes effect on the next request. |
| 11 | **The band** sits at the top of every workspace page. It shows the requests waiting on the viewer's own sessions: the count, and for the oldest one the tool, a short preview, the session and the machine. **Details** opens a dialog that lists each request with its input, a duration choice and Allow / Deny. Runs waiting for the viewer's approval are counted in the band and link to the runs page; their approval keeps its own card (code, hash, signature), and grants already cover repeated runs. |
| 12 | **The approvals page** (`/w/:ws/approvals`, in the sidebar) has three parts: waiting requests (as in the dialog), the viewer's live rules with Revoke, and the recent answers on their sessions (who or which rule answered, and when). |
| 13 | **Read access:** `GET /workspaces/{id}/permission-requests` lists requests on sessions the caller owns, open first, capped at 50. `GET /workspaces/{id}/approval-rules` lists the caller's own live rules. Nobody sees another user's rules. |

## 3. Consequences

- One click can cover a repeated tool for a while. The command or path has to match exactly, so a `Bash` or file rule is narrow by design.
  A reading tool's rule covers every read in that project on that machine until it ends.
- A rule still trusts what the command does in that project: a rule for `npm test` runs whatever the project's test script says when it runs.
- An older agent never gets an automatic answer, which fails closed.
- Strict machines keep a per-request decision.

## 4. Not covered

- A command pattern (a prefix or a wildcard) for `Bash`. That needs its own threat review.
- A keyed hash of the unmasked input from the agent, which would let a command with a masked secret have a rule.
- An `always` rule keeps its command until it is revoked, which can be longer than the workspace's retention.
- Rules across machines or for the whole workspace.
- Approval durations for remote runs. Grants and jobs (ADR-0005) already cover repeated runs.
