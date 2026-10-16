// The machines namespace of the English dictionary: the list, and the role, Claude presence and tool inventory of ADR-0008; en.ts assigns it as `machines`.
export const enMachines = {
  title: "Machines", empty: "No machine is connected yet.", connect: "Connect a machine", revoke: "Disconnect",
  revokeConfirm: "Disconnect this machine? Its agent stops reporting.", revoked: "Disconnected", lastSeen: "Last seen",
  version: "Agent", user: "User", moveTo: "Move to workspace", enrolled: "Connected",
  updateAvailable: "Update available: {version}",
  updateHint: "Run \"cm-agent update\" on the machine, or let it update itself (cm-agent config auto-update on, if the workspace allows it).",
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
};
