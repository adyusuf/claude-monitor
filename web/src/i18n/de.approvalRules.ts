import type { Dictionary } from "./en";
import type { enApprovalRulesErrors } from "./en.approvalRules";

// Das Freigabeband, sein Dialog und die Seite Freigaben (ADR-0019); de.ts weist es als `approvalRules` zu.
export const deApprovalRules: Dictionary["approvalRules"] = {
  title: "Freigaben",
  intro: "Worauf Ihre Claude-Sitzungen warten, die Freigaben, die Sie eine Weile gelten lassen, und was zuletzt beantwortet wurde.",
  bandOne: "1 Freigabe wartet", bandMany: "{n} Freigaben warten", bandRuns: "{n} Ausführungen warten auf Sie", details: "Details", dialog: "Wartende Freigaben",
  waiting: "Wartend", noneWaiting: "Nichts wartet auf Sie.",
  rules: "Stehende Freigaben", noRules: "Sie haben keine stehenden Freigaben.",
  recent: "Zuletzt beantwortet", noRecent: "Zuletzt wurde nichts beantwortet.",
  session: "Sitzung", machine: "Maschine", tool: "Werkzeug", command: "Befehl", input: "Eingabe", expires: "Wartet bis", expired: "Die Zeit ist um: diese Anfrage wird jetzt auf der Maschine gestellt.", untitled: "Sitzung ohne Titel",
  remember: "Erlauben für", duration: { once: "Nur dieses Mal", "5m": "5 Minuten", "10m": "10 Minuten", "30m": "30 Minuten", "1h": "1 Stunde", always: "Immer" },
  rememberHint: "Dasselbe Werkzeug auf dieser Maschine in diesem Projekt wird dann bis zum Ende von selbst erlaubt: für Bash nur genau dieser Befehl, für eine Datei nur diese Datei.",
  rememberHintAlways: "Immer gilt, bis Sie es auf der Seite Freigaben widerrufen oder bis es so lange unbenutzt bleibt, wie der Arbeitsbereich seine Sitzungen behält, und fragt nach Ihrem Code.",
  strictNoRemember: "Produktions- oder Datenbankmaschine: jede Anfrage wird einzeln entschieden.",
  noRemember: "Diese Anfrage wird einmal beantwortet: dieses Werkzeug, diese Eingabe oder Datei kann keine stehende Freigabe haben.",
  questionOnSession: "Eine Frage: beantworten Sie sie auf der Sitzungsseite.", openSession: "Sitzung öffnen",
  allow: "Erlauben", deny: "Ablehnen", reason: "Grund (optional)",
  ends: "Endet", never: "bis zum Widerruf", uses: "Verwendet", lastUsed: "Zuletzt verwendet", neverUsed: "nie", revoke: "Widerrufen", revokeConfirm: "Diese stehende Freigabe widerrufen? Die nächste Anfrage fragt wieder.",
  answeredBy: "Beantwortet von", byRule: "eine stehende Freigabe", decision: { allow: "Erlaubt", deny: "Abgelehnt" }, when: "Wann",
  anyInput: "jede Eingabe",
  moreLine: "+1 weitere Zeile", moreLines: "+{n} weitere Zeilen",
};

/** Die Codes der Antwort auf eine Berechtigungsanfrage und der stehenden Freigaben (ADR-0019), als `errors.<code>`; de.ts fügt sie in `errors` ein. */
export const deApprovalRulesErrors: typeof enApprovalRulesErrors = {
  rule_limit: "Sie haben schon die meisten stehenden Freigaben, die möglich sind (200). Widerrufen Sie eine auf der Seite Freigaben, oder erlauben Sie nur dieses eine Mal.",
  rule_not_allowed: "Diese Anfrage kann jetzt keine stehende Freigabe bekommen (die Maschine ist vielleicht Produktion oder eine Datenbank geworden). Erlauben Sie sie nur dieses eine Mal.",
};
