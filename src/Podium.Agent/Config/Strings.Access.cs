namespace Podium.Agent.Config;

/// <summary>The keys of the access messages (see <see cref="Strings"/>): `aipod config access` and the `aipod status` line (ADR-0020 §4).</summary>
public static partial class Msg
{
    public const string AccessUsage = "cli.access.usage";
    public const string AccessNeedsAdmin = "cli.access.needsadmin";
    public const string AccessNoServiceFile = "cli.access.noservicefile";
    public const string AccessWriteFailed = "cli.access.writefailed";
    public const string AccessSourceService = "cli.access.sourceservice";
    public const string AccessSourceAgent = "cli.access.sourceagent";
    public const string AccessYes = "cli.access.yes";
    public const string AccessNo = "cli.access.no";
    public const string AccessShowCeiling = "cli.access.showceiling";
    public const string AccessShowAllowed = "cli.access.showallowed";
    public const string AccessShowAllowedNone = "cli.access.showallowednone";
    public const string AccessShowDenied = "cli.access.showdenied";
    public const string AccessShowApps = "cli.access.showapps";
    public const string AccessShowAppsNone = "cli.access.showappsnone";
    public const string AccessShowPolicies = "cli.access.showpolicies";
    public const string AccessShowPolicy = "cli.access.showpolicy";
    public const string AccessShowNoStore = "cli.access.shownostore";
    public const string AccessShowDefault = "cli.access.showdefault";
    public const string AccessDefaultApplies = "cli.access.defaultapplies";
    public const string AccessCeilingOnly = "cli.access.ceilingonly";
    public const string AccessSigned = "cli.access.signed";
    public const string AccessUnsigned = "cli.access.unsigned";
    public const string AccessAllowedDone = "cli.access.alloweddone";
    public const string AccessDeniedDone = "cli.access.denieddone";
    public const string AccessAppDone = "cli.access.appdone";
    public const string AccessAppHashed = "cli.access.apphashed";
    public const string AccessNoShell = "cli.access.noshell";
    public const string AccessRemoved = "cli.access.removed";
    public const string AccessNotInCeiling = "cli.access.notinceiling";
    public const string AccessCleared = "cli.access.cleared";
    public const string AccessNotFolder = "cli.access.notfolder";
    public const string AccessForbiddenRoot = "cli.access.forbiddenroot";
    public const string AccessNotExecutable = "cli.access.notexecutable";
    public const string AccessBadHash = "cli.access.badhash";
    public const string StAccess = "st.access";
}

public static partial class Strings
{
    private static IEnumerable<Entry> AccessEntries() =>
    [
        E(Msg.AccessUsage, "usage: aipod config access show | allow <folder> | deny <folder> | app <exe> [--sha256 <hex>] | remove <path> | clear", "kullanım: aipod config access show | allow <klasör> | deny <klasör> | app <program> [--sha256 <hex>] | remove <yol> | clear", "Verwendung: aipod config access show | allow <Ordner> | deny <Ordner> | app <Programm> [--sha256 <hex>] | remove <Pfad> | clear"),
        E(Msg.AccessNeedsAdmin, "the ceiling of a service is in its admin-owned exec policy file: run this as the admin (sudo, or an elevated prompt on Windows)", "bir hizmetin tavanı yöneticiye ait çalıştırma politikası dosyasındadır: bunu yönetici olarak çalıştırın (sudo ya da Windows'ta yükseltilmiş bir komut istemi)", "die Obergrenze eines Dienstes steht in seiner Ausführungsrichtlinie, die dem Admin gehört: führen Sie dies als Admin aus (sudo oder unter Windows eine Eingabeaufforderung mit erhöhten Rechten)"),
        E(Msg.AccessNoServiceFile, "this machine's service has no valid exec policy file ({0}); install the service first (aipod install --service)", "bu makinenin hizmetinin geçerli bir çalıştırma politikası dosyası yok ({0}); önce hizmeti kurun (aipod install --service)", "der Dienst dieser Maschine hat keine gültige Ausführungsrichtlinie ({0}); installieren Sie zuerst den Dienst (aipod install --service)"),
        E(Msg.AccessWriteFailed, "the ceiling could not be saved ({0}); on a service machine run this as the admin", "tavan kaydedilemedi ({0}); hizmet makinesinde bunu yönetici olarak çalıştırın", "die Obergrenze konnte nicht gespeichert werden ({0}); auf einer Dienstmaschine führen Sie dies als Admin aus"),
        E(Msg.AccessSourceService, "the service's exec policy file", "hizmetin çalıştırma politikası dosyası", "die Ausführungsrichtlinie des Dienstes"),
        E(Msg.AccessSourceAgent, "agent.json", "agent.json", "agent.json"),
        E(Msg.AccessYes, "allowed", "izinli", "erlaubt"),
        E(Msg.AccessNo, "refused", "reddedilir", "abgelehnt"),
        E(Msg.AccessShowCeiling, "ceiling ({0}): level {1}, shell and script runs {2}", "tavan ({0}): seviye {1}, kabuk ve betik çalıştırmaları {2}", "Obergrenze ({0}): Stufe {1}, Shell- und Skriptausführungen {2}"),
        E(Msg.AccessShowAllowed, "allowed folders: {0}", "izinli klasörler: {0}", "erlaubte Ordner: {0}"),
        E(Msg.AccessShowAllowedNone, "allowed folders: no limit of its own (what the run account may reach)", "izinli klasörler: kendi sınırı yok (çalıştırma hesabının erişebildiği kadar)", "erlaubte Ordner: keine eigene Grenze (was das Ausführungskonto erreichen kann)"),
        E(Msg.AccessShowDenied, "denied folders: {0}", "yasaklı klasörler: {0}", "gesperrte Ordner: {0}"),
        E(Msg.AccessShowApps, "applications: {0}", "uygulamalar: {0}", "Anwendungen: {0}"),
        E(Msg.AccessShowAppsNone, "applications: no limit of its own", "uygulamalar: kendi sınırı yok", "Anwendungen: keine eigene Grenze"),
        E(Msg.AccessShowPolicies, "web policies on this machine: {0}", "bu makinedeki web politikaları: {0}", "Web-Richtlinien auf dieser Maschine: {0}"),
        E(Msg.AccessShowPolicy, "  {0}: version {1}, {2}, {3}, {4} allowed, {5} denied, {6} application(s), shell {7}", "  {0}: sürüm {1}, {2}, {3}, {4} izinli, {5} yasaklı, {6} uygulama, kabuk {7}", "  {0}: Version {1}, {2}, {3}, {4} erlaubt, {5} gesperrt, {6} Anwendung(en), Shell {7}"),
        E(Msg.AccessShowNoStore, "web policies: none read in this home ({0})", "web politikaları: bu ev klasöründe okunmadı ({0})", "Web-Richtlinien: in diesem Home-Ordner keine gelesen ({0})"),
        E(Msg.AccessShowDefault, "a run whose repository has no policy gets {0}", "deposunun politikası olmayan bir çalıştırma şunu alır: {0}", "eine Ausführung, deren Repository keine Richtlinie hat, bekommt {0}"),
        E(Msg.AccessDefaultApplies, "the default (its project's folder, argv runs only, no shell or script)", "varsayılan (projesinin klasörü, yalnızca argv çalıştırmaları, kabuk ya da betik yok)", "den Standard (der Ordner seines Projekts, nur argv-Ausführungen, keine Shell und kein Skript)"),
        E(Msg.AccessCeilingOnly, "the ceiling only", "yalnızca tavan", "nur die Obergrenze"),
        E(Msg.AccessSigned, "signed", "imzalı", "signiert"),
        E(Msg.AccessUnsigned, "unsigned", "imzasız", "unsigniert"),
        E(Msg.AccessAllowedDone, "allowed {0}: a run's folders must now be under one of the {1} allowed folder(s)", "{0} izinli: bir çalıştırmanın klasörleri artık {1} izinli klasörden birinin altında olmalı", "{0} erlaubt: die Ordner einer Ausführung müssen jetzt unter einem der {1} erlaubten Ordner liegen"),
        E(Msg.AccessDeniedDone, "denied {0}: no run may use it or anything under it", "{0} yasaklandı: hiçbir çalıştırma onu ya da altındakileri kullanamaz", "{0} gesperrt: keine Ausführung darf ihn oder etwas darunter verwenden"),
        E(Msg.AccessAppDone, "application {0} allowed{1}: a run may start only the {2} listed application(s)", "{0} uygulamasına izin verildi{1}: bir çalıştırma yalnızca listelenen {2} uygulamayı başlatabilir", "Anwendung {0} erlaubt{1}: eine Ausführung darf nur die {2} aufgeführten Anwendung(en) starten"),
        E(Msg.AccessAppHashed, " (its SHA-256 is checked before each run)", " (SHA-256'sı her çalıştırmadan önce denetlenir)", " (ihr SHA-256 wird vor jeder Ausführung geprüft)"),
        E(Msg.AccessNoShell, "; shell and script runs are refused while the ceiling has any entry", "; tavanda bir girdi olduğu sürece kabuk ve betik çalıştırmaları reddedilir", "; Shell- und Skriptausführungen werden abgelehnt, solange die Obergrenze einen Eintrag hat"),
        E(Msg.AccessRemoved, "removed {0} from the ceiling", "{0} tavandan kaldırıldı", "{0} aus der Obergrenze entfernt"),
        E(Msg.AccessNotInCeiling, "{0} is not in the ceiling", "{0} tavanda yok", "{0} steht nicht in der Obergrenze"),
        E(Msg.AccessCleared, "ceiling cleared: runs are again limited only by what the run account may reach (the OS permissions)", "tavan temizlendi: çalıştırmalar yine yalnızca çalıştırma hesabının erişebildikleriyle (işletim sistemi izinleri) sınırlı", "Obergrenze geleert: Ausführungen sind wieder nur durch das begrenzt, was das Ausführungskonto erreichen kann (die Rechte des Betriebssystems)"),
        E(Msg.AccessNotFolder, "not changed: give the absolute path of an existing folder", "değiştirilmedi: var olan bir klasörün mutlak yolunu verin", "nicht geändert: geben Sie den absoluten Pfad eines vorhandenen Ordners an"),
        E(Msg.AccessForbiddenRoot, "not changed: {0} is a system or home root, too wide to allow", "değiştirilmedi: {0} bir sistem ya da ev kökü, izin vermek için fazla geniş", "nicht geändert: {0} ist eine System- oder Home-Wurzel, zu weit, um sie zu erlauben"),
        E(Msg.AccessNotExecutable, "not changed: give the absolute path of an existing executable", "değiştirilmedi: var olan bir programın mutlak yolunu verin", "nicht geändert: geben Sie den absoluten Pfad eines vorhandenen Programms an"),
        E(Msg.AccessBadHash, "not changed: --sha256 takes 64 hexadecimal characters", "değiştirilmedi: --sha256 64 onaltılık karakter alır", "nicht geändert: --sha256 nimmt 64 Hexadezimalzeichen"),
        E(Msg.StAccess, "access: ceiling {0} allowed, {1} denied, {2} application(s); web policies {3} applied, {4} not applied; runs without a policy get {5}", "erişim: tavan {0} izinli, {1} yasaklı, {2} uygulama; web politikaları {3} uygulanıyor, {4} uygulanmıyor; politikası olmayan çalıştırmalar şunu alır: {5}", "Zugriff: Obergrenze {0} erlaubt, {1} gesperrt, {2} Anwendung(en); Web-Richtlinien {3} angewendet, {4} nicht angewendet; Ausführungen ohne Richtlinie bekommen {5}"),
    ];
}
