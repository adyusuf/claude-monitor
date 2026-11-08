import type { Dictionary } from "./en";

// Die Installationsoptionen auf der Download-Seite; de.ts weist sie als `installOptions` zu.
export const deInstallOptions: Dictionary["installOptions"] = {
  title: "Installationsoptionen", hint: "Wie der Agent startet, unter welchem Konto er läuft und was er darf.",
  mode: "Wie er startet", modes: { plugin: "Mit Claude Code (Plugin-Hooks)", standalone: "Wenn Sie sich am Computer anmelden", service: "Als Dienst beim Start der Maschine" },
  modeHint: {
    plugin: "Claude Code startet ihn mit jeder Sitzung, in Ihrem eigenen Konto. Verbinden Sie die Maschine zuerst (aipod login).",
    standalone: "Ein Hintergrunddienst ohne Claude Code, in Ihrem eigenen Konto: er meldet sich zuerst an und startet dann bei jeder Anmeldung.",
    service: "Für Server: ein Startdienst unter einem eigenen Konto ohne Rechte. Die Installation braucht einen Administrator.",
  },
  runsAs: "Läuft als", ownAccount: "Ihr eigenes Benutzerkonto, mit Ihren Rechten.",
  serviceAccount: "Ein eigenes Dienstkonto (aipod unter Linux, _aipod unter macOS, NT SERVICE\\aipod unter Windows), nie root oder SYSTEM.",
  runAccount: "Ausführungen aus der Ferne als zweites Konto ohne Rechte starten (Linux und macOS)", runAccountName: "Name dieses Kontos", runAccountDefault: "der Standardname",
  admin: "Administratorrechte",
  adminUser: "Er fragt nie nach Administratorrechten; was er ausführt, hat nur die Rechte Ihres Benutzers.",
  adminService: "Der Dienst läuft nie als Administrator: die Installation lehnt root, SYSTEM und LocalSystem ab.",
  allowRoot: "Erlauben, dass das Dienstkonto root oder SYSTEM ist",
  allowRootOn: "Als root meldet der Agent nur Messwerte und Sitzungen: er führt nie einen Befehl aus, Ausführungen aus der Ferne sind aus.",
  exec: "Ausführungen aus der Ferne auf dieser Maschine", execs: { off: "Aus (Standard)", argv: "Nur freigegebene Programme (argv)", shell: "Auch freigegebene Shell-Befehle" },
  runUser: "Geben Sie dies in einem Terminal auf der Maschine ein:", runService: "Geben Sie dies als Administrator in einem Terminal ein (sudo oder eine PowerShell mit erhöhten Rechten):",
};
