# Folder and application access on a machine (ADR-0020)

Moved out of [remote-work-setup.md](remote-work-setup.md) §10 to keep that guide under 300 lines.

## Folder and application access: the ceiling and the web policies (ADR-0020)

Every remote run is checked, after the exec level, against **access layers**, always on real paths (links, junctions
and Windows short names followed; a network or device path refused):

1. **The machine's ceiling**, set by its admin with a command, never by editing a file:

   ```
   aipod config access show
   aipod config access allow <folder>            # runs may use only the allowed folders (once one is listed)
   aipod config access deny <folder>             # no run may use this folder or anything under it
   aipod config access app <exe> [--sha256 <hex>] # runs may start only the listed programs; with a hash, that exact file
   aipod config access remove <path>
   aipod config access clear                     # back to what the run account may reach (the OS permissions)
   ```

   Each entry must exist and is stored as its real path; an allowed folder is never a system or home root. On a
   service the ceiling lives in the admin-owned exec policy file (step 4: `deniedRoots`, `executableHashes` beside
   `allowedRoots` and `allowedExecutables`), so these verbs run as the admin (`sudo aipod config access ...`, an
   elevated prompt on Windows); an interactive agent keeps it in its `agent.json`. A ceiling with any entry still
   refuses shell and script runs. It applies to the next run; a reinstall keeps it.
2. **The web policies of the repositories the run touches** (the project page, owners and admins), which only narrow
   the ceiling. The run names its repository with its `project` key; the API keeps it only when it is a repository the target
   machine reports (compared without case and after Unicode NFC, stored in the machine's own spelling; anything else is
   dropped, so the run carries no label). The repositories are that one and every git checkout around its real working
   directory and around each path a grant fills in, a submodule's enclosing repository included; every policy covering
   one of them applies. The API delivers every live policy and tombstone of the agent's workspace (the workspace the API
   names; on a machine with signed approvals the one in its `agent.json`, so after a move on the web every run gets the
   default until the machine's admin logs in again); the agent fetches them when the stamp in its settings changes, and again,
   with the settings, before a run that names a newer stamp (the stamp also changes with the enforcement switch). On a
   machine with signed approvals (`aipod config approvals required`, step 9) a policy applies only when a pinned owner
   key signed it; an unsigned or older version leaves the last signed one in place, and only a newer signed version of
   the same project (a signed tombstone included) replaces it. A delivery of exactly 200 policies (the cap) is taken as
   cut, and so is one that mixes workspaces: every run then gets the default until a complete one arrives.
3. **The default**, where no policy covers the run's repository but the workspace switch *enforce access policies* is
   on, or the machine is production or a database: the run's project folder only, argv runs only, no shell and no
   script. With the switch off and an ordinary machine, such a run is held to the ceiling only, as before. A machine
   whose role the agent cannot learn (an API older than this release sends none with a run) counts as production.

Two rules hold the run to where it really works:

- **The repository it names must be its working directory's.** A run whose `project` names none of the checkouts around
  its working directory is refused (`access_cwd_not_allowed`, by the policy layer) wherever a policy or the default
  applies; with the ceiling only it runs as before, still held to the policy of the checkout it works in.
- **An interpreter is code it is given.** An argv run of an interpreter (sh, bash, python, node, PowerShell and the
  like) needs shell from every policy and from the default, and from a ceiling with any entry unless the interpreter is
  on the ceiling's application list (`access_script_refused`); an empty ceiling treats it as before.

A refused run fails with a code that starts with `access_` (for example `access_cwd_not_allowed`,
`access_exe_hash`, `access_shell_refused`) and the layer that refused it; the web shows both. `aipod status` prints
one line about the ceiling and the policies; `aipod config access show` prints the ceiling and each policy's version,
state and whether it is signed. A machine reports its ceiling to the web (shown only to its owner and the workspace's
admins) and, of its project folders, only how many there are.

**Update the API before the agents.** An agent of this release on an older API gets no role with a run and treats every
run as on a production machine (the default wherever no policy applies). A run that names a repository is approved with
the repository in the signed payload (`ap-approval-v2`). A machine with signed approvals that runs an agent older than
this release cannot verify such a signature and refuses the run (`approval_invalid`); update its agent. The Windows parts (final paths, file identity, the hard-link count) compile but
have not yet run on a real Windows host.

Known limits of this release, stated plainly:

- **A run without a repository gets the default where the default applies.** Runs are asked for by Claude through
  `aipod_run` and `aipod_script`, whose `project` argument (a key from `aipod_machine`) is optional; the web starts only
  a project's test command (which always names its repository) and job schedules (argv jobs, which name none and never
  run on a production or database machine). A shell or script run that names no repository is therefore refused on a
  production or database machine, and wherever the switch is on (`access_shell_refused`, `access_script_refused`): no
  policy can lift it, because none covers it. Name the repository (`project`, one the target lists in `aipod_machine`)
  for a run that a policy should allow; the tools' description tells Claude so.
- **Older agents and path approval rules.** An agent older than this release reports no access scope with a
  permission request, so approval rules for path tools (Read, Edit, Write, MultiEdit, LS, NotebookEdit, and Glob or
  Grep with a path) stop matching for it and "remember" is not offered for them until its agent is updated; Bash
  rules are not affected.
- **A web page loaded before the deploy** signs approvals with payload v1. For a run that names a repository the API
  then answers 403 `approval_invalid` until the page is reloaded.
