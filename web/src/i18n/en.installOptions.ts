// The install choices on the download page; en.ts assigns it as `installOptions`.
export const enInstallOptions = {
  title: "Install choices", hint: "How the agent starts, which account it runs as and what it may do.",
  mode: "How it starts", modes: { plugin: "With Claude Code (plugin hooks)", standalone: "When you sign in to the computer", service: "As a service when the machine starts" },
  modeHint: {
    plugin: "Claude Code starts it with every session, in your own account. Connect the machine first (aipod login).",
    standalone: "A daemon without Claude Code, in your own account: it signs in first, then starts each time you sign in.",
    service: "For servers: a boot service under its own unprivileged account. Installing it needs an administrator.",
  },
  runsAs: "Runs as", ownAccount: "Your own user account, with your rights.",
  serviceAccount: "Its own service account (aipod on Linux, _aipod on macOS, NT SERVICE\\aipod on Windows), never root or SYSTEM.",
  runAccount: "Start remote runs as a second unprivileged account (Linux and macOS)", runAccountName: "Name of that account", runAccountDefault: "the default name",
  admin: "Administrator rights",
  adminUser: "It never asks for administrator rights; what it runs has only your user's rights.",
  adminService: "The service never runs as an administrator: the install refuses root, SYSTEM and LocalSystem.",
  allowRoot: "Allow the service account to be root or SYSTEM",
  allowRootOn: "As root the agent only reports metrics and sessions: it never runs a command, so remote runs are off.",
  exec: "Remote runs on this machine", execs: { off: "Off (default)", argv: "Approved programs only (argv)", shell: "Approved shell commands too" },
  runUser: "Type this in a terminal on the machine:", runService: "Type this in a terminal as an administrator (sudo, or an elevated PowerShell):",
};
