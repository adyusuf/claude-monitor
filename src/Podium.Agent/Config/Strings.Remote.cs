namespace Podium.Agent.Config;

/// <summary>The keys of the sentences the remote verbs print for a person (see <see cref="Strings"/>).</summary>
public static partial class Msg
{
    public const string RemoteNotConnected = "remote.notconnected";
    public const string RemoteUnexpected = "remote.unexpected";
    public const string RemoteUsage = "remote.usage";
    public const string RemoteNoJob = "remote.nojob";
    public const string RemoteJobNotActive = "remote.jobnotactive";
    public const string RemoteFileSpec = "remote.filespec";
    public const string RemoteFileMissing = "remote.filemissing";
    public const string RemoteStdinMissing = "remote.stdinmissing";
    public const string RemoteAttachmentsBad = "remote.attachmentsbad";
    public const string RemoteScriptMissing = "remote.scriptmissing";
    public const string RemoteQueued = "remote.queued";
    public const string RemoteRunQueued = "remote.runqueued";
    public const string RemoteOwnerApproves = "remote.ownerapproves";
    public const string RemoteAskSent = "remote.asksent";
    public const string RemoteAskNotCreated = "remote.asknotcreated";
    public const string RemoteAskUnknown = "remote.askunknown";
    public const string RemoteAskLine = "remote.askline";
    public const string RemoteRunNotCreated = "remote.runnotcreated";
    public const string RemoteRunUnknown = "remote.rununknown";
    public const string RemoteRunLine = "remote.runline";
    public const string RemoteRunLineOn = "remote.runlineon";
    public const string RemoteExitCode = "remote.exitcode";
    public const string RemoteErrorCode = "remote.errorcode";
    public const string RemoteWaitingApproval = "remote.waitingapproval";
    public const string RemoteNotFinished = "remote.notfinished";
    public const string TrayAutostartOn = "tray.autostart.on";
    public const string TrayAutostartOnRun = "tray.autostart.onrun";
    public const string TrayAutostartOff = "tray.autostart.off";
    public const string TrayAutostartOnlyWinMac = "tray.autostart.onlywinmac";
}

public static partial class Strings
{
    private static IEnumerable<Entry> RemoteEntries() =>
    [
        E(Msg.RemoteNotConnected, "This machine is not connected: run aipod login --server <url> (or install --standalone --server <url>) first.", "Bu makine bağlı değil: önce aipod login --server <url> (ya da install --standalone --server <url>) çalıştırın.", "Diese Maschine ist nicht verbunden: führen Sie zuerst aipod login --server <url> (oder install --standalone --server <url>) aus."),
        E(Msg.RemoteUnexpected, "Unexpected answer: {0}.", "Beklenmeyen yanıt: {0}.", "Unerwartete Antwort: {0}."),
        E(Msg.RemoteUsage, "usage: {0}", "kullanım: {0}", "Verwendung: {0}"),
        E(Msg.RemoteNoJob, "No job {0} in this workspace.", "Bu çalışma alanında {0} kimlikli iş yok.", "Im Arbeitsbereich gibt es keinen Job {0}."),
        E(Msg.RemoteJobNotActive, "Job {0} is {1}; only an approved job runs.", "{0} kimlikli iş {1} durumunda; yalnızca onaylı bir iş çalışır.", "Job {0} ist {1}; nur ein freigegebener Job läuft."),
        E(Msg.RemoteFileSpec, "--file takes <path in the run's folder>=<local file>, not \"{0}\".", "--file <çalıştırmanın klasöründeki yol>=<yerel dosya> alır, \"{0}\" değil.", "--file nimmt <Pfad im Ordner der Ausführung>=<lokale Datei>, nicht \"{0}\"."),
        E(Msg.RemoteFileMissing, "The file {0} is missing or too large.", "{0} dosyası yok ya da çok büyük.", "Die Datei {0} fehlt oder ist zu groß."),
        E(Msg.RemoteStdinMissing, "The stdin file {0} is missing or too large.", "{0} stdin dosyası yok ya da çok büyük.", "Die stdin-Datei {0} fehlt oder ist zu groß."),
        E(Msg.RemoteAttachmentsBad, "The files or the stdin are not acceptable: at most 8 text files of 64 KiB (256 KiB together) at relative paths of letters, digits, dot, underscore and dash, and a stdin of at most 65536 characters.", "Dosyalar ya da stdin kabul edilemez: en çok 8 metin dosyası, her biri en çok 64 KiB (toplam 256 KiB), yolları harf, rakam, nokta, alt çizgi ve tireden oluşan göreli yollar, ve en çok 65536 karakterlik bir stdin.", "Die Dateien oder stdin sind nicht zulässig: höchstens 8 Textdateien zu je 64 KiB (zusammen 256 KiB) mit relativen Pfaden aus Buchstaben, Ziffern, Punkt, Unterstrich und Bindestrich und ein stdin von höchstens 65536 Zeichen."),
        E(Msg.RemoteScriptMissing, "The script file {0} is missing or too large.", "{0} betik dosyası yok ya da çok büyük.", "Die Skriptdatei {0} fehlt oder ist zu groß."),
        E(Msg.RemoteQueued, "Queued as request {0}. Read it with: aipod result {1}", "{0} isteği olarak kuyruğa alındı. Okumak için: aipod result {1}", "Als Anfrage {0} eingereiht. Lesen mit: aipod result {1}"),
        E(Msg.RemoteRunQueued, "Run {0}: {1}.{2} Read it with: aipod result {0} --wait 30", "Çalıştırma {0}: {1}.{2} Okumak için: aipod result {0} --wait 30", "Ausführung {0}: {1}.{2} Lesen mit: aipod result {0} --wait 30"),
        E(Msg.RemoteOwnerApproves, " Its owner approves it on the web (until {0}).", " Sahibi bunu web'de onaylar ({0} tarihine kadar).", " Der Eigentümer gibt sie im Web frei (bis {0})."),
        E(Msg.RemoteAskSent, "Ask {0} sent to {1}. Read the answer with: aipod result {0} --wait 30", "{0} sorusu {1} makinesine gönderildi. Yanıtı okumak için: aipod result {0} --wait 30", "Frage {0} an {1} gesendet. Antwort lesen mit: aipod result {0} --wait 30"),
        E(Msg.RemoteAskNotCreated, "Ask {0} was not created.", "{0} sorusu oluşturulmadı.", "Frage {0} wurde nicht erstellt."),
        E(Msg.RemoteAskUnknown, "Ask {0} is not known to this agent yet; try again shortly.", "{0} sorusu bu ajan tarafından henüz bilinmiyor; birazdan yeniden deneyin.", "Frage {0} ist diesem Agent noch nicht bekannt; versuchen Sie es gleich noch einmal."),
        E(Msg.RemoteAskLine, "Ask {0} to {1}: {2}", "{0} sorusu, {1}: {2}", "Frage {0} an {1}: {2}"),
        E(Msg.RemoteRunNotCreated, "Run {0} was not created.", "{0} çalıştırması oluşturulmadı.", "Ausführung {0} wurde nicht erstellt."),
        E(Msg.RemoteRunUnknown, "Run {0} is not known to this agent yet; try again shortly.", "{0} çalıştırması bu ajan tarafından henüz bilinmiyor; birazdan yeniden deneyin.", "Ausführung {0} ist diesem Agent noch nicht bekannt; versuchen Sie es gleich noch einmal."),
        E(Msg.RemoteRunLine, "Run {0}: {1}", "Çalıştırma {0}: {1}", "Ausführung {0}: {1}"),
        E(Msg.RemoteRunLineOn, "Run {0} on {1}: {2}{3}{4}", "Çalıştırma {0}, {1}: {2}{3}{4}", "Ausführung {0} auf {1}: {2}{3}{4}"),
        E(Msg.RemoteExitCode, ", exit code {0}", ", çıkış kodu {0}", ", Exit-Code {0}"),
        E(Msg.RemoteErrorCode, ", error {0}", ", hata {0}", ", Fehler {0}"),
        E(Msg.RemoteWaitingApproval, "\nWaiting for the owner's approval on the web.", "\nSahibin web'deki onayı bekleniyor.", "\nWarte auf die Freigabe des Eigentümers im Web."),
        E(Msg.RemoteNotFinished, "\nNot finished yet.", "\nHenüz bitmedi.", "\nNoch nicht beendet."),
        E(Msg.TrayAutostartOn, "The tray starts at login ({0}).", "Tepsi oturum açıldığında başlar ({0}).", "Der Tray startet bei der Anmeldung ({0})."),
        E(Msg.TrayAutostartOnRun, "The tray starts at login (HKCU Run).", "Tepsi oturum açıldığında başlar (HKCU Run).", "Der Tray startet bei der Anmeldung (HKCU Run)."),
        E(Msg.TrayAutostartOff, "The tray no longer starts at login.", "Tepsi artık oturum açıldığında başlamaz.", "Der Tray startet nicht mehr bei der Anmeldung."),
        E(Msg.TrayAutostartOnlyWinMac, "Starting at login is set up for Windows and macOS only.", "Oturum açıldığında başlatma yalnızca Windows ve macOS için kurulur.", "Der Start bei der Anmeldung wird nur für Windows und macOS eingerichtet."),
    ];
}
