# ADR-0020: Install choices, and folder and application access on a machine

**Status:** Part 1 built on `feat/install-options` (08/11/2026). Part 2 is **decided** (§4, 09/11/2026) and being built.
**Extends:** ADR-0005 (the executor and its account), ADR-0008 (machine roles), ADR-0010 (the run account), ADR-0019 (approval rules).

## 1. Problem

The maintainer asked for two things:
- when the agent is installed, a choice of which user it runs as and whether it has administrator rights;
- an idea for controlling which folders and which applications the agent, and what it runs, may reach.

## 2. Part 1: install choices (built)

The agent already has every choice:
- **How it starts:** `aipod install` (Claude Code plugin hooks), `--standalone` (at sign-in) or `--service` (at boot).
- **The account:** your own user for the first two. A service has its own account, and can add a second unprivileged account for runs (`--run-account [<name>]`).
- **Administrator rights:** never. A service always runs under its own unprivileged account (`aipod`, `_aipod`, `NT SERVICE\aipod`), and the install refuses root, SYSTEM and LocalSystem. `--allow-root` is read only for a privileged service account, which the CLI never picks, so the form does not offer it; it states the rule instead. Only installing a service needs an administrator.
- **Remote runs:** `--exec off|argv|shell`.

The web did not show these choices. The download page now has a form, "Install choices", that builds the commands for what was chosen. The level of remote runs is always written, `off` too, so a reinstall never keeps an earlier level. With Claude Code it gives two lines, because `aipod install --exec <level>` alone only sets the level and does not register the plugin. A service command uses the agent's full path under sudo, because sudo does not search the user's own folders. It adds no new flag and no new right. The agent checks every choice again when it installs: the run account's name, refusing a privileged account, and that only an administrator can install a service.

## 3. Part 2: what reaches folders and applications today

- **A session's own tools** (Read, Edit, Bash) run as the user who started Claude Code, within Claude Code's own permission rules and sandbox. Podium only sees their permission requests. The approval rules of ADR-0019 pin one exact value and never cover configuration or script files.
- **Remote runs** (ADR-0005) run in the service account, or in the run account (ADR-0010). The working directory is a literal path and the environment is built from empty. The only folder limit is the OS file permissions of that account.
- **Grants** pin `argv[0]` to an absolute path that is not an interpreter, so a grant names its application. Shell runs have no application limit.

## 4. Part 2: decision (maintainer, 09/11/2026: "flexible but secure; security comes first; no file to edit by hand")

The policy is a **setting on the web, per project**, never a file a person opens and edits. Five rules keep it safe:

| # | Decision |
|---|---|
| 1 | **Where it lives:** an *access policy* per managed project (the project page, ADR-0011), set by the workspace's owners and admins with re-authentication. It names the repositories it covers explicitly (repo keys such as `git:host/org/repo`, picked from the project's applications; one key belongs to at most one policy, so a contributor editing an application's URL cannot move a repository out of it). Per OS it lists allowed folders, denied folders and allowed applications (absolute executables, each with an optional SHA-256), whether the project's own folder on each machine is allowed, and whether shell runs are allowed. A folder that is not a git repository has no policy and always gets the default. The API delivers every policy of the workspace (tombstones included, at most 200; a full answer may be cut and the default then applies) to each of its machines, by a stamp in its settings and a separate fetch, so a machine that runs a repository it never reported still holds its policy. |
| 2 | **It only narrows.** The agent applies the intersection of the web policy and the machine's own ceiling. The ceiling is what the run account may reach (the OS permissions, as today), narrowed further only by the machine's admin through a command (`aipod config access ...`), never by editing a file. A missing, unreadable or invalid web policy means the default below, never "no limit". A compromised server can at worst take a policy back to the default; it can never widen past the ceiling. |
| 3 | **It is signed when the machine asks for signatures.** On a machine whose admin turned on signed approvals (ADR-0010, `aipod config approvals required`), a policy is applied only when one of the pinned owner keys signed it in the browser; the agent keeps the last signed policy, so an unsigned change cannot loosen it. Elsewhere it is applied as it comes (it can only narrow, rule 2). |
| 4 | **Enforcement and the default:** a workspace setting, *enforce access policies* (off by default, so nothing changes until an admin turns it on). Off: a run whose repository has a policy is held to it, any other run is as before. On, and always on a production or database machine (ADR-0008): a run whose repository has no policy, or that names no repository, gets the default: the project's own folder, argv runs only, no shell and no script. Leaving out the repository therefore never escapes a policy where enforcement is on. |
| 5 | **Shell and applications:** a shell or a script can start anything and `cd` anywhere, so it counts as shell: refused unless every layer allows shell, and a policy that allows it has no application or folder limit for those runs (the page says so). An argv run's executable and a grant's `argv[0]` must be on every non-empty application list, by real path and, where given, SHA-256. |
| 6 | **The repository is signed with the run.** A run's repository decides which policy applies, so on a machine with signed approvals the owner's signature covers it (approval payload v2, `ap-approval-v2`): the server cannot relabel a run after it was signed. |

What it checks, on the agent, before each remote run (ADR-0005): the working directory and every path argument filled by a grant's typed placeholder, after real paths are resolved, must be under an allowed folder and under no denied one; the executable must be allowed. A refusal is reported as the run's result with the rule that refused it, and audited.

Claude's own tools (Read, Edit, Bash in a session) stay under Claude Code's own permissions, because a hook that blocks Read while Bash can still `cat` the file would only look safe. What ties them to the policy: an approval rule (ADR-0019) for a file or path is made only for a path inside the project's allowed folders, and the session page shows the policy.

The web shows each machine's effective policy (web policy ∩ ceiling, the state of each policy on the machine and whether it is signed) read-only on the machine page; the ceiling's paths only to the machine's owner and admins. A machine admin changes the ceiling with `aipod config access show | allow <folder> | deny <folder> | app <exe> [--sha256 <hex>] | remove <path> | clear`, never by editing a file.

Decisions confirmed by the maintainer on 09/11/2026: the workspace switch (rule 4), the project page with an explicit repository list (rule 1), and signing the repository with the run (rule 6).
## 5. Not covered

- Blocking Claude's own tools in a session (rule above).
- OS-level enforcement such as AppArmor, SELinux, macOS sandbox profiles or Windows AppLocker. The proposal checks paths in the agent, and the OS account stays the hard limit.
- Network access control.
