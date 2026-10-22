namespace Podium.Agent.Config;

/// <summary>The keys of the Status messages (see <see cref="Strings"/>).</summary>
public static partial class Msg
{
    public const string StEventsQueued = "st.events";
    public const string StOldest = "st.oldest";
    public const string StAgo = "st.ago";
    public const string StNever = "st.never";
    public const string StAgeHours = "st.age.hours";
    public const string StUploadFailure = "st.uploadfailure";
    public const string StConnection = "st.connection";
    public const string StDaemonDown = "st.daemondown";
    public const string StLastMessage = "st.lastmessage";
    public const string StNoMessage = "st.nomessage";
    public const string StStreamConnected = "st.stream.connected";
    public const string StStreamReconnecting = "st.stream.reconnecting";
    public const string StStreamNotOpen = "st.stream.notopen";
    public const string StPushOff = "st.push.off";
    public const string StScopeMachine = "st.push.scopemachine";
    public const string StScopeSession = "st.push.scopesession";
    public const string StNotBound = "st.push.notbound";
    public const string StLastPushed = "st.push.lastpushed";
    public const string StNothingPushed = "st.push.nothing";
    public const string StPushOn = "st.push.on";
    public const string StInboxNotBound = "st.inbox.notbound";
    public const string StInboxKnown = "st.inbox.known";
    public const string StInboxUnknown = "st.inbox.unknown";
    public const string StInboxFailure = "st.inbox.failure";
    public const string StNotices = "st.inbox.notices";
    public const string StInboxLine = "st.inbox.line";
    public const string UpAuto = "st.up.auto";
    public const string UpChecking = "st.up.checking";
    public const string UpAvailable = "st.up.available";
    public const string UpLastCheck = "st.up.lastcheck";
    public const string UpRolledBack = "st.up.rolledback";
    public const string CuOn = "st.cu.on";
    public const string CuOff = "st.cu.off";
    public const string CuLine = "st.cu.line";
    public const string CuWill = "st.cu.will";
    public const string CuLast = "st.cu.last";
    public const string CuFailing = "st.cu.failing";
    public const string ApRequired = "st.ap.required";
    public const string ApOff = "st.ap.off";
    public const string ExDescribe = "st.ex.describe";
    public const string ExRunsAs = "st.ex.runsas";
    public const string AuLogin = "st.au.login";
    public const string AuExpired = "st.au.expired";
    public const string AuRevoked = "st.au.revoked";
    public const string AuReused = "st.au.reused";
    public const string AuUnknown = "st.au.unknown";
    public const string AuNoToken = "st.au.notoken";
    public const string AuHttp = "st.au.http";
    public const string AuNoReason = "st.au.noreason";
}

public static partial class Strings
{
    private static IEnumerable<Entry> StatusEntries() =>
    [
        E(Msg.StEventsQueued, "Events queued locally: {0}{1}; last upload {2}.", "Yerelde sıradaki olaylar: {0}{1}; son yükleme {2}.", "Lokal eingereihte Ereignisse: {0}{1}; letzter Upload {2}."),
        E(Msg.StOldest, " (oldest {0})", " (en eskisi {0})", " (das älteste {0})"),
        E(Msg.StAgo, "{0} ago", "{0} önce", "vor {0}"),
        E(Msg.StNever, "never", "hiç", "nie"),
        E(Msg.StAgeHours, "{0} h", "{0} sa", "{0} Std."),
        E(Msg.StUploadFailure, "Last upload failure: {0} at {1}.", "Son yükleme hatası: {0}, saat {1}.", "Letzter Upload-Fehler: {0} um {1}."),
        E(Msg.StConnection, "Connection: {0} (refused at {1})", "Bağlantı: {0} (reddedildiği saat {1})", "Verbindung: {0} (abgelehnt um {1})"),
        E(Msg.StDaemonDown, "Web stream: the agent daemon is not running.", "Web akışı: ajan arka plan süreci çalışmıyor.", "Web-Datenstrom: der Agent-Daemon läuft nicht."),
        E(Msg.StLastMessage, "; last message id {0} ({1} ago)", "; son ileti kimliği {0} ({1} önce)", "; letzte Nachrichten-ID {0} (vor {1})"),
        E(Msg.StNoMessage, "; no message received yet", "; henüz ileti alınmadı", "; noch keine Nachricht empfangen"),
        E(Msg.StStreamConnected, "Web stream: connected since {0}{1}.", "Web akışı: {0} saatinden beri bağlı{1}.", "Web-Datenstrom: verbunden seit {0}{1}."),
        E(Msg.StStreamReconnecting, "Web stream: reconnecting (failure {0}: {1}){2}.", "Web akışı: yeniden bağlanılıyor (hata {0}: {1}){2}.", "Web-Datenstrom: Verbindung wird neu aufgebaut (Fehler {0}: {1}){2}."),
        E(Msg.StStreamNotOpen, "Web stream: not opened yet{0}.", "Web akışı: henüz açılmadı{0}.", "Web-Datenstrom: noch nicht geöffnet{0}."),
        E(Msg.StPushOff, "Push into the session: off (aipod install --push inbox); web messages arrive at the next prompt.", "Oturuma anlık iletim: kapalı (aipod install --push inbox); web iletileri sonraki istemde ulaşır.", "Push in die Sitzung: aus (aipod install --push inbox); Web-Nachrichten kommen bei der nächsten Eingabe an."),
        E(Msg.StScopeMachine, "every session on this machine", "bu makinedeki her oturum", "jede Sitzung auf dieser Maschine"),
        E(Msg.StScopeSession, "this session ({0})", "bu oturum ({0})", "diese Sitzung ({0})"),
        E(Msg.StNotBound, "not bound yet", "henüz bağlanmadı", "noch nicht gebunden"),
        E(Msg.StLastPushed, "last pushed {0} at {1}", "son iletilen {0}, saat {1}", "zuletzt zugestellt {0} um {1}"),
        E(Msg.StNothingPushed, "nothing pushed yet", "henüz bir şey iletilmedi", "noch nichts zugestellt"),
        E(Msg.StPushOn, "Push into the session: on for {0}; {1}; {2} pushed but not yet seen in the transcript; {3} confirmed; {4} waiting for a hook.", "Oturuma anlık iletim: {0} için açık; {1}; {2} iletildi ama döküm içinde henüz görülmedi; {3} doğrulandı; {4} bir kancayı bekliyor.", "Push in die Sitzung: ein für {0}; {1}; {2} zugestellt, aber noch nicht im Transkript gesehen; {3} bestätigt; {4} warten auf einen Hook."),
        E(Msg.StInboxNotBound, "this session is not bound yet", "bu oturum henüz bağlanmadı", "diese Sitzung ist noch nicht gebunden"),
        E(Msg.StInboxKnown, "this session's inbox is known", "bu oturumun gelen kutusu biliniyor", "der Eingang dieser Sitzung ist bekannt"),
        E(Msg.StInboxUnknown, "this session's inbox is not known yet (a hook has not run since push was switched on)", "bu oturumun gelen kutusu henüz bilinmiyor (anlık iletim açıldığından beri bir kanca çalışmadı)", "der Eingang dieser Sitzung ist noch nicht bekannt (seit dem Einschalten von Push ist kein Hook gelaufen)"),
        E(Msg.StInboxFailure, " Last inbox failure: {0} at {1}.", " Son gelen kutusu hatası: {0}, saat {1}.", " Letzter Eingangsfehler: {0} um {1}."),
        E(Msg.StNotices, " One-line notices: {0} read, {1} not read yet.", " Tek satırlık bildirimler: {0} okundu, {1} henüz okunmadı.", " Einzeilige Hinweise: {0} gelesen, {1} noch nicht gelesen."),
        E(Msg.StInboxLine, "Push into the session: on through the inbox socket; {0}; {1}; {2} pushed but not yet seen in the transcript; {3} confirmed; {4} waiting for a hook.{5}{6}", "Oturuma anlık iletim: gelen kutusu soketi üzerinden açık; {0}; {1}; {2} iletildi ama döküm içinde henüz görülmedi; {3} doğrulandı; {4} bir kancayı bekliyor.{5}{6}", "Push in die Sitzung: ein über den Eingangs-Socket; {0}; {1}; {2} zugestellt, aber noch nicht im Transkript gesehen; {3} bestätigt; {4} warten auf einen Hook.{5}{6}"),
        E(Msg.UpAuto, "auto-update: {0} (this machine: {1}, workspace: {2})", "otomatik güncelleme: {0} (bu makine: {1}, çalışma alanı: {2})", "Auto-Update: {0} (diese Maschine: {1}, Arbeitsbereich: {2})"),
        E(Msg.UpChecking, "update: {0} -> {1} is being checked", "güncelleme: {0} -> {1} denetleniyor", "Update: {0} -> {1} wird geprüft"),
        E(Msg.UpAvailable, "update available: {0} (aipod update)", "güncelleme var: {0} (aipod update)", "Update verfügbar: {0} (aipod update)"),
        E(Msg.UpLastCheck, "last update check: {0} ({1}) {2}", "son güncelleme denetimi: {0} ({1}) {2}", "letzte Update-Prüfung: {0} ({1}) {2}"),
        E(Msg.UpRolledBack, "update {0} was rolled back and is not retried by itself", "{0} güncellemesi geri alındı ve kendiliğinden yeniden denenmez", "Update {0} wurde zurückgenommen und wird nicht von selbst wiederholt"),
        E(Msg.CuOn, "on", "açık", "an"),
        E(Msg.CuOff, "off", "kapalı", "aus"),
        E(Msg.CuLine, "claude-update: {0} (this machine: {1}, workspace: {2})", "claude-update: {0} (bu makine: {1}, çalışma alanı: {2})", "claude-update: {0} (diese Maschine: {1}, Arbeitsbereich: {2})"),
        E(Msg.CuWill, "Claude Code will be updated at {0} (cancel: aipod claude-update cancel)", "Claude Code saat {0} sırasında güncellenecek (iptal: aipod claude-update cancel)", "Claude Code wird um {0} aktualisiert (abbrechen: aipod claude-update cancel)"),
        E(Msg.CuLast, "last Claude Code update: {0} ({1}) {2}", "son Claude Code güncellemesi: {0} ({1}) {2}", "letztes Claude-Code-Update: {0} ({1}) {2}"),
        E(Msg.CuFailing, "Claude Code update failed {0} times in a row: it is tried only every {1:0} h until it works", "Claude Code güncellemesi art arda {0} kez başarısız oldu: çalışana kadar yalnız her {1:0} saatte bir denenir", "Das Claude-Code-Update ist {0}-mal hintereinander fehlgeschlagen: es wird nur alle {1:0} Std. versucht, bis es klappt"),
        E(Msg.ApRequired, "signed approvals: required, {0} key(s) pinned", "imzalı onaylar: zorunlu, {0} anahtar sabitlenmiş", "signierte Freigaben: erforderlich, {0} Schlüssel festgelegt"),
        E(Msg.ApOff, "signed approvals: off, {0} key(s) pinned", "imzalı onaylar: kapalı, {0} anahtar sabitlenmiş", "signierte Freigaben: aus, {0} Schlüssel festgelegt"),
        E(Msg.ExDescribe, "exec level {0}, {1} allowed executables, {2} allowed roots{3}", "yürütme düzeyi {0}, {1} izinli çalıştırılabilir, {2} izinli kök{3}", "Ausführungsstufe {0}, {1} erlaubte Programme, {2} erlaubte Wurzeln{3}"),
        E(Msg.ExRunsAs, ", runs as {0}", ", {0} olarak çalışır", ", läuft als {0}"),
        E(Msg.AuLogin, "Run `aipod login --server <url>` to connect it again.", "Yeniden bağlamak için `aipod login --server <url>` çalıştırın.", "Führen Sie `aipod login --server <url>` aus, um sie erneut zu verbinden."),
        E(Msg.AuExpired, "the API refused this agent: its credentials expired ({0}). {1}", "API bu ajanı reddetti: kimlik bilgilerinin süresi doldu ({0}). {1}", "die API hat diesen Agent abgelehnt: seine Zugangsdaten sind abgelaufen ({0}). {1}"),
        E(Msg.AuRevoked, "the API refused this agent: it was revoked ({0}), on the web or by connecting this machine again. {1}", "API bu ajanı reddetti: iptal edildi ({0}); web'de ya da bu makine yeniden bağlanarak. {1}", "die API hat diesen Agent abgelehnt: er wurde widerrufen ({0}), im Web oder durch erneutes Verbinden dieser Maschine. {1}"),
        E(Msg.AuReused, "the API revoked this agent because its credentials were used twice ({0}): another copy of this agent, or a restored copy of its home folder, is probably running. Stop the other copy first, then on this machine: {1}", "API bu ajanı iptal etti çünkü kimlik bilgileri iki kez kullanıldı ({0}): büyük olasılıkla bu ajanın başka bir kopyası ya da ev klasörünün geri yüklenmiş bir kopyası çalışıyor. Önce diğer kopyayı durdurun, sonra bu makinede: {1}", "die API hat diesen Agent widerrufen, weil seine Zugangsdaten zweimal verwendet wurden ({0}): wahrscheinlich läuft eine andere Kopie dieses Agents oder eine wiederhergestellte Kopie seines Heimatordners. Beenden Sie zuerst die andere Kopie, dann auf dieser Maschine: {1}"),
        E(Msg.AuUnknown, "the API does not know these credentials ({0}): the agent was deleted on the web or the server was reset. {1}", "API bu kimlik bilgilerini tanımıyor ({0}): ajan web'de silindi ya da sunucu sıfırlandı. {1}", "die API kennt diese Zugangsdaten nicht ({0}): der Agent wurde im Web gelöscht oder der Server wurde zurückgesetzt. {1}"),
        E(Msg.AuNoToken, "no refresh token is stored on this machine ({0}). {1}", "bu makinede saklı bir yenileme belirteci yok ({0}). {1}", "auf dieser Maschine ist kein Refresh-Token gespeichert ({0}). {1}"),
        E(Msg.AuHttp, "the API refused to refresh this agent's credentials ({0}; the server gave no reason, it may be an older version). {1}", "API bu ajanın kimlik bilgilerini yenilemeyi reddetti ({0}; sunucu neden vermedi, daha eski bir sürüm olabilir). {1}", "die API hat die Erneuerung der Zugangsdaten dieses Agents abgelehnt ({0}; der Server nannte keinen Grund, vielleicht ist er eine ältere Version). {1}"),
        E(Msg.AuNoReason, "the API refused this agent (no reason given). {0}", "API bu ajanı reddetti (neden verilmedi). {0}", "die API hat diesen Agent abgelehnt (kein Grund genannt). {0}"),
    ];
}
