import type { Dictionary } from "./en";

// Das Dashboard des Arbeitsbereichs; de.ts weist es als `dashboard` zu.
export const deDashboard: Dictionary["dashboard"] = {
  title: "Übersicht", intro: "Die Maschinen dieses Arbeitsbereichs und was in den letzten {days} Tagen auf ihnen geschah.",
  machines: "Maschinen", machinesOnline: "{on} von {n} online", noMachines: "Diesem Arbeitsbereich ist noch keine Maschine beigetreten.",
  alerts: "Offene Alarme", projects: "Projekte", projectsActive: "{n} aktiv", projectsArchived: "{n} archiviert",
  sprints: "Sprints", sprintsActive: "Aktiv", sprintsPassive: "Passiv", sprintsPassiveHint: "geplant oder abgeschlossen",
  cpu: "CPU", memory: "Speicher", disk: "Festplatte", notMeasured: "nicht gemessen", software: "Installierte Software", noSoftware: "Nichts gemeldet",
  runs: "Ausführungen nach Status", noRuns: "Keine Ausführung in diesem Zeitraum.", approvals: "Berechtigungsanfragen", noApprovals: "Keine Berechtigungsanfrage in diesem Zeitraum.",
  approval: { allow: "Von einer Person erlaubt", auto_allowed: "Von einer stehenden Freigabe erlaubt", deny: "Abgelehnt", expired: "Nicht rechtzeitig beantwortet", open: "Wartet" },
  approvedShare: "{pct} % erlaubt", count: "{n}",
};
