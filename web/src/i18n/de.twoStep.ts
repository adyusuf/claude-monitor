import type { Dictionary } from "./en";

// The two-step sign-in guide (/two-step), German: the same keys as en.twoStep.ts; de.ts assigns it as `twoStep`.
export const deTwoStep: Dictionary["twoStep"] = {
  title: "Authenticator-App (Anmeldung in zwei Schritten)",
  nav: "Authenticator-App", navOff: "nicht eingerichtet",
  intro: "Einige Aktionen verlangen einen 6-stelligen Code: Fragen erlauben, einen Agent neu starten oder aktualisieren, den Zugriff auf Produktionsmaschinen freigeben. Der Code kommt aus einer Authenticator-App auf Ihrem Telefon und ändert sich alle 30 Sekunden. Die App richten Sie einmal hier ein.",
  stepsTitle: "So richten Sie sie ein",
  step1: "Installieren Sie eine Authenticator-App auf Ihrem Telefon: Google Authenticator, Microsoft Authenticator, Authy oder 1Password. Jede funktioniert auf die gleiche Weise.",
  step2: "Drücken Sie im Feld unten auf „Einrichten“. Ein quadratischer QR-Code erscheint.",
  step3: "Drücken Sie in der App auf + (Konto hinzufügen) und wählen Sie „QR-Code scannen“, richten Sie dann die Kamera auf den Code auf diesem Bildschirm. Keine Kamera? Wählen Sie „Einrichtungsschlüssel eingeben“ und tippen Sie den Schlüssel ein, der unter dem Code steht.",
  step4: "Die App zeigt jetzt einen 6-stelligen Code. Geben Sie ihn in das Feld ein und drücken Sie auf „Einschalten“.",
  step5: "Speichern Sie die Wiederherstellungscodes, die erscheinen. Sie werden nur einmal angezeigt und lassen Sie sich anmelden, wenn Sie das Telefon verlieren.",
  after: "Wann immer Podium künftig den Code aus Ihrer Authenticator-App verlangt, öffnen Sie die App und geben den 6-stelligen Code ein, den sie in diesem Moment anzeigt.",
  on: "Die Anmeldung in zwei Schritten ist für Ihr Konto eingeschaltet.", off: "Die Anmeldung in zwei Schritten ist aus. Folgen Sie den Schritten oben, um sie einzuschalten.",
  help: "Woher bekomme ich diesen Code?", qrLabel: "QR-Code für Ihre Authenticator-App", scan: "Scannen Sie diesen Code mit Ihrer Authenticator-App:",
  manual: "Scannen nicht möglich? Geben Sie stattdessen diesen Einrichtungsschlüssel in der App ein:", guide: "Schritt-für-Schritt-Anleitung",
};
