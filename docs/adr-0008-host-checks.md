# ADR-0008: checks that need a real host

The tests of ADR-0008 run on macOS with fakes for the operating system's tools. These checks need a real machine (or two) and a connected workspace, so
nobody has run them yet; a person does, once per system, before the feature is relied on. Use a machine that is not a production machine, an account
that is not shared, and a workspace you can throw away. Write the result of each step into the table at the end (pass, fail, what you saw).
Host names stay out of this file and out of the repository; write them in your own notes.

## 0. Before you start

- One workspace with the remote-work switch on (Settings), one admin account with two-step sign-in on, two machines that belong to the same account.
- Each machine: `cm-agent install --standalone --server <url>` (or the plugin path of ADR-0007) and `cm-agent status` shows it connected.
- Undo for everything below: `cm-agent uninstall --standalone`, then delete the machine's page on the web.

## 1. macOS: the autostart as an application

1. `cm-agent install --standalone --server <url>`, sign in when asked.
2. Expect: `~/Library/LaunchAgents/com.claudemonitor.agent.user.plist`, an application bundle `ClaudeMonitorAgent.app` in the agent home, the daemon running from
   inside it (`launchctl print gui/$(id -u)/com.claudemonitor.agent.user`), no Dock icon and no window.
3. Log out and back in (not a restart of the program): the daemon is running again and the machine shows online on the web within a minute.
4. `cm-agent uninstall --standalone`: the plist, the bundle and the launchd job are gone; the agent's own data stays.

## 2. Linux: `systemd --user` and the process names

1. Same install. Expect `~/.config/systemd/user/claude-monitor-agent.service`; `systemctl --user status claude-monitor-agent.service` is active.
2. Note whether it survives a logout (it needs the user's lingering; the result decides whether `SETUP.md` must say so).
3. Start a small listener as the same user (for example `python3 -m http.server 8099`), enable "share listening services" on the machine page (owner, with a code),
   and check that its page shows the port with the process name. Then start one as another account: it must show the port without a name.
4. Uninstall removes the unit.

## 3. Windows: the logon task, the windowless program, asks

1. `cm-agent install --standalone --server <url>` in the user's own account (not an administrator shell).
2. Expect a logon task `ClaudeMonitorAgent` (`schtasks /Query /TN ClaudeMonitorAgent`), a second program `cm-agentw.exe` next to `cm-agent.exe`, and no console window
   when you sign out and in. The daemon runs from `cm-agentw.exe`; its output goes to the log.
3. Interpreters: the machine page lists PowerShell and Python as trusted only when they are the real system ones. Run a PowerShell script (`monitor_run_script`)
   under the default execution policy and under a restricted one; record what happens (the script runs from a scratch `-File`).
4. Asks: from a Claude session on another machine ask this one a question (section 5). Delivery to a session goes through its named pipe. A session started with
   `permission_mode` set to bypass must not receive it (the gate on this system); a normal one must.
5. Uninstall removes the task and `cm-agentw.exe`.

## 4. Roles, presence and the two-step code rule

1. Set the machine's role to production on its page (admin). The page shows the role badge, "Claude: running/idle/none" and the tool inventory within a minute.
2. A shell command (`monitor_run` in shell mode from a Claude session; the command line has no shell option) on this machine waits on the web. Allow needs a code. With an owner account
   that has no two-step sign-in, Allow is refused ("you need two-step sign-in"), and the run expires.
3. The same for an argv run of an interpreter (`cm-agent run --machine <host> --argv /bin/sh -c true`) and for approving a grant or a job. A plain argv run of another program needs only the click.
4. Change the role to test: the shell command needs no code. Change it to production after approving without a code and before the agent starts it: the run is cancelled.

## 5. A real ask, end to end

1. Machine B: the owner turns on "accept asks" (needs a code). A Claude Code session is open on B and idle.
2. On machine A, from a Claude session: `monitor_ask` with the host of B and a short question (or `cm-agent ask --machine <host> "<question>"`).
3. Expect: the session on B receives it as an untrusted, labelled message, answers with `monitor_answer`, and `monitor_ask_result` (or `cm-agent result <id> --wait 60`)
   on A returns the answer wrapped as coming from another Claude. With no idle session on B the result says so (`no_idle_session`), after the 2-minute wait for confirmation.
4. Ask from a machine of another user: refused. An ask with a token-shaped string in it: arrives masked.

## 5a. ADR-0010: files, schedules, signed approvals, a run account

1. Files and stdin: send a script with a helper file and a stdin text (`cm-agent run --machine <host> --script main.py --interpreter python --file helper.py=./helper.py --stdin ./in.txt`); the card shows every box and Allow waits until each was read; the output shows the script read both.
2. Schedules: an admin turns on "Allow schedules"; an owner starts a 15-minute schedule on an approved job on a test machine; a run appears every tick, none while the last is open; make the job fail three times and see it pause; make the machine production and see it stop.
3. Signed approvals: make a key on the account page, `cm-agent approvers add <key>` on a machine, compare the fingerprints, `cm-agent config approvals required`; allow a run on the web (it runs); stop the web server from signing (or use a browser profile without the key) and see the machine refuse; an approval by a job or grant there waits for a signature.
4. The run account (Linux and macOS service only): `sudo cm-agent install --service --server <url> --exec argv --run-account`, then run `id` and `ls -la <home>` as a remote argv/shell run: it reports the run account and cannot read the service home. **Kill a long run (cancel on the web) and look at `ps`: a process that ignores TERM may remain (ADR-0010 §5, kill reach).** Check `cat /etc/sudoers.d/cm-agent-run` and that `uninstall --service` removes it.

## 6. Results

| Step | Pass / fail | What you saw |
|---|---|---|
| 1 macOS |  |  |
| 2 Linux |  |  |
| 3 Windows |  |  |
| 4 Roles and the code rule |  |  |
| 5 A real ask |  |  |
