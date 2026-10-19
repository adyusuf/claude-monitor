namespace ClaudeMonitor.Agent.Config;

/// <summary>The keys of the Migration messages (see <see cref="Strings"/>).</summary>
public static partial class Msg
{
    public const string HmWindows = "mig.home.windows";
    public const string HmFromVariable = "mig.home.fromvariable";
    public const string HmServiceMode = "mig.home.servicemode";
    public const string HmAlreadyAipod = "mig.home.alreadyaipod";
    public const string HmNeitherFolder = "mig.home.neither";
    public const string HmAlreadyMoved = "mig.home.alreadymoved";
    public const string HmNoFolder = "mig.home.nofolder";
    public const string HmTargetHasContent = "mig.home.targethascontent";
    public const string HmTargetIsFile = "mig.home.targetisfile";
    public const string HmParentMissing = "mig.home.parentmissing";
    public const string HmWould = "mig.home.would";
    public const string HmDaemonDidNotStop = "mig.home.daemondidnotstop";
    public const string HmNothingMoved = "mig.home.nothingmoved";
    public const string HmDaemonNotRestarted = "mig.home.daemonnotrestarted";
    public const string HmMoved = "mig.home.moved";
    public const string HmDaemonRestarted = "mig.home.daemonrestarted";
    public const string HmUndo = "mig.home.undo";
    public const string MigPlanCopy = "mig.svc.plan.copy";
    public const string MigPlanNoTokens = "mig.svc.plan.notokens";
    public const string MigPlanPolicy = "mig.svc.plan.policy";
    public const string MigPlanCreate = "mig.svc.plan.create";
    public const string MigPlanWait = "mig.svc.plan.wait";
    public const string MigPlanDisable = "mig.svc.plan.disable";
    public const string MigPlanLater = "mig.svc.plan.later";
    public const string MigAlreadyThere = "mig.svc.alreadythere";
    public const string MigNoOldService = "mig.svc.noold";
    public const string MigNotMigrated = "mig.svc.notmigrated";
    public const string MigNotConnected = "mig.svc.notconnected";
    public const string MigHasFiles = "mig.svc.hasfiles";
    public const string MigDryRun = "mig.svc.dryrun";
    public const string MigMigrating = "mig.svc.migrating";
    public const string MigFailed = "mig.svc.failed";
    public const string MigPutBack = "mig.svc.putback";
    public const string MigDone = "mig.svc.done";
    public const string MigLoginAgain = "mig.svc.loginagain";
    public const string MigToGoBack = "mig.svc.togoback";
    public const string MigNothingBack = "mig.svc.nothingback";
    public const string MigBackFailed = "mig.svc.backfailed";
    public const string MigBackDone = "mig.svc.backdone";
    public const string MigNoFinished = "mig.svc.nofinished";
    public const string MigKept = "mig.svc.kept";
    public const string MigCleanFailed = "mig.svc.cleanfailed";
    public const string MigCleaned = "mig.svc.cleaned";
    public const string MigNeedAdminWindows = "mig.svc.needadminwindows";
    public const string MigNeedRoot = "mig.svc.needroot";
    public const string MigDidNotStop = "mig.svc.didnotstop";
    public const string MigLeftLink = "mig.svc.leftlink";
}

public static partial class Strings
{
    private static IEnumerable<Entry> MigrationEntries() =>
    [
        E(Msg.HmWindows, "Not on Windows yet: it needs a junction and a test machine. The agent finds the old folder by itself, so nothing is lost by waiting.", "Windows'ta henüz yok: bir bağlantı noktası (junction) ve bir test makinesi gerekir. Ajan eski klasörü kendiliğinden bulur, bu yüzden beklemekle bir şey kaybolmaz.", "Unter Windows noch nicht: es braucht eine Junction und eine Testmaschine. Der Agent findet den alten Ordner selbst, beim Warten geht also nichts verloren."),
        E(Msg.HmFromVariable, "The home is named by AIPOD_AGENT_HOME or CM_AGENT_HOME; that folder is yours to move.", "Ev dizini AIPOD_AGENT_HOME ya da CM_AGENT_HOME ile belirlenmiş; o klasörü taşımak size kalmış.", "Der Heimatordner wird durch AIPOD_AGENT_HOME oder CM_AGENT_HOME bestimmt; diesen Ordner verschieben Sie selbst."),
        E(Msg.HmServiceMode, "A boot service's home is set by the service definition (ADR-0013, section 4), not moved here.", "Bir açılış hizmetinin ev dizinini hizmet tanımı belirler (ADR-0013, bölüm 4); burada taşınmaz.", "Der Heimatordner eines Systemdienstes wird von der Dienstdefinition bestimmt (ADR-0013, Abschnitt 4) und hier nicht verschoben."),
        E(Msg.HmAlreadyAipod, "Already using the aipod folder: {0}", "aipod klasörü zaten kullanılıyor: {0}", "Der aipod-Ordner wird bereits verwendet: {0}"),
        E(Msg.HmNeitherFolder, "The agent's home is {0}, which is neither the old folder nor the aipod folder; nothing was moved.", "Ajanın ev dizini {0}; bu ne eski klasör ne de aipod klasörü; hiçbir şey taşınmadı.", "Der Heimatordner des Agents ist {0}, weder der alte noch der aipod-Ordner; nichts wurde verschoben."),
        E(Msg.HmAlreadyMoved, "Already moved: {0} is a link to the aipod folder.", "Zaten taşınmış: {0}, aipod klasörüne bir bağlantıdır.", "Bereits verschoben: {0} ist ein Link auf den aipod-Ordner."),
        E(Msg.HmNoFolder, "There is no folder at {0}: nothing to move.", "{0} konumunda klasör yok: taşınacak bir şey yok.", "Unter {0} gibt es keinen Ordner: nichts zu verschieben."),
        E(Msg.HmTargetHasContent, "{0} already has content; nothing was moved. Look at both folders and move one by hand.", "{0} zaten içerik barındırıyor; hiçbir şey taşınmadı. İki klasöre de bakın ve birini elle taşıyın.", "{0} enthält bereits Inhalt; nichts wurde verschoben. Sehen Sie sich beide Ordner an und verschieben Sie einen von Hand."),
        E(Msg.HmTargetIsFile, "{0} is a file; nothing was moved.", "{0} bir dosya; hiçbir şey taşınmadı.", "{0} ist eine Datei; nichts wurde verschoben."),
        E(Msg.HmParentMissing, "The folder that should hold {0} does not exist; nothing was moved.", "{0} yolunu içermesi gereken klasör yok; hiçbir şey taşınmadı.", "Der Ordner, der {0} enthalten soll, existiert nicht; nichts wurde verschoben."),
        E(Msg.HmWould, "Would move {0} to {1} and leave a link at {0}. Nothing was changed.", "{0} klasörü {1} konumuna taşınır ve {0} yerinde bir bağlantı bırakılır. Hiçbir şey değiştirilmedi.", "{0} würde nach {1} verschoben und bei {0} ein Link hinterlassen. Nichts wurde geändert."),
        E(Msg.HmDaemonDidNotStop, "The daemon did not stop, so nothing was moved. Try again in a moment.", "Arka plan süreci durmadı, bu yüzden hiçbir şey taşınmadı. Biraz sonra yeniden deneyin.", "Der Daemon wurde nicht beendet, deshalb wurde nichts verschoben. Versuchen Sie es gleich noch einmal."),
        E(Msg.HmNothingMoved, "Nothing was moved: {0}.", "Hiçbir şey taşınmadı: {0}.", "Nichts wurde verschoben: {0}."),
        E(Msg.HmDaemonNotRestarted, "The daemon could not be started again: run `aipod restart`.", "Arka plan süreci yeniden başlatılamadı: `aipod restart` çalıştırın.", "Der Daemon konnte nicht wieder gestartet werden: führen Sie `aipod restart` aus."),
        E(Msg.HmMoved, "Moved {0} to {1}; {0} is now a link to it.", "{0}, {1} konumuna taşındı; {0} artık ona bir bağlantı.", "{0} wurde nach {1} verschoben; {0} ist jetzt ein Link darauf."),
        E(Msg.HmDaemonRestarted, "The daemon was started again.", "Arka plan süreci yeniden başlatıldı.", "Der Daemon wurde wieder gestartet."),
        E(Msg.HmUndo, "To undo: stop the daemon, then `rm \"{0}\"` and `mv \"{1}\" \"{0}\"`.", "Geri almak için: arka plan sürecini durdurun, sonra `rm \"{0}\"` ve `mv \"{1}\" \"{0}\"` çalıştırın.", "Zum Rückgängigmachen: Daemon beenden, dann `rm \"{0}\"` und `mv \"{1}\" \"{0}\"`."),
        E(Msg.MigPlanCopy, "stop the service {0} and copy its home {1} to {2}", "{0} hizmetini durdur ve ev dizinini {1} konumundan {2} konumuna kopyala", "den Dienst {0} beenden und seinen Heimatordner {1} nach {2} kopieren"),
        E(Msg.MigPlanNoTokens, " (without its encrypted tokens: this machine logs in again)", " (şifreli belirteçleri olmadan: bu makine yeniden giriş yapar)", " (ohne seine verschlüsselten Token: diese Maschine meldet sich neu an)"),
        E(Msg.MigPlanPolicy, "copy the exec policy and the approvers from {0} to {1}", "yürütme ilkesini ve onaylayıcıları {0} konumundan {1} konumuna kopyala", "die Ausführungsrichtlinie und die Freigebenden von {0} nach {1} kopieren"),
        E(Msg.MigPlanCreate, "create the account {0} and the service {1} (binary {2}, home {3}) and start it", "{0} hesabını ve {1} hizmetini oluştur (ikili dosya {2}, ev dizini {3}) ve başlat", "das Konto {0} und den Dienst {1} anlegen (Programm {2}, Heimatordner {3}) und starten"),
        E(Msg.MigPlanWait, "wait up to {0} s for the new service to reach the server; if it does not, put everything back", "yeni hizmetin sunucuya ulaşması için en çok {0} sn bekle; ulaşmazsa her şeyi geri koy", "bis zu {0} s warten, bis der neue Dienst den Server erreicht; sonst alles zurücksetzen"),
        E(Msg.MigPlanDisable, "disable (not remove) the old service {0}; its files stay for {1:0} days", "eski {0} hizmetini devre dışı bırak (kaldırma); dosyaları {1:0} gün kalır", "den alten Dienst {0} deaktivieren (nicht entfernen); seine Dateien bleiben {1:0} Tage"),
        E(Msg.MigPlanLater, "later: `service migrate --undo` goes back, `service migrate --finish` removes the old service, account, folders and policy", "sonra: `service migrate --undo` geri döner, `service migrate --finish` eski hizmeti, hesabı, klasörleri ve ilkeyi kaldırır", "später: `service migrate --undo` geht zurück, `service migrate --finish` entfernt den alten Dienst, das Konto, die Ordner und die Richtlinie"),
        E(Msg.MigAlreadyThere, "Nothing to migrate: the service {0} already exists.", "Taşınacak bir şey yok: {0} hizmeti zaten var.", "Nichts zu migrieren: der Dienst {0} existiert bereits."),
        E(Msg.MigNoOldService, "Nothing to migrate: no service named {0} is installed.", "Taşınacak bir şey yok: {0} adında kurulu bir hizmet yok.", "Nichts zu migrieren: es ist kein Dienst namens {0} installiert."),
        E(Msg.MigNotMigrated, "Not migrated: {0}", "Taşınmadı: {0}", "Nicht migriert: {0}"),
        E(Msg.MigNotConnected, "Not migrated: the old service in {0} is not connected to the server yet. Connect it first (read its login code and approve it), then migrate.", "Taşınmadı: {0} içindeki eski hizmet henüz sunucuya bağlı değil. Önce bağlayın (giriş kodunu okuyup onaylayın), sonra taşıyın.", "Nicht migriert: der alte Dienst in {0} ist noch nicht mit dem Server verbunden. Verbinden Sie ihn zuerst (Anmeldecode lesen und freigeben), dann migrieren."),
        E(Msg.MigHasFiles, "Not migrated: {0} already has files. Look at it and move or remove them first.", "Taşınmadı: {0} içinde zaten dosyalar var. Önce ona bakın ve dosyaları taşıyın ya da kaldırın.", "Nicht migriert: {0} enthält bereits Dateien. Sehen Sie nach und verschieben oder entfernen Sie sie zuerst."),
        E(Msg.MigDryRun, "Dry run: nothing is changed. A real run would:", "Deneme çalıştırması: hiçbir şey değişmez. Gerçek bir çalıştırma şunları yapardı:", "Probelauf: nichts wird geändert. Ein echter Lauf würde:"),
        E(Msg.MigMigrating, "Migrating the service to the aipod names:", "Hizmet aipod adlarına taşınıyor:", "Der Dienst wird auf die aipod-Namen migriert:"),
        E(Msg.MigFailed, "Migration failed: {0}", "Taşıma başarısız: {0}", "Migration fehlgeschlagen: {0}"),
        E(Msg.MigPutBack, "Everything was put back: the old service runs as before and the aipod files were removed.", "Her şey geri konuldu: eski hizmet eskisi gibi çalışıyor ve aipod dosyaları kaldırıldı.", "Alles wurde zurückgesetzt: der alte Dienst läuft wie zuvor und die aipod-Dateien wurden entfernt."),
        E(Msg.MigDone, "Migrated. The service is now {0}; the old one is disabled and kept: its files stay for {1:0} days.", "Taşındı. Hizmet artık {0}; eskisi devre dışı ve saklanıyor: dosyaları {1:0} gün kalır.", "Migriert. Der Dienst heißt jetzt {0}; der alte ist deaktiviert und bleibt erhalten: seine Dateien bleiben {1:0} Tage."),
        E(Msg.MigLoginAgain, "This machine logs in again: read the code with `type \"{0}\"` in an elevated prompt and approve it on the web.", "Bu makine yeniden giriş yapar: kodu yükseltilmiş bir komut isteminde `type \"{0}\"` ile okuyun ve web'de onaylayın.", "Diese Maschine meldet sich neu an: lesen Sie den Code mit `type \"{0}\"` in einer Eingabeaufforderung mit erhöhten Rechten und geben Sie ihn im Web frei."),
        E(Msg.MigToGoBack, "To go back:  service migrate --undo     To remove the old service for good (after {0:0} days):  service migrate --finish", "Geri dönmek için:  service migrate --undo     Eski hizmeti kalıcı olarak kaldırmak için ({0:0} gün sonra):  service migrate --finish", "Zurück:  service migrate --undo     Den alten Dienst endgültig entfernen (nach {0:0} Tagen):  service migrate --finish"),
        E(Msg.MigNothingBack, "There is nothing to go back to: both the aipod service and the disabled old one, and the migration marker, are needed.", "Geri dönülecek bir şey yok: hem aipod hizmeti hem devre dışı eski hizmet hem de taşıma işareti gerekir.", "Es gibt nichts, wohin man zurückkehren könnte: der aipod-Dienst, der deaktivierte alte Dienst und die Migrationsmarke werden alle gebraucht."),
        E(Msg.MigBackFailed, "Going back failed: {0}", "Geri dönüş başarısız: {0}", "Die Rückkehr ist fehlgeschlagen: {0}"),
        E(Msg.MigBackDone, "Back on the old service {0}. The aipod service is disabled and its files are kept; `service migrate` can be run again.", "Eski {0} hizmetine dönüldü. aipod hizmeti devre dışı ve dosyaları saklanıyor; `service migrate` yeniden çalıştırılabilir.", "Zurück beim alten Dienst {0}. Der aipod-Dienst ist deaktiviert und seine Dateien bleiben erhalten; `service migrate` kann erneut ausgeführt werden."),
        E(Msg.MigNoFinished, "There is no finished migration to clean up: the aipod service and its migration marker are needed.", "Temizlenecek tamamlanmış bir taşıma yok: aipod hizmeti ve taşıma işareti gerekir.", "Es gibt keine abgeschlossene Migration zum Aufräumen: der aipod-Dienst und seine Migrationsmarke werden gebraucht."),
        E(Msg.MigKept, "The old service is kept for {0:0} days after the migration; {1:0.0} days are left. `service migrate --finish --now` removes it anyway.", "Eski hizmet taşımadan sonra {0:0} gün saklanır; {1:0.0} gün kaldı. `service migrate --finish --now` onu yine de kaldırır.", "Der alte Dienst wird nach der Migration {0:0} Tage aufbewahrt; es bleiben {1:0.0} Tage. `service migrate --finish --now` entfernt ihn trotzdem."),
        E(Msg.MigCleanFailed, "Cleaning up failed: {0}", "Temizleme başarısız: {0}", "Das Aufräumen ist fehlgeschlagen: {0}"),
        E(Msg.MigCleaned, "The old service, its account, folders and policy are removed. The aipod service is all that is left.", "Eski hizmet, hesabı, klasörleri ve ilkesi kaldırıldı. Geriye yalnız aipod hizmeti kaldı.", "Der alte Dienst, sein Konto, seine Ordner und seine Richtlinie sind entfernt. Übrig bleibt nur der aipod-Dienst."),
        E(Msg.MigNeedAdminWindows, "Migrating a service needs an administrator. Run this from an elevated prompt:  \"{0}\"", "Bir hizmeti taşımak yönetici gerektirir. Bunu yükseltilmiş bir komut isteminden çalıştırın:  \"{0}\"", "Das Migrieren eines Dienstes braucht einen Administrator. Führen Sie dies in einer Eingabeaufforderung mit erhöhten Rechten aus:  \"{0}\""),
        E(Msg.MigNeedRoot, "Migrating a service needs root. Run:  sudo \"{0}\"", "Bir hizmeti taşımak root gerektirir. Çalıştırın:  sudo \"{0}\"", "Das Migrieren eines Dienstes braucht root. Führen Sie aus:  sudo \"{0}\""),
        E(Msg.MigDidNotStop, "The service {0} did not stop within 30 s; continuing.", "{0} hizmeti 30 sn içinde durmadı; devam ediliyor.", "Der Dienst {0} wurde innerhalb von 30 s nicht beendet; es geht weiter."),
        E(Msg.MigLeftLink, "Left the link {0} where it is.", "{0} bağlantısı olduğu yerde bırakıldı.", "Der Link {0} wurde an seiner Stelle belassen."),
    ];
}
