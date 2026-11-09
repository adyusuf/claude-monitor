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
  runAccount: "Ausführungen aus der Ferne als zweites Konto ohne Rechte starten (Linux und macOS)", runAccountName: "Name dieses Kontos", runAccountDefault: "der Standardname", runAccountInvalid: "Kein Name, den der Agent annimmt: Buchstaben, Ziffern, _ und -, beginnend mit einem Buchstaben oder _, höchstens 32 Zeichen und kein Systemname wie root.",
  admin: "Administratorrechte",
  adminUser: "Er fragt nie nach Administratorrechten; was er ausführt, hat nur die Rechte Ihres Benutzers.",
  adminService: "Der Dienst läuft nie als Administrator: er läuft unter seinem eigenen Konto ohne Rechte, und die Installation lehnt root, SYSTEM und LocalSystem ab. Nur die Installation braucht einen Administrator.",
  exec: "Ausführungen aus der Ferne auf dieser Maschine", execs: { off: "Aus (Standard)", argv: "Nur freigegebene Programme (argv)", shell: "Auch freigegebene Shell-Befehle" },
  os: { linux: "Linux", macos: "macOS", windows: "Windows (PowerShell mit erhöhten Rechten)" },
  runUser: "Geben Sie dies in einem Terminal auf der Maschine ein (eine Zeile nach der anderen):", runService: "Geben Sie dies als Administrator ein, mit der Zeile für Ihr System: unter Linux und macOS mit sudo, unter Windows in einer PowerShell mit erhöhten Rechten. Jede Zeile nennt den vollen Pfad des Agents, weil die Shell eines Administrators Ihre eigenen Ordner nicht durchsucht.",
  noWindows: "Keine Windows-Zeile: ein Ausführungskonto gibt es nur unter Linux und macOS.",
  serviceHttps: "Ein Dienst braucht diesen Server unter einer https-Adresse. Öffnen Sie diese Seite über die https-Adresse des Servers, um den Befehl zu erhalten.",
  standaloneHttps: "Der Agent nimmt eine http-Adresse nur auf dieser Maschine (localhost) an. Öffnen Sie diese Seite über die https-Adresse des Servers, um den Befehl zu erhalten.",
};
