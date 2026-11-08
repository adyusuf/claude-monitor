// The workspace dashboard; en.ts assigns it as `dashboard`.
export const enDashboard = {
  title: "Dashboard", intro: "The machines of this workspace and what happened on them in the last {days} days.",
  machines: "Machines", machinesOnline: "{on} of {n} online", noMachines: "No machine has joined this workspace yet.",
  alerts: "Open alerts", projects: "Projects", projectsActive: "{n} active", projectsArchived: "{n} archived",
  sprints: "Sprints", sprintsActive: "Active", sprintsPassive: "Passive", sprintsPassiveHint: "planned or closed",
  cpu: "CPU", memory: "Memory", disk: "Disk", notMeasured: "not measured", software: "Installed software", noSoftware: "Nothing reported",
  runs: "Runs by status", noRuns: "No run in this period.", approvals: "Permission requests", noApprovals: "No permission request in this period.",
  approval: { allow: "Allowed by a person", auto_allowed: "Allowed by a standing approval", deny: "Denied", expired: "Not answered in time", open: "Waiting" },
  approvedShare: "{pct}% allowed", count: "{n}",
};
