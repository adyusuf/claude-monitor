// Strings of the tabbed and tabular page layouts (members, workspace settings, sessions, machine, integrations); en.ts assigns it as `views`.
export const enViews = {
  members: {
    tabs: "Members sections", tabMembers: "Members", tabInvitations: "Invitations",
    colMember: "Member", colRole: "Role", colJoined: "Joined", colInvited: "Invited e-mail", colExpires: "Expires",
    inviteTitle: "Invite someone",
  },
  workspace: {
    tabs: "Workspace settings sections", colWhen: "When", colAction: "Action", colActor: "Done by", colTarget: "Target", colDetail: "Detail",
    actorUser: "user", actorAgent: "machine", showDetail: "Show",
  },
  sessions: {
    pick: "Select a machine to see its sessions.", activeNow: "active now", waiting: "{n} waiting", total: "{n} in total",
  },
  integrations: {
    colConnection: "Connection", colConnectedBy: "Connected by", colProjects: "Projects imported", colLastCheck: "Last check", colDetails: "Details",
    openSettings: "Settings", none: "No Jira site is connected yet.",
  },
  machine: {
    tabs: "Machine sections", overview: "Overview", control: "Control", work: "Work", monitoring: "Monitoring", access: "Access",
  },
  serverIntegrations: { tabs: "Server integrations" },
};
