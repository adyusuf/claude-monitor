import type { enViews } from "./en.views";

export const deViews: typeof enViews = {
  members: {
    tabs: "Bereiche der Mitglieder", tabMembers: "Mitglieder", tabInvitations: "Einladungen",
    colMember: "Mitglied", colRole: "Rolle", colJoined: "Beigetreten", colInvited: "Eingeladene E-Mail", colExpires: "Läuft ab",
    inviteTitle: "Jemanden einladen",
  },
  workspace: {
    tabs: "Bereiche der Arbeitsbereich-Einstellungen", colWhen: "Zeitpunkt", colAction: "Aktion", colActor: "Durchgeführt von", colTarget: "Ziel", colDetail: "Detail",
    actorUser: "Benutzer", actorAgent: "Maschine", showDetail: "Anzeigen",
  },
  sessions: {
    pick: "Wählen Sie eine Maschine, um ihre Sitzungen zu sehen.", activeNow: "gerade aktiv", waiting: "{n} wartet", total: "{n} insgesamt",
  },
  integrations: {
    colConnection: "Verbindung", colConnectedBy: "Verbunden von", colProjects: "Importierte Projekte", colLastCheck: "Letzte Prüfung", colDetails: "Details",
    openSettings: "Einstellungen", none: "Es ist noch keine Jira-Site verbunden.",
  },
  machine: {
    tabs: "Bereiche der Maschine", overview: "Übersicht", control: "Steuerung", work: "Aufgaben", monitoring: "Überwachung", access: "Zugriff",
  },
  serverIntegrations: { tabs: "Server-Integrationen" },
};
