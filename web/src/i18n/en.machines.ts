// The machines namespace of the English dictionary: the list, and the role, Claude presence and tool inventory of ADR-0008; en.ts assigns it as `machines`.
export const enMachines = {
  title: "Machines", empty: "No machine is connected yet.", connect: "Connect a machine", revoke: "Disconnect",
  revokeConfirm: "Disconnect this machine? Its agent stops reporting.", revoked: "Disconnected", lastSeen: "Last seen",
  version: "Agent", user: "User", moveTo: "Move to workspace", enrolled: "Connected",
  updateAvailable: "Update available: {version}",
  updateHint: "Run \"aipod update\" on the machine, or let it update itself (aipod config auto-update on, if the workspace allows it).",
  role: {
    title: "Role", environment: "Environment", function: "Function", notSet: "not set", unknown: "unknown",
    claimed: "The machine itself says: {role}.", claimTightens: "A claim only makes the role stricter; it never sets or loosens it.",
    adminOnly: "Only a workspace admin sets the role.", save: "Save role", saved: "Role saved.",
    lowerHint: "Moving a machine away from production or database needs a recent sign-in: enter a code from your authenticator app, or sign in again.",
    strict: "Strict target (production or database): the stricter approval rules of ADR-0008 apply here.",
    env: { dev: "development", test: "test", stage: "staging", preprod: "pre-production", production: "production", other: "other" },
    fn: { development: "developer machine", application: "application server", web: "web server", database: "database server", ci: "CI runner", storage: "storage", other: "other" },
  },
  claude: {
    title: "Claude Code", none: "not installed", unknown: "unknown (older agent)", idle: "{n} idle", sessions: "{live} session(s), {idle} idle",
    kind: { npm: "npm install", native: "native install", unsupported: "an install the agent does not update" },
  },
  tools: {
    title: "Tools", none: "No tool from the list was found.", unknown: "Not reported yet (older agent).",
    untrusted: "listed, not run by the agent: the file is not admin-owned", interpreters: "Script interpreters",
    hint: "The agent probes a fixed list by absolute path and reports what it finds; it runs --version only for trusted or self-owned binaries.",
  },
  tags: {
    title: "Tags", label: "Tags (key=value, separated by spaces)", none: "No tags.", save: "Save tags", saved: "Tags saved.",
    hint: "At most {max} pairs; keys and values use letters, digits, dot, underscore and dash (1 to 32 characters). Claude sees them as labels, never as instructions.",
  },
  note: {
    title: "Runbook note", label: "Note for the people and the Claude sessions that work on this machine", none: "No note.", save: "Save note", saved: "Note saved.",
    hint: "Plain text, at most {max} characters. Claude receives it only when it asks about this machine, marked as text written by an admin.",
  },
  services: {
    title: "Listening services", off: "This machine does not report its listening ports.", waiting: "Switched on; the agent has not reported yet.",
    hidden: "Switched on by the machine's owner. Only the owner and workspace admins see the list.", at: "Reported {at}",
    hint: "The TCP ports the machine listens on, with the process name where the system shows it. Off by default: only the machine's owner turns it on.",
    turnOn: "Report listening ports", turnOff: "Stop reporting", saved: "Saved.", ownerOnly: "Only the machine's owner decides this.",
    enableHint: "Turning this on needs a recent sign-in: enter a code from your authenticator app, or sign in again.",
  },
  actions: {
    title: "Agent actions", hint: "Ask this machine's agent to do one of a few fixed things. There is no command to type: each button is one action the agent already knows. The result is shown below.",
    ownerOnly: "Only the machine's owner can ask for these.", offline: "The machine is offline: an action waits for 10 minutes and is dropped if the agent has not taken it by then.",
    oldAgent: "This agent is older than {min} and ignores these requests. Update it on the machine with \"aipod update\"; after that the buttons work.",
    enableHint: "Restart and update need a recent sign-in: enter a code from your authenticator app, or sign in again.",
    strictHint: "This machine is production or a database: restart and update always need a fresh code from your authenticator app.",
    sent: "Asked: {action}. The agent answers below.", none: "No action asked yet.",
    kind: { status: "Status", update_check: "Check for update", update: "Update", restart: "Restart" },
    status: { queued: "waiting for the agent", delivered: "the agent has it", succeeded: "done", failed: "failed", expired: "not taken in time" },
  },
  asks: {
    title: "Asks from your other machines", hint: "A Claude session on another machine of yours can ask a question of an idle Claude session here (not a production or database machine, and only if an admin has set this machine's environment). The answer comes back as text. Off by default: only this machine's owner turns it on.",
    off: "This machine takes no asks.", on: "This machine takes asks from your other machines.", turnOn: "Allow asks", turnOff: "Stop allowing asks", saved: "Saved.",
    ownerOnly: "Only the machine's owner decides this.", enableHint: "Turning this on needs a recent sign-in: enter a code from your authenticator app, or sign in again.",
    none: "No ask yet.", question: "Question", answer: "Answer", answerCut: "The answer was cut at the server's limit.",
    status: { queued: "waiting", delivered: "delivered, waiting for the answer", answered: "answered", no_idle_session: "no idle session", not_accepting: "not accepting", no_answer: "no answer", expired: "expired", cancelled: "cancelled" },
  },
  projects: {
    title: "Projects", none: "No project is known on this machine yet.", noneHint: "A project appears when a session works in it, or when a folder is listed with aipod config projects add.",
    listed: "listed folder", session: "from a session", errors: "{n} tool errors in 7 days", noRuns: "No run for this project yet.", run: "run", cut: "output cut",
    passed: "{n} passed", failed: "{n} failed", skipped: "{n} skipped", kind: { test: "test", build: "build", deploy: "deploy", other: "other" },
  },
};
