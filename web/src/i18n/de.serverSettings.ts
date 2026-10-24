import type { Dictionary } from "./en";

// The serverSettings namespace of the German dictionary (ADR-0017): the same keys as en.serverSettings.ts. "Sie" throughout.
export const deServerSettings: Dictionary["serverSettings"] = {
  title: "Server-Einstellungen: Integrationen",
  intro: "Einstellungen für den ganzen Server. Werte, die auf dem Server selbst gesetzt sind, lassen sich hier nicht ändern.",
  noAccess: "Diese Seite ist für die Administratoren dieses Servers.",
  cannotSave: "Auf dem Server fehlt der Integrationsschlüssel, daher kann hier nichts gespeichert werden. Setzen Sie zuerst AIPOD_INTEGRATION_KEY auf dem Server.",
  names: { smtp: "E-Mail (SMTP)", github: "Anmeldung mit GitHub", google: "Anmeldung mit Google", jira: "Jira" },
  status: { ready: "Bereit", incomplete: "Unvollständig", off: "Aus" },
  statusLabel: "Status: {status}",
  notes: { integration_key_missing: "Auf dem Server fehlt der Integrationsschlüssel: Jira kann seine Tokens erst speichern, wenn er gesetzt ist." },
  fields: {
    host: "Server", port: "Port", user: "Benutzername", password: "Passwort", from: "Absenderadresse", tls: "STARTTLS verwenden",
    client_id: "Client-ID", client_secret: "Client-Geheimnis",
  },
  required: "erforderlich",
  setOnServer: "Auf dem Server gesetzt ({env})",
  saved: "Gespeichert", notSet: "Nicht gesetzt",
  secretKeep: "Leer lassen, um den gespeicherten Wert zu behalten.",
  remove: "Entfernen", removeNamed: "Gespeicherten Wert für {name} entfernen", removing: "Wird beim Speichern entfernt.", undoRemove: "Behalten",
  callback: "Rückrufadresse", callbackHint: "Registrieren Sie diese Adresse beim Anbieter.",
  copy: "Kopieren", copyNamed: "Rückrufadresse von {name} kopieren", copied: "Kopiert", copyFailed: "Die Adresse konnte nicht kopiert werden. Markieren und von Hand kopieren.", hiddenSaved: "Ein auf dieser Seite gespeicherter Wert wird vom Wert des Servers verdeckt. Er gälte wieder, wenn der des Servers entfernt würde.", hiddenRemove: "Gespeicherten Wert entfernen", hiddenRemoveNamed: "Gespeicherten Wert für {name} entfernen, den der Wert des Servers verdeckt", resetDefault: "Standard verwenden", resetNamed: "Gespeicherten Wert für {name} entfernen und den Standard verwenden",
  save: "Speichern", saveNamed: "{name} speichern", savedDone: "Gespeichert.",
  testMail: "Test-E-Mail senden", testMailHint: "Verwendet die gespeicherten Einstellungen und sendet die E-Mail an Ihre eigene Adresse.",
  testMailSent: "Eine Test-E-Mail ging an Ihre Adresse. Sehen Sie in Ihrem Postfach nach.",
  errors: {
    invalid_setting: "Diese Integration hat keine solche Einstellung.",
    invalid_value: "Dieser Wert ist für diese Einstellung nicht gültig.",
  },
};
