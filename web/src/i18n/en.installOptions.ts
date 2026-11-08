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
  runAccount: "Start remote runs as a second unprivileged account (Linux and macOS)", runAccountName: "Name of that account", runAccountDefault: "the default name", runAccountInvalid: "Not a name the agent accepts: letters, digits, _ and -, starting with a letter or _, at most 32, and not a system name such as root.",
  admin: "Administrator rights",
  adminUser: "It never asks for administrator rights; what it runs has only your user's rights.",
  adminService: "The service never runs as an administrator: it runs under its own unprivileged account, and the install refuses root, SYSTEM and LocalSystem. Only installing it needs an administrator.",
  exec: "Remote runs on this machine", execs: { off: "Off (default)", argv: "Approved programs only (argv)", shell: "Approved shell commands too" },
  runUser: "Type this in a terminal on the machine (one line after the other):", runService: "Type this in a terminal as an administrator. On Linux and macOS sudo needs the agent's full path (the installer puts it in ~/.aipod/bin); on Windows, in an elevated PowerShell, start it with aipod install --service and the same options.",
};
