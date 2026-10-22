namespace Podium.Agent.Config;

/// <summary>The keys of the Cli messages (see <see cref="Strings"/>).</summary>
public static partial class Msg
{
    public const string Usage = "cli.usage";
    public const string ErrStopWait = "cli.err.stopwait";
    public const string ErrPush = "cli.err.push";
    public const string ErrPushFormat = "cli.err.pushformat";
    public const string RestartNotConnected = "cli.restart.notconnected";
    public const string RestartDidNotStop = "cli.restart.didnotstop";
    public const string RestartDone = "cli.restart.done";
    public const string RestartFailed = "cli.restart.failed";
    public const string StatusHome = "cli.status.home";
    public const string StatusConnected = "cli.status.connected";
    public const string StatusNotConnected = "cli.status.notconnected";
    public const string StatusDaemon = "cli.status.daemon";
    public const string StatusRunning = "cli.status.running";
    public const string StatusNotRunning = "cli.status.notrunning";
    public const string StatusEventsWaiting = "cli.status.events";
    public const string StatusCommandsWaiting = "cli.status.commands";
    public const string StatusCommandsNone = "cli.status.commandsnone";
    public const string StatusStopWait = "cli.status.stopwait";
    public const string StatusRemoteRuns = "cli.status.remoteruns";
    public const string StatusServiceSuffix = "cli.status.servicesuffix";
    public const string StatusClaudeWeb = "cli.status.claudeweb";
    public const string StatusByAutomatic = "cli.status.byauto";
    public const string StatusVersion = "cli.status.version";
    public const string UpdateUsage = "cli.update.usage";
    public const string UpdateNotConnected = "cli.update.notconnected";
    public const string UpdateRefused = "cli.update.refused";
    public const string UpdateCurrent = "cli.update.current";
    public const string UpdateBelowMinimum = "cli.update.belowmin";
    public const string UpdateAvailable = "cli.update.available";
    public const string UpdateDone = "cli.update.done";
    public const string UpdateFailed = "cli.update.failed";
    public const string ConfigAutoUpdate = "cli.config.autoupdate";
    public const string ConfigClaudeUpdate = "cli.config.claudeupdate";
    public const string ConfigPushOff = "cli.config.pushoff";
    public const string ConfigPushOn = "cli.config.pushon";
    public const string ConfigPushFormatShort = "cli.config.pushformatshort";
    public const string ConfigPushFormatFull = "cli.config.pushformatfull";
    public const string ConfigRoleUsage = "cli.config.roleusage";
    public const string ConfigRoleClaim = "cli.config.roleclaim";
    public const string ConfigApprovals = "cli.config.approvals";
    public const string ConfigApprovalsNothing = "cli.config.approvalsnothing";
    public const string ConfigUsage = "cli.config.usage";
    public const string ProjectsService = "cli.projects.service";
    public const string ProjectsListed = "cli.projects.listed";
    public const string ProjectsNotListed = "cli.projects.notlisted";
    public const string ProjectsRemoved = "cli.projects.removed";
    public const string ProjectsNotInList = "cli.projects.notinlist";
    public const string ProjectsUsage = "cli.projects.usage";
    public const string ClaudeUpdateUsage = "cli.claudeupdate.usage";
    public const string ClaudeUpdateNone = "cli.claudeupdate.none";
    public const string ClaudeUpdateCancelled = "cli.claudeupdate.cancelled";
}

public static partial class Strings
{
    private static IEnumerable<Entry> CliEntries() =>
    [
        E(Msg.Usage, Cli.Usage, "aipod login --server <url>   bu makineyi bağlar (web'de onaylanacak bir kod gösterir)\naipod install [--stop-wait <saniye>] [--push off|inbox|channel]\n                                bu ajanı başlatan Claude Code eklentisini (kancalar + MCP) kaydeder;\n                                --stop-wait: biten bir turun web'den gelecek bir istemi ne kadar bekleyeceği (0-590, varsayılan 0)\n                                --push: web mesajlarını boştaki bir oturuma ilet (varsayılan off). inbox: arka plan hizmeti oturumun kendi\n                                soketine yazar, başlatma bayrağı gerekmez (ADR-0007); channel (ayrıca \"on\"): bir Claude Code kanalı (ADR-0003)\naipod install --exec off|argv|shell\n                                onaylı uzak çalıştırmaların bu makinede yürütülüp yürütülemeyeceği (varsayılan off)\naipod install --standalone [--server <url>] [--exec off|argv|shell]\naipod uninstall --standalone\n                                bu makine Claude Code olmadan bir arka plan hizmeti olarak: önce giriş yapar, sonra oturum açtığınızda başlar\n                                (macOS ve Windows'ta bir uygulama, Linux'ta systemd --user), kendi hesabınızda (ADR-0008)\naipod install --service --server <url> [--exec off|argv|shell] [--allow-root] [--run-account [<ad>]]\naipod uninstall --service    (yönetici) sunucular için kendi hesabı altında bir açılış hizmeti (ADR-0005); --run-account uzak çalıştırmaları\n                                ikinci, ayrıcalıksız bir hesapla başlatır (Linux ve macOS, sudo ile; ADR-0010)\naipod connect [--server <url>]\n                                login gibi, Claude Code içinden: kodu yazar ve döner; giriş arka planda tamamlanır\naipod service migrate [--dry-run | --undo | --finish [--now]]\n                                (yönetici) bir açılış hizmetini aipod adlarına taşır (aipod, _cmagent, com.aipod.agent yerine aipod, _aipod, aipod.agent), eskisini\n                                7 gün devre dışı tutar; --undo geri döner, --finish eski hizmeti, hesabı, klasörleri ve politikayı kaldırır (ADR-0013, tier 2)\naipod restart                çalışan arka plan hizmetini bu ikili dosyaya devreder\naipod migrate-home [--dry-run]\n                                eski ev klasörünü aipod klasörüne taşır ve eski yola bir bağ bırakır (macOS, Linux; ADR-0013);\n                                kendiliğinden yapılmaz, emin değilse reddeder\naipod status                 bağlantı ve kuyruk\naipod update [--check]       sunucudan en yeni imzalı ajan sürümünü kurar (--check: yalnızca bakar)\naipod config [auto-update off|check|on] [claude-update on|off] [push off|inbox|channel]\n                                ajanın kendi kendine güncelleme arayıp kurup kurmayacağı ve tüm oturumlar boştayken\n                                Claude Code'u güncelleyip güncelleyemeyeceği (ikisi de varsayılan kapalı; çalışma alanı da izin vermeli)\naipod config projects add|remove|list [<klasör>]  bu makinenin hakkında olgu bildirdiği proje klasörleri (ADR-0008)\naipod config role <environment|none> <function|none>\n                                bu makinenin kendini ne olarak tanıttığı (dev|test|stage|preprod|production|other; development|application|web|\n                                database|ci|storage|other); rolü web'de bir yönetici belirler, bir beyan onu yalnızca sıkılaştırır (ADR-0008)\naipod approvers add <açık anahtar> [--name <ad>] | remove <parmak izi> | list\naipod config approvals required|off\n                                bir sahibin onay anahtarını sabitler (web'den kopyalayın; parmak izini karşılaştırın) ve her uzak çalıştırmanın\n                                sabitlenmiş bir anahtarla imzalanmasını şart koşar; varsayılan kapalı. Bunları bir yönetici çalıştırır (hizmette dosya yöneticiye aittir) (ADR-0010)\naipod machines | machine <x> | metrics <x> | alerts [<x>] | jobs [<x>] | job-run <iş kimliği>\naipod run --machine <x> (--script <dosya> --interpreter <i> [--arg <v>]... | --argv <exe> [args...]) [--timeout] [--reason] [--project] [--kind]\naipod result <çalıştırma ya da soru kimliği> [--wait <saniye>] [--offset <karakter>]\naipod ask --machine <x> \"<soru>\"\n                                çalışma alanının diğer makinelerine buradan, web olmadan ulaşır (onay yine sahibindir, web'de; ADR-0008)\naipod claude-update cancel   geri sayımdaki bir Claude Code güncellemesini durdurur\naipod logout | uninstall | version\n(aipod hook <Event> | mcp | daemon, Claude Code ve ajanın kendisi tarafından başlatılır)", "aipod login --server <url>   verbindet diese Maschine (zeigt einen Code, der im Web freigegeben wird)\naipod install [--stop-wait <Sekunden>] [--push off|inbox|channel]\n                                registriert das Claude-Code-Plugin (Hooks + MCP), das den Agent startet;\n                                --stop-wait: wie lange ein beendeter Durchgang auf eine Eingabe aus dem Web wartet (0-590, Standard 0)\n                                --push: Webnachrichten in eine leerlaufende Sitzung liefern (Standard off). inbox: der Hintergrunddienst schreibt in den\n                                eigenen Socket der Sitzung, kein Startparameter (ADR-0007); channel (auch \"on\"): ein Claude-Code-Kanal (ADR-0003)\naipod install --exec off|argv|shell\n                                ob freigegebene Remote-Ausführungen auf dieser Maschine laufen dürfen (Standard off)\naipod install --standalone [--server <url>] [--exec off|argv|shell]\naipod uninstall --standalone\n                                diese Maschine als Hintergrunddienst ohne Claude Code: erst die Anmeldung, dann der Start bei Ihrer Anmeldung\n                                (eine App unter macOS und Windows, systemd --user unter Linux), im eigenen Konto (ADR-0008)\naipod install --service --server <url> [--exec off|argv|shell] [--allow-root] [--run-account [<Name>]]\naipod uninstall --service    (Admin) ein Boot-Dienst unter eigenem Konto für Server (ADR-0005); --run-account startet Remote-Ausführungen\n                                als zweites, unprivilegiertes Konto (Linux und macOS, über sudo; ADR-0010)\naipod connect [--server <url>]\n                                wie login, aus Claude Code heraus: gibt den Code aus und kehrt zurück; die Anmeldung endet im Hintergrund\naipod service migrate [--dry-run | --undo | --finish [--now]]\n                                (Admin) verschiebt einen Boot-Dienst auf die aipod-Namen (aus aipod, _cmagent, com.aipod.agent werden aipod, _aipod, aipod.agent) und hält den alten\n                                7 Tage deaktiviert; --undo geht zurück, --finish entfernt den alten Dienst, das Konto, die Ordner und die Richtlinie (ADR-0013, Tier 2)\naipod restart                übergibt den laufenden Hintergrunddienst an diese Programmdatei\naipod migrate-home [--dry-run]\n                                verschiebt den alten Home-Ordner in den aipod-Ordner und lässt am alten Pfad einen Link (macOS, Linux; ADR-0013);\n                                nie von selbst, verweigert bei Unsicherheit\naipod status                 Verbindung und Warteschlange\naipod update [--check]       installiert den neuesten signierten Agent-Build vom Server (--check: nur nachsehen)\naipod config [auto-update off|check|on] [claude-update on|off] [push off|inbox|channel]\n                                ob der Agent von selbst nach Agent-Updates sucht und sie installiert und ob er Claude Code\n                                aktualisieren darf, wenn alle Sitzungen leerlaufen (beides standardmäßig aus; der Arbeitsbereich muss auch erlauben)\naipod config projects add|remove|list [<Ordner>]  die Projektordner, zu denen diese Maschine Fakten meldet (ADR-0008)\naipod config role <environment|none> <function|none>\n                                als was sich diese Maschine ausgibt (dev|test|stage|preprod|production|other; development|application|web|\n                                database|ci|storage|other); die Rolle legt ein Admin im Web fest, eine Angabe verschärft sie nur (ADR-0008)\naipod approvers add <öffentlicher Schlüssel> [--name <Name>] | remove <Fingerabdruck> | list\naipod config approvals required|off\n                                pinnt den Freigabeschlüssel eines Eigentümers (aus dem Web kopieren; Fingerabdruck vergleichen) und verlangt, dass jede\n                                Remote-Ausführung mit einem gepinnten Schlüssel signiert ist; standardmäßig aus. Ein Admin führt das aus (die Datei gehört bei einem Dienst dem Admin) (ADR-0010)\naipod machines | machine <x> | metrics <x> | alerts [<x>] | jobs [<x>] | job-run <Job-ID>\naipod run --machine <x> (--script <Datei> --interpreter <i> [--arg <v>]... | --argv <exe> [args...]) [--timeout] [--reason] [--project] [--kind]\naipod result <Ausführungs- oder Fragen-ID> [--wait <Sekunden>] [--offset <Zeichen>]\naipod ask --machine <x> \"<Frage>\"\n                                erreicht die anderen Maschinen des Arbeitsbereichs von hier aus, ohne das Web (die Freigabe bleibt beim Eigentümer, im Web; ADR-0008)\naipod claude-update cancel   stoppt ein Claude-Code-Update, das herunterzählt\naipod logout | uninstall | version\n(aipod hook <Event> | mcp | daemon werden von Claude Code und vom Agent selbst gestartet)"),
        E(Msg.ErrStopWait, "--stop-wait takes whole seconds from 0 to {0}, not \"{1}\".", "--stop-wait 0 ile {0} arasında tam saniye alır, \"{1}\" değil.", "--stop-wait nimmt ganze Sekunden von 0 bis {0}, nicht \"{1}\"."),
        E(Msg.ErrPush, "--push takes on, off, inbox or channel, not \"{0}\".", "--push on, off, inbox ya da channel alır, \"{0}\" değil.", "--push nimmt on, off, inbox oder channel, nicht \"{0}\"."),
        E(Msg.ErrPushFormat, "--push-format takes full or short, not \"{0}\".", "--push-format full ya da short alır, \"{0}\" değil.", "--push-format nimmt full oder short, nicht \"{0}\"."),
        E(Msg.RestartNotConnected, "Not connected: there is no daemon to restart.", "Bağlı değil: yeniden başlatılacak bir arka plan hizmeti yok.", "Nicht verbunden: Es gibt keinen Hintergrunddienst, der neu gestartet werden könnte."),
        E(Msg.RestartDidNotStop, "The daemon did not stop; it keeps running its older version until it ends.", "Arka plan hizmeti durmadı; sona erene kadar eski sürümüyle çalışmayı sürdürür.", "Der Hintergrunddienst hat nicht angehalten; er läuft mit seiner älteren Version weiter, bis er endet."),
        E(Msg.RestartDone, "The daemon was restarted.", "Arka plan hizmeti yeniden başlatıldı.", "Der Hintergrunddienst wurde neu gestartet."),
        E(Msg.RestartFailed, "The daemon could not be started.", "Arka plan hizmeti başlatılamadı.", "Der Hintergrunddienst konnte nicht gestartet werden."),
        E(Msg.StatusHome, "home: {0}", "ev klasörü: {0}", "Home-Ordner: {0}"),
        E(Msg.StatusConnected, "connected: {0}", "bağlı: {0}", "verbunden: {0}"),
        E(Msg.StatusNotConnected, "not connected (aipod login --server <url>)", "bağlı değil (aipod login --server <url>)", "nicht verbunden (aipod login --server <url>)"),
        E(Msg.StatusDaemon, "daemon: {0}", "arka plan hizmeti: {0}", "Hintergrunddienst: {0}"),
        E(Msg.StatusRunning, "running", "çalışıyor", "läuft"),
        E(Msg.StatusNotRunning, "not running", "çalışmıyor", "läuft nicht"),
        E(Msg.StatusEventsWaiting, "events waiting: {0}", "bekleyen olaylar: {0}", "wartende Ereignisse: {0}"),
        E(Msg.StatusCommandsWaiting, "commands waiting: {0} (oldest expires {1})", "bekleyen komutlar: {0} (en eskisi {1} süresi dolar)", "wartende Befehle: {0} (der älteste läuft um {1} ab)"),
        E(Msg.StatusCommandsNone, "commands waiting: 0", "bekleyen komutlar: 0", "wartende Befehle: 0"),
        E(Msg.StatusStopWait, "stop wait: {0} s", "durdurma bekleme: {0} sn", "Stopp-Wartezeit: {0} s"),
        E(Msg.StatusRemoteRuns, "remote runs: {0}{1}", "uzak çalıştırmalar: {0}{1}", "Remote-Ausführungen: {0}{1}"),
        E(Msg.StatusServiceSuffix, " (service)", " (hizmet)", " (Dienst)"),
        E(Msg.StatusClaudeWeb, "Claude from the web: {0} session(s) started here running{1}; automatic start: {2}", "Web'den Claude: burada başlatılan {0} oturum çalışıyor{1}; otomatik başlatma: {2}", "Claude aus dem Web: {0} hier gestartete Sitzung(en) laufen{1}; automatischer Start: {2}"),
        E(Msg.StatusByAutomatic, " ({0} by the automatic mode)", " ({0} tanesi otomatik modla)", " ({0} davon durch den automatischen Modus)"),
        E(Msg.StatusVersion, "version: {0}", "sürüm: {0}", "Version: {0}"),
        E(Msg.UpdateUsage, "usage: aipod update [--check]", "kullanım: aipod update [--check]", "Verwendung: aipod update [--check]"),
        E(Msg.UpdateNotConnected, "not connected (aipod login --server <url>): the update comes from the server the agent is connected to", "bağlı değil (aipod login --server <url>): güncelleme, ajanın bağlı olduğu sunucudan gelir", "nicht verbunden (aipod login --server <url>): das Update kommt vom Server, mit dem der Agent verbunden ist"),
        E(Msg.UpdateRefused, "update refused: {0}: {1}", "güncelleme reddedildi: {0}: {1}", "Update abgelehnt: {0}: {1}"),
        E(Msg.UpdateCurrent, "{0} ({1})", "{0} ({1})", "{0} ({1})"),
        E(Msg.UpdateBelowMinimum, "this version ({0}) is below the server's minimum supported version {1}", "bu sürüm ({0}) sunucunun desteklediği en düşük sürümün {1} altında", "diese Version ({0}) liegt unter der vom Server mindestens unterstützten Version {1}"),
        E(Msg.UpdateAvailable, "update available: {0} (running {1}); install it with: aipod update", "güncelleme var: {0} (çalışan {1}); kurmak için: aipod update", "Update verfügbar: {0} (läuft {1}); installieren mit: aipod update"),
        E(Msg.UpdateDone, "updated: {0}", "güncellendi: {0}", "aktualisiert: {0}"),
        E(Msg.UpdateFailed, "update {0}: {1}", "güncelleme {0}: {1}", "Update {0}: {1}"),
        E(Msg.ConfigAutoUpdate, "auto-update: {0}", "otomatik güncelleme: {0}", "automatisches Update: {0}"),
        E(Msg.ConfigClaudeUpdate, "claude-update: {0} on this machine (the workspace must allow it too)", "claude-update: bu makinede {0} (çalışma alanı da izin vermeli)", "claude-update: {0} auf dieser Maschine (der Arbeitsbereich muss auch erlauben)"),
        E(Msg.ConfigPushOff, "push: off (web messages arrive at the next prompt)", "push: kapalı (web mesajları bir sonraki istemde gelir)", "push: aus (Webnachrichten kommen bei der nächsten Eingabe an)"),
        E(Msg.ConfigPushOn, "push: on through the {0}", "push: {0} üzerinden açık", "push: an über {0}"),
        E(Msg.ConfigPushFormatShort, "push-format: short (the inbox gets a one-line notice; the session reads the message with its inbox tool, aipod_inbox_read or aipod_inbox_read)", "push-format: short (gelen kutusuna tek satırlık bir bildirim gider; oturum mesajı gelen kutusu aracıyla okur, aipod_inbox_read ya da aipod_inbox_read)", "push-format: short (der Posteingang bekommt einen einzeiligen Hinweis; die Sitzung liest die Nachricht mit ihrem Posteingangswerkzeug, aipod_inbox_read oder aipod_inbox_read)"),
        E(Msg.ConfigPushFormatFull, "push-format: full (the inbox gets the whole message)", "push-format: full (gelen kutusuna mesajın tamamı gider)", "push-format: full (der Posteingang bekommt die ganze Nachricht)"),
        E(Msg.ConfigRoleUsage, "role takes an environment ({0}|none) and a function ({1}|none).", "role bir ortam ({0}|none) ve bir işlev ({1}|none) alır.", "role nimmt eine Umgebung ({0}|none) und eine Funktion ({1}|none)."),
        E(Msg.ConfigRoleClaim, "role claim: {0} (an admin sets the role on the web; a claim only makes it stricter)", "rol beyanı: {0} (rolü web'de bir yönetici belirler; bir beyan onu yalnızca sıkılaştırır)", "Rollenangabe: {0} (die Rolle legt ein Admin im Web fest; eine Angabe verschärft sie nur)"),
        E(Msg.ConfigApprovals, "signed approvals: {0} ({1} key(s) pinned)", "imzalı onaylar: {0} ({1} anahtar sabitlendi)", "signierte Freigaben: {0} ({1} Schlüssel gepinnt)"),
        E(Msg.ConfigApprovalsNothing, "; NOTHING can run until a key is pinned (aipod approvers add)", "; bir anahtar sabitlenene kadar HİÇBİR ŞEY çalışamaz (aipod approvers add)", "; NICHTS kann laufen, bis ein Schlüssel gepinnt ist (aipod approvers add)"),
        E(Msg.ConfigUsage, "usage: aipod config [auto-update off|check|on] [claude-update on|off] [push off|inbox|channel] [push-format full|short] [role <environment|none> <function|none>] [approvals required|off] [projects add|remove|list [<folder>]]", "kullanım: aipod config [auto-update off|check|on] [claude-update on|off] [push off|inbox|channel] [push-format full|short] [role <environment|none> <function|none>] [approvals required|off] [projects add|remove|list [<klasör>]]", "Verwendung: aipod config [auto-update off|check|on] [claude-update on|off] [push off|inbox|channel] [push-format full|short] [role <environment|none> <function|none>] [approvals required|off] [projects add|remove|list [<Ordner>]]"),
        E(Msg.ProjectsService, "a service agent reads its project folders from the policy file an administrator owns (\"projects\").", "bir hizmet ajanı proje klasörlerini bir yöneticinin sahip olduğu politika dosyasından (\"projects\") okur.", "ein Dienst-Agent liest seine Projektordner aus der Richtliniendatei, die einem Administrator gehört (\"projects\")."),
        E(Msg.ProjectsListed, "project folder listed (its key, branch and commit are reported; the path stays on this machine)", "proje klasörü listelendi (anahtarı, dalı ve commit'i bildirilir; yol bu makinede kalır)", "Projektordner aufgenommen (sein Schlüssel, Branch und Commit werden gemeldet; der Pfad bleibt auf dieser Maschine)"),
        E(Msg.ProjectsNotListed, "not listed: give the absolute path of an existing folder that is not listed yet (at most {0}).", "listelenmedi: henüz listelenmemiş, var olan bir klasörün mutlak yolunu verin (en fazla {0}).", "nicht aufgenommen: geben Sie den absoluten Pfad eines vorhandenen Ordners an, der noch nicht aufgenommen ist (höchstens {0})."),
        E(Msg.ProjectsRemoved, "project folder removed", "proje klasörü kaldırıldı", "Projektordner entfernt"),
        E(Msg.ProjectsNotInList, "that folder was not listed", "bu klasör listelenmemişti", "dieser Ordner war nicht aufgenommen"),
        E(Msg.ProjectsUsage, "usage: aipod config projects add <folder> | remove <folder> | list", "kullanım: aipod config projects add <klasör> | remove <klasör> | list", "Verwendung: aipod config projects add <Ordner> | remove <Ordner> | list"),
        E(Msg.ClaudeUpdateUsage, "usage: aipod claude-update cancel", "kullanım: aipod claude-update cancel", "Verwendung: aipod claude-update cancel"),
        E(Msg.ClaudeUpdateNone, "no Claude Code update is waiting to start", "başlamayı bekleyen bir Claude Code güncellemesi yok", "es wartet kein Claude-Code-Update auf den Start"),
        E(Msg.ClaudeUpdateCancelled, "the Claude Code update is cancelled; it is not tried again for {0} h", "Claude Code güncellemesi iptal edildi; {0} saat boyunca yeniden denenmez", "das Claude-Code-Update ist abgebrochen; es wird {0} Stunden lang nicht erneut versucht"),
    ];
}
