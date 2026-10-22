namespace Podium.Agent.Config;

/// <summary>The keys of the Install messages (see <see cref="Strings"/>).</summary>
public static partial class Msg
{
    public const string PluginNoClaude = "install.plugin.noclaude";
    public const string PluginInstalled = "install.plugin.installed";
    public const string PluginWaits = "install.plugin.waits";
    public const string PluginNoWait = "install.plugin.nowait";
    public const string PluginPushChannel = "install.plugin.pushchannel";
    public const string PluginPushInbox = "install.plugin.pushinbox";
    public const string PluginPushOff = "install.plugin.pushoff";
    public const string PluginRemoved = "install.plugin.removed";
    public const string ServiceServerOption = "install.service.serveroption";
    public const string ServiceRunAccountWindows = "install.service.runaccountwindows";
    public const string ServiceRunAccountPlain = "install.service.runaccountplain";
    public const string ServiceShellWarning = "install.service.shellwarning";
    public const string ServiceOldNames = "install.service.oldnames";
    public const string ServiceMigrateUsage = "install.service.migrateusage";
    public const string ServiceExecIs = "install.service.execis";
    public const string ServiceExecOption = "install.service.execoption";
    public const string SvcExecBad = "install.svc.execbad";
    public const string SvcRunAccountWindows = "install.svc.runaccountwindows";
    public const string SvcRefuseAccount = "install.svc.refuseaccount";
    public const string SvcAllowRoot = "install.svc.allowroot";
    public const string NotInstalled = "install.notinstalled";
    public const string InstallFailed = "install.failed";
    public const string SvcInstalled = "install.svc.installed";
    public const string SvcNotInstalled = "install.svc.notinstalled";
    public const string UninstallFailed = "install.uninstallfailed";
    public const string SvcRemoved = "install.svc.removed";
    public const string SvcNoSystemd = "install.svc.nosystemd";
    public const string SvcNotConnected = "install.svc.notconnected";
    public const string SvcLogsInItself = "install.svc.logsinitself";
    public const string SvcApproveCode = "install.svc.approvecode";
    public const string SvcNeedAdminWindows = "install.svc.needadminwindows";
    public const string SvcNeedRoot = "install.svc.needroot";
    public const string SvcUnsupportedOs = "install.svc.unsupportedos";
    public const string SvcOldNotStopped = "install.svc.oldnotstopped";
    public const string Undone = "install.undone";
    public const string CouldNotUndo = "install.couldnotundo";
    public const string SaNotAsRoot = "install.sa.notasroot";
    public const string SaNoSystemdUser = "install.sa.nosystemduser";
    public const string SaNotConnected = "install.sa.notconnected";
    public const string SaConnecting = "install.sa.connecting";
    public const string SaLoginIncomplete = "install.sa.loginincomplete";
    public const string SaInstalled = "install.sa.installed";
    public const string SaNotInstalled = "install.sa.notinstalled";
    public const string SaRemoved = "install.sa.removed";
    public const string SaUnsupportedOs = "install.sa.unsupportedos";
    public const string SaReplaced = "install.sa.replaced";
    public const string SaConsoleBuild = "install.sa.consolebuild";
    public const string SaLinger = "install.sa.linger";
    public const string SaCheck = "install.sa.check";
}

public static partial class Strings
{
    private static IEnumerable<Entry> InstallEntries() =>
    [
        E(Msg.PluginNoClaude, "Claude Code could not be reached. Register the plugin yourself:", "Claude Code'a ulaşılamadı. Eklentiyi kendiniz kaydedin:", "Claude Code war nicht erreichbar. Registrieren Sie das Plugin selbst:"),
        E(Msg.PluginInstalled, "Installed. Claude Code starts the agent with its sessions. Binary: {0}", "Kuruldu. Claude Code ajanı oturumlarıyla birlikte başlatır. İkili dosya: {0}", "Installiert. Claude Code startet den Agent mit seinen Sitzungen. Programm: {0}"),
        E(Msg.PluginWaits, "A finished turn waits up to {0} s for a prompt from the web.", "Biten bir tur, web'den gelecek bir istem için en çok {0} sn bekler.", "Ein beendeter Durchgang wartet bis zu {0} s auf eine Eingabe aus dem Web."),
        E(Msg.PluginNoWait, "A finished turn does not wait for the web (aipod install --stop-wait <seconds> changes that).", "Biten bir tur web'i beklemez (aipod install --stop-wait <saniye> bunu değiştirir).", "Ein beendeter Durchgang wartet nicht auf das Web (aipod install --stop-wait <Sekunden> ändert das)."),
        E(Msg.PluginPushChannel, "Push is ON through a channel. Start Claude Code with:  claude {0}  (a channel is a research preview; the agent cannot enable it for you).", "Anlık iletim bir kanal üzerinden AÇIK. Claude Code'u şöyle başlatın:  claude {0}  (kanal bir araştırma önizlemesidir; ajan bunu sizin yerinize açamaz).", "Push ist über einen Kanal EIN. Starten Sie Claude Code mit:  claude {0}  (ein Kanal ist eine Forschungsvorschau; der Agent kann ihn nicht für Sie aktivieren)."),
        E(Msg.PluginPushInbox, "Push is ON through each session's inbox: a web message starts a turn in an idle session, with no start-up flag (ADR-0007).", "Anlık iletim her oturumun gelen kutusu üzerinden AÇIK: bir web iletisi, başlatma bayrağı olmadan boştaki oturumda bir tur başlatır (ADR-0007).", "Push ist über den Eingang jeder Sitzung EIN: eine Web-Nachricht startet in einer ruhenden Sitzung einen Durchgang, ohne Startflag (ADR-0007)."),
        E(Msg.PluginPushOff, "Web messages reach a session at its next prompt (aipod install --push inbox delivers them into an idle session).", "Web iletileri bir oturuma sonraki isteminde ulaşır (aipod install --push inbox bunları boştaki bir oturuma iletir).", "Web-Nachrichten erreichen eine Sitzung bei ihrer nächsten Eingabe (aipod install --push inbox stellt sie in eine ruhende Sitzung zu)."),
        E(Msg.PluginRemoved, "The Claude Code plugin is removed. The agent's data stays in {0}", "Claude Code eklentisi kaldırıldı. Ajanın verileri {0} içinde kalır", "Das Claude-Code-Plugin ist entfernt. Die Daten des Agents bleiben in {0}"),
        E(Msg.ServiceServerOption, "{0} takes the https address of the aipod server, not \"{1}\".", "{0}, aipod sunucusunun https adresini alır, \"{1}\" değil.", "{0} nimmt die https-Adresse des aipod-Servers, nicht „{1}“."),
        E(Msg.ServiceRunAccountWindows, "{0} is supported on Linux and macOS only (ADR-0010 §5).", "{0} yalnız Linux ve macOS'ta desteklenir (ADR-0010 §5).", "{0} wird nur unter Linux und macOS unterstützt (ADR-0010 §5)."),
        E(Msg.ServiceRunAccountPlain, "{0} takes a plain account name (letters, digits, underscore, dash), not \"{1}\".", "{0}, sade bir hesap adı alır (harf, rakam, alt çizgi, tire), \"{1}\" değil.", "{0} nimmt einen einfachen Kontonamen (Buchstaben, Ziffern, Unterstrich, Bindestrich), nicht „{1}“."),
        E(Msg.ServiceShellWarning, "Warning: exec level shell lets approved runs execute any shell text on this machine.", "Uyarı: shell yürütme düzeyi, onaylanmış çalıştırmaların bu makinede her kabuk metnini yürütmesine izin verir.", "Warnung: Die Ausführungsstufe shell lässt freigegebene Läufe jeden Shell-Text auf dieser Maschine ausführen."),
        E(Msg.ServiceOldNames, "This service still has its old names (aipod). It is upgraded as it is; `aipod service migrate` moves it to the aipod names.", "Bu hizmet hâlâ eski adlarını (aipod) taşıyor. Olduğu gibi yükseltilir; `aipod service migrate` onu aipod adlarına taşır.", "Dieser Dienst hat noch seine alten Namen (aipod). Er wird so aktualisiert, wie er ist; `aipod service migrate` stellt ihn auf die aipod-Namen um."),
        E(Msg.ServiceMigrateUsage, "usage: service {0} [{1} | {2} | {3} [{4}]]", "kullanım: service {0} [{1} | {2} | {3} [{4}]]", "Aufruf: service {0} [{1} | {2} | {3} [{4}]]"),
        E(Msg.ServiceExecIs, "Remote runs on this machine: {0} (the workspace switch and an owner's approval still apply).", "Bu makinede uzaktan çalıştırmalar: {0} (çalışma alanı anahtarı ve sahibin onayı yine geçerlidir).", "Remote-Läufe auf dieser Maschine: {0} (der Schalter des Arbeitsbereichs und die Freigabe eines Eigentümers gelten weiterhin)."),
        E(Msg.ServiceExecOption, "{0} takes {1}, not \"{2}\".", "{0} şunlardan birini alır: {1}; \"{2}\" değil.", "{0} nimmt {1}, nicht „{2}“."),
        E(Msg.SvcExecBad, "--exec takes off, argv or shell, not \"{0}\".", "--exec off, argv ya da shell alır, \"{0}\" değil.", "--exec nimmt off, argv oder shell, nicht „{0}“."),
        E(Msg.SvcRunAccountWindows, "--run-account is not supported on Windows: starting a process as another local account needs a stored password or a design that was not built (ADR-0010 §5).", "--run-account Windows'ta desteklenmez: bir işlemi başka bir yerel hesapla başlatmak, saklanan bir parola ya da kurulmamış bir tasarım gerektirir (ADR-0010 §5).", "--run-account wird unter Windows nicht unterstützt: einen Prozess als anderes lokales Konto zu starten braucht ein gespeichertes Kennwort oder einen Entwurf, der nicht gebaut wurde (ADR-0010 §5)."),
        E(Msg.SvcRefuseAccount, "Refusing to run the service as {0}. --allow-root does it for metrics and uploads only; remote execution stays off.", "Hizmet {0} olarak çalıştırılmayacak. --allow-root bunu yalnız ölçümler ve yüklemeler için yapar; uzaktan yürütme kapalı kalır.", "Der Dienst wird nicht als {0} ausgeführt. --allow-root tut das nur für Messwerte und Uploads; die Remote-Ausführung bleibt aus."),
        E(Msg.SvcAllowRoot, "--allow-root: the service runs privileged, so remote execution stays off.", "--allow-root: hizmet ayrıcalıklı çalışır, bu yüzden uzaktan yürütme kapalı kalır.", "--allow-root: der Dienst läuft mit Vorrechten, deshalb bleibt die Remote-Ausführung aus."),
        E(Msg.NotInstalled, "Not installed: {0}", "Kurulmadı: {0}", "Nicht installiert: {0}"),
        E(Msg.InstallFailed, "Install failed: {0}", "Kurulum başarısız: {0}", "Installation fehlgeschlagen: {0}"),
        E(Msg.SvcInstalled, "The service is installed and started. Binary: {0}; home: {1}; exec policy: {2} ({3}).", "Hizmet kuruldu ve başlatıldı. İkili dosya: {0}; ev dizini: {1}; yürütme ilkesi: {2} ({3}).", "Der Dienst ist installiert und gestartet. Programm: {0}; Heimatordner: {1}; Ausführungsrichtlinie: {2} ({3})."),
        E(Msg.SvcNotInstalled, "The service is not installed.", "Hizmet kurulu değil.", "Der Dienst ist nicht installiert."),
        E(Msg.UninstallFailed, "Uninstall failed: {0}", "Kaldırma başarısız: {0}", "Deinstallation fehlgeschlagen: {0}"),
        E(Msg.SvcRemoved, "The service is removed. Kept: the home {0}, the exec policy {1} and the binary {2}.", "Hizmet kaldırıldı. Korunanlar: ev dizini {0}, yürütme ilkesi {1} ve ikili dosya {2}.", "Der Dienst ist entfernt. Erhalten bleiben: der Heimatordner {0}, die Ausführungsrichtlinie {1} und das Programm {2}."),
        E(Msg.SvcNoSystemd, "this machine does not run systemd; only systemd is supported on Linux.", "bu makine systemd çalıştırmıyor; Linux'ta yalnız systemd desteklenir.", "diese Maschine führt kein systemd aus; unter Linux wird nur systemd unterstützt."),
        E(Msg.SvcNotConnected, "Not connected: install again with --server https://<your aipod server address> so the service can log in by itself.", "Bağlı değil: hizmetin kendiliğinden giriş yapabilmesi için --server https://<aipod sunucu adresiniz> ile yeniden kurun.", "Nicht verbunden: installieren Sie erneut mit --server https://<Ihre aipod-Serveradresse>, damit sich der Dienst selbst anmelden kann."),
        E(Msg.SvcLogsInItself, "The service logs in by itself: it shows a code to approve on the web. Read it with:", "Hizmet kendiliğinden giriş yapar: web'de onaylanacak bir kod gösterir. Şununla okuyun:", "Der Dienst meldet sich selbst an: er zeigt einen Code, der im Web freizugeben ist. Lesen Sie ihn mit:"),
        E(Msg.SvcApproveCode, "Then approve the code on the web while signed in; the service starts reporting at once.", "Sonra kodu web'de oturum açıkken onaylayın; hizmet hemen bildirim göndermeye başlar.", "Geben Sie den Code anschließend im Web frei, während Sie angemeldet sind; der Dienst beginnt sofort zu melden."),
        E(Msg.SvcNeedAdminWindows, "The service needs an administrator. Run this from an elevated prompt (Run as administrator):  {0}", "Hizmet bir yönetici gerektirir. Bunu yükseltilmiş bir komut isteminden (Yönetici olarak çalıştır) çalıştırın:  {0}", "Der Dienst braucht einen Administrator. Führen Sie dies in einer Eingabeaufforderung mit erhöhten Rechten aus (Als Administrator ausführen):  {0}"),
        E(Msg.SvcNeedRoot, "The service needs root. Run:  sudo {0}", "Hizmet root gerektirir. Çalıştırın:  sudo {0}", "Der Dienst braucht root. Führen Sie aus:  sudo {0}"),
        E(Msg.SvcUnsupportedOs, "A service is not supported on this OS ({0}).", "Bu işletim sisteminde hizmet desteklenmiyor ({0}).", "Ein Dienst wird auf diesem Betriebssystem nicht unterstützt ({0})."),
        E(Msg.SvcOldNotStopped, "The old service did not stop within {0} s; continuing.", "Eski hizmet {0} sn içinde durmadı; devam ediliyor.", "Der alte Dienst wurde innerhalb von {0} s nicht beendet; es geht weiter."),
        E(Msg.Undone, "Undone: {0}.", "Geri alındı: {0}.", "Rückgängig gemacht: {0}."),
        E(Msg.CouldNotUndo, "Could not undo \"{0}\": {1}", "\"{0}\" geri alınamadı: {1}", "„{0}“ konnte nicht rückgängig gemacht werden: {1}"),
        E(Msg.SaNotAsRoot, "A standalone agent runs as you, not as root or administrator: run this from your own account (a boot service for a server is `install --service`).", "Bağımsız bir ajan root ya da yönetici olarak değil, sizin hesabınızla çalışır: bunu kendi hesabınızdan çalıştırın (bir sunucu için açılış hizmeti `install --service` ile kurulur).", "Ein eigenständiger Agent läuft als Sie, nicht als root oder Administrator: führen Sie dies unter Ihrem eigenen Konto aus (ein Systemdienst für einen Server ist `install --service`)."),
        E(Msg.SaNoSystemdUser, "Not installed: this account has no systemd user session; only systemd is supported on Linux.", "Kurulmadı: bu hesabın systemd kullanıcı oturumu yok; Linux'ta yalnız systemd desteklenir.", "Nicht installiert: dieses Konto hat keine systemd-Benutzersitzung; unter Linux wird nur systemd unterstützt."),
        E(Msg.SaNotConnected, "Not connected: give the server, aipod install --standalone --server https://<your aipod server address>, or run aipod login --server <url> first.", "Bağlı değil: sunucuyu verin, aipod install --standalone --server https://<aipod sunucu adresiniz>, ya da önce aipod login --server <url> çalıştırın.", "Nicht verbunden: geben Sie den Server an, aipod install --standalone --server https://<Ihre aipod-Serveradresse>, oder führen Sie zuerst aipod login --server <url> aus."),
        E(Msg.SaConnecting, "Connecting this machine first: approve the code on the web.", "Önce bu makine bağlanıyor: kodu web'de onaylayın.", "Diese Maschine wird zuerst verbunden: geben Sie den Code im Web frei."),
        E(Msg.SaLoginIncomplete, "Not installed: the login did not complete, so nothing was enabled. Run the command again when you can approve the code.", "Kurulmadı: giriş tamamlanmadı, bu yüzden hiçbir şey etkinleştirilmedi. Kodu onaylayabildiğinizde komutu yeniden çalıştırın.", "Nicht installiert: die Anmeldung wurde nicht abgeschlossen, deshalb wurde nichts aktiviert. Führen Sie den Befehl erneut aus, wenn Sie den Code freigeben können."),
        E(Msg.SaInstalled, "The standalone agent is installed and starts when you log in. Binary: {0}; home: {1}.", "Bağımsız ajan kuruldu ve oturum açtığınızda başlar. İkili dosya: {0}; ev dizini: {1}.", "Der eigenständige Agent ist installiert und startet, wenn Sie sich anmelden. Programm: {0}; Heimatordner: {1}."),
        E(Msg.SaNotInstalled, "The standalone autostart is not installed.", "Bağımsız otomatik başlatma kurulu değil.", "Der Autostart des eigenständigen Agents ist nicht installiert."),
        E(Msg.SaRemoved, "The standalone autostart is removed. Kept: the home {0} (the login and the queue) and the binary {1}.", "Bağımsız otomatik başlatma kaldırıldı. Korunanlar: ev dizini {0} (giriş ve kuyruk) ve ikili dosya {1}.", "Der Autostart des eigenständigen Agents ist entfernt. Erhalten bleiben: der Heimatordner {0} (die Anmeldung und die Warteschlange) und das Programm {1}."),
        E(Msg.SaUnsupportedOs, "A standalone agent is not supported on this OS ({0}).", "Bu işletim sisteminde bağımsız ajan desteklenmiyor ({0}).", "Ein eigenständiger Agent wird auf diesem Betriebssystem nicht unterstützt ({0})."),
        E(Msg.SaReplaced, "Replaced the autostart that had the old names (com.aipod.agent.user / aipod / Aipod).", "Eski adları taşıyan otomatik başlatma değiştirildi (com.aipod.agent.user / aipod / Aipod).", "Der Autostart mit den alten Namen wurde ersetzt (com.aipod.agent.user / aipod / Aipod)."),
        E(Msg.SaConsoleBuild, "Note: a windowless copy could not be made, so the autostart uses the console build and Windows may show a console window at logon.", "Not: penceresiz bir kopya yapılamadı, bu yüzden otomatik başlatma konsol sürümünü kullanır ve Windows oturum açılışında bir konsol penceresi gösterebilir.", "Hinweis: eine fensterlose Kopie konnte nicht erstellt werden, deshalb nutzt der Autostart die Konsolenversion und Windows zeigt bei der Anmeldung eventuell ein Konsolenfenster."),
        E(Msg.SaLinger, "Note: without lingering the agent runs only while you are logged in. To keep it running after you log out: sudo loginctl enable-linger {0}", "Not: lingering olmadan ajan yalnız oturum açıkken çalışır. Oturumu kapattıktan sonra da çalışmasını istiyorsanız: sudo loginctl enable-linger {0}", "Hinweis: ohne Lingering läuft der Agent nur, solange Sie angemeldet sind. Damit er nach der Abmeldung weiterläuft: sudo loginctl enable-linger {0}"),
        E(Msg.SaCheck, "Run `aipod status` to check it. This machine reports no Claude Code (claude=none); other machines can still reach it as a target.", "Denetlemek için `aipod status` çalıştırın. Bu makine Claude Code bildirmiyor (claude=none); diğer makineler yine de ona hedef olarak ulaşabilir.", "Prüfen Sie es mit `aipod status`. Diese Maschine meldet kein Claude Code (claude=none); andere Maschinen können sie trotzdem als Ziel erreichen."),
    ];
}
