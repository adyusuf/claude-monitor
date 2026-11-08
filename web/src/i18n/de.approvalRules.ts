import type { Dictionary } from "./en";

// Das Freigabeband, sein Dialog und die Seite Freigaben (ADR-0019); de.ts weist es als `approvalRules` zu.
export const deApprovalRules: Dictionary["approvalRules"] = {
  title: "Freigaben",
  intro: "Worauf Ihre Claude-Sitzungen warten, die Freigaben, die Sie eine Weile gelten lassen, und was zuletzt beantwortet wurde.",
  bandOne: "1 Freigabe wartet", bandMany: "{n} Freigaben warten", bandRuns: "{n} Ausführungen warten auf Sie", details: "Details", dialog: "Wartende Freigaben",
  waiting: "Wartend", noneWaiting: "Nichts wartet auf Sie.",
  rules: "Stehende Freigaben", noRules: "Sie haben keine stehenden Freigaben.",
  recent: "Zuletzt beantwortet", noRecent: "Zuletzt wurde nichts beantwortet.",
  session: "Sitzung", machine: "Maschine", tool: "Werkzeug", command: "Befehl", input: "Eingabe", expires: "Wartet bis", untitled: "Sitzung ohne Titel",
  remember: "Erlauben für", duration: { once: "Nur dieses Mal", "5m": "5 Minuten", "10m": "10 Minuten", "30m": "30 Minuten", "1h": "1 Stunde", always: "Immer" },
  rememberHint: "Dasselbe Werkzeug auf dieser Maschine in diesem Projekt wird dann bis zum Ende von selbst erlaubt: für Bash nur genau dieser Befehl, für eine Datei nur diese Datei.",
  rememberHintAlways: "Immer gilt, bis Sie es auf der Seite Freigaben widerrufen, und fragt nach Ihrem Code.",
  strictNoRemember: "Produktions- oder Datenbankmaschine: jede Anfrage wird einzeln entschieden.",
  noRemember: "Diese Anfrage wird einmal beantwortet: dieses Werkzeug, diese Eingabe oder Datei kann keine stehende Freigabe haben.",
  questionOnSession: "Eine Frage: beantworten Sie sie auf der Sitzungsseite.", openSession: "Sitzung öffnen",
  allow: "Erlauben", deny: "Ablehnen", reason: "Grund (optional)",
  ends: "Endet", never: "bis zum Widerruf", uses: "Verwendet", lastUsed: "Zuletzt verwendet", revoke: "Widerrufen", revokeConfirm: "Diese stehende Freigabe widerrufen? Die nächste Anfrage fragt wieder.",
  answeredBy: "Beantwortet von", byRule: "eine stehende Freigabe", decision: { allow: "Erlaubt", deny: "Abgelehnt" }, when: "Wann",
  anyInput: "jede Eingabe",
};
