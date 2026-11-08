# ADR-0020: Install choices, and folder and application access on a machine

**Status:** Part 1 built on `feat/install-options` (08/11/2026). Part 2 is **proposed**: every decision in §4 waits for the maintainer.
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

## 4. Part 2: proposal (not built; decisions for the maintainer)

| # | Proposal | Question for the maintainer |
|---|---|---|
| 1 | **A local access policy file** (`access.json`), owned by the admin and kept beside the exec policy. The executor reads it before each run. The API never writes it, so a compromised server cannot widen it. | Is a local, admin-owned file the right owner, rather than a setting on the web? |
| 2 | **Folders:** an `allow` list and a `deny` list of absolute folders. A run's working directory, and every path argument that a grant's typed placeholder fills, must be under an allowed folder and under no denied one, after real paths are resolved. Default: the run account's work folder only. | Should a session's own tools (Edit, Read) also be held to it, through a Claude Code `PreToolUse` hook that refuses a path outside the lists? |
| 3 | **Applications:** an `allow` list of absolute executables, each with an optional SHA-256. An argv run, a grant and a script's interpreter must be on it. Shell mode stays as it is, because a shell can start anything. A machine that wants the limit uses `--exec argv`. | Is "shell means no application limit" acceptable, or should the policy turn shell off by itself? |
| 4 | **The web shows the policy** each machine reports, read-only, so the owner knows what a run may reach before approving it. | Should an admin be able to propose a policy on the web, for the machine's admin to accept locally (`aipod config access accept`)? |
| 5 | **Strict machines** (production, database): the policy is required, and without one remote runs are refused. | Required, or only recommended? |

## 5. Not covered

- OS-level enforcement such as AppArmor, SELinux, macOS sandbox profiles or Windows AppLocker. The proposal checks paths in the agent, and the OS account stays the hard limit.
- Network access control.
