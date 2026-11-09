# ADR-0021: Turning remote runs on from the machine's page, signed by the owner

**Status:** Accepted by the maintainer on 09/11/2026 ("İmzalı olarak yap"). Extends ADR-0005 (the exec level), ADR-0010 (pinned owner keys) and ADR-0020.

## 1. Problem

Until now a machine's remote-run level (`off`, `argv`, `shell`) could only be changed on the machine itself (`aipod install --exec`). That kept a
compromised server from turning remote runs on, but it meant going to the machine for every change. The maintainer asked for the switch on the
machine's settings page on the web.

## 2. Decision

| # | Rule |
|---|---|
| 1 | The machine's owner changes the level on the machine page. Every change needs re-authentication; on a production or database machine a fresh two-step code (ADR-0008 §9.13). |
| 2 | **Raising** the level (`off` -> `argv` -> `shell`) is signed in the owner's browser with their approval key over `ap-exec-level-v1`, the machine's agent id, the level, a sequence number and a deadline of at most 10 minutes (`ExecLevelSigning.Payload`). The API checks the signature against the owner's registered key; the **machine checks it again against the owner keys pinned on it** (`aipod approvers add`). A machine with no pinned key never accepts a raise from the web (`no_pinned_key`): the one-time pin is the local step that makes the web switch possible. |
| 3 | **Lowering** needs no signature: it never widens what runs. A compromised server can therefore turn remote runs off, never on. |
| 4 | Each change has a sequence number; the API stores one change per machine with compare-and-set on it, and the machine applies only a sequence higher than the last it applied, and only before the deadline, so a recorded change cannot be replayed. |
| 5 | The API delivers the latest change in the agent's settings (`AgentSettings.ExecLevelRequest`); the agent reports what it did in its profile (`AgentProfile.ExecLevelChange`: `applied`, `signature_refused`, `no_pinned_key`, `expired`, `needs_admin`). |
| 6 | A service keeps its level in its admin-owned exec policy file, which the service account cannot write: a change from the web is reported as `needs_admin` and the admin runs `aipod install --service --exec <level>`. |
| 7 | The machine's access ceiling and the project policies (ADR-0020) still apply to every run whatever the level. |

## 3. Consequences

- The web switch is as strong as the owner's key: someone who holds the owner's browser and code can turn runs on, as they can already approve runs.
- A machine that never pinned a key behaves as before: only a local change raises its level.
