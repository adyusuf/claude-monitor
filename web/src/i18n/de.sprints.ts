import type { Dictionary } from "./en";

// German sprints namespace (the sprint list and the sprint page): the same keys as en.sprints.ts; de.ts assigns it as `sprints`.
export const deSprints: Dictionary["sprints"] = {
  title: "Sprints", sprint: "Sprint {n}", sprintNamed: "Sprint {n} · {name}", backToSprints: "Sprints", new: "Neuer Sprint", create: "Sprint anlegen",
  startSprint: "Sprint starten", activeSprints: "Aktive Sprints",
  filter: "Welche Sprints", all: "Alle", forApplication: "Anwendung: {name}",
  status: { planned: "Geplant", active: "Aktiv", closed: "Abgeschlossen" },
  empty: "Es gibt noch keine Sprints.", emptyFiltered: "Kein Sprint hat diesen Status.",
  emptyHint: "Planen Sie den nächsten Arbeitsabschnitt: Legen Sie einen Sprint an und fügen Sie dann Vorgänge aus dem Backlog hinzu.",
  name: "Name", goal: "Ziel", startDate: "Startdatum", endDate: "Enddatum", capacity: "Kapazität", capacityIn: "Kapazität ({unit})",
  release: "Release", noRelease: "Kein Release", team: "Team", noTeam: "Niemand gehört zum Team.", releaseIs: "Release: {name}",
  dates: "{start} – {end}", datesLabel: "Zeitraum", issuesLabel: "Vorgänge", noDates: "Noch keine Daten", issueCount: "Vorgänge: {n}", goalLabel: "Ziel",
  barLabel: "Eingeplante Schätzungen im Verhältnis zur Kapazität", committedOf: "{committed} von {capacity} {unit} eingeplant",
  committed: "Eingeplant", done: "Erledigt", estimateIn: "{n} {unit}",
  start: "Starten", delete: "Löschen", deleteConfirm: "Sprint {n} löschen? Seine Vorgänge gehen zurück ins Backlog.",
  edit: "Bearbeiten", editTitle: "Sprint bearbeiten", close: "Sprint abschließen",
  closedBanner: "Dieser Sprint ist abgeschlossen: Er ist schreibgeschützt.",
  closeTitle: "Diesen Sprint abschließen", closeQuestion: "{n} Vorgänge sind nicht fertig. Wohin sollen sie?", closeNone: "Alle Vorgänge sind fertig.",
  moveTo: "Die unfertigen Vorgänge verschieben nach", toBacklog: "Zurück ins Backlog", confirmClose: "Sprint abschließen",
  boardTitle: "Vorgänge des Sprints", noIssues: "In diesem Sprint sind noch keine Vorgänge.", noneInColumn: "Keine.",
  statusOf: "Status von {mark}", remove: "Entfernen", removeFrom: "{mark} aus dem Sprint entfernen",
  addTitle: "Vorgänge hinzufügen", candidates: "Vorgänge im Backlog", noCandidates: "Das Backlog hat keine weiteren Vorgänge für diesen Sprint.",
  selectIssue: "{mark} auswählen", addSelected: "Ausgewählte hinzufügen ({n})", createAndAdd: "Anlegen und hinzufügen", createTitle: "Neuen Vorgang anlegen und hinzufügen",
  inSprint: "In einem Sprint", teamOf: "Team des Sprints",
};
