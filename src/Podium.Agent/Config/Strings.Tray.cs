namespace Podium.Agent.Config;

/// <summary>The keys of the Tray messages (see <see cref="Strings"/>).</summary>
public static partial class Msg
{
    public const string TrayTooltip = "tray.tooltip";
    public const string TrayTooltipUpdate = "tray.tooltip.update";
    public const string TrayVersion = "tray.version";
    public const string TrayUpdateAvailable = "tray.update.available";
    public const string TrayUpdateAvailableDot = "tray.update.availabledot";
    public const string TrayUpToDate = "tray.update.uptodate";
    public const string TrayOpenWindow = "tray.menu.openwindow";
    public const string TrayReconnect = "tray.menu.reconnect";
    public const string TrayLogin = "tray.menu.login";
    public const string TrayLoginDots = "tray.menu.logindots";
    public const string TrayLoginAgain = "tray.menu.loginagain";
    public const string TrayDisconnect = "tray.menu.disconnect";
    public const string TrayCheckUpdates = "tray.menu.checkupdates";
    public const string TrayUpdateTo = "tray.menu.updateto";
    public const string TrayUpdateNow = "tray.menu.updatenow";
    public const string TrayOpenWeb = "tray.menu.openweb";
    public const string TrayShowLog = "tray.menu.showlog";
    public const string TrayQuit = "tray.menu.quit";
    public const string TrayMessages = "tray.messages";
    public const string TrayHlWaiting = "tray.hl.waiting";
    public const string TrayHlNotConnected = "tray.hl.notconnected";
    public const string TrayHlCannotRead = "tray.hl.cannotread";
    public const string TrayHlRefused = "tray.hl.refused";
    public const string TrayHlNotRunning = "tray.hl.notrunning";
    public const string TrayHlConnected = "tray.hl.connected";
    public const string TrayHlReconnecting = "tray.hl.reconnecting";
    public const string TrayHlStreamNotOpen = "tray.hl.streamnotopen";
    public const string TrayAgeSeconds = "tray.age.seconds";
    public const string TrayAgeMinutes = "tray.age.minutes";
    public const string TrayActReconnecting = "tray.act.reconnecting";
    public const string TrayActDisconnected = "tray.act.disconnected";
    public const string TrayActGiveServer = "tray.act.giveserver";
    public const string TrayActNoServer = "tray.act.noserver";
    public const string TrayActNoLog = "tray.act.nolog";
    public const string TrayActNotCommand = "tray.act.notcommand";
    public const string TrayActFailed = "tray.act.failed";
    public const string TrayActCouldNotRun = "tray.act.couldnotrun";
    public const string TrayActOnlyHttp = "tray.act.onlyhttp";
    public const string TrayActCouldNotOpen = "tray.act.couldnotopen";
}

public static partial class Strings
{
    private static IEnumerable<Entry> TrayEntries() =>
    [
        E(Msg.TrayTooltip, "aipod {0} - {1}", "aipod {0} - {1}", "aipod {0} - {1}"),
        E(Msg.TrayTooltipUpdate, "; update {0} available", "; {0} güncellemesi var", "; Update {0} verfügbar"),
        E(Msg.TrayVersion, "aipod {0}", "aipod {0}", "aipod {0}"),
        E(Msg.TrayUpdateAvailable, "Update {0} is available", "{0} güncellemesi var", "Update {0} ist verfügbar"),
        E(Msg.TrayUpdateAvailableDot, "Update {0} is available.", "{0} güncellemesi var.", "Update {0} ist verfügbar."),
        E(Msg.TrayUpToDate, "Up to date, as far as the last check knows.", "Güncel; son denetimin bildiği kadarıyla.", "Auf dem neuesten Stand, soweit die letzte Prüfung es weiß."),
        E(Msg.TrayOpenWindow, "Open the status window", "Durum penceresini aç", "Statusfenster öffnen"),
        E(Msg.TrayReconnect, "Reconnect", "Yeniden bağlan", "Neu verbinden"),
        E(Msg.TrayLogin, "Log in", "Giriş yap", "Anmelden"),
        E(Msg.TrayLoginDots, "Log in…", "Giriş yap…", "Anmelden…"),
        E(Msg.TrayLoginAgain, "Log in again…", "Yeniden giriş yap…", "Erneut anmelden…"),
        E(Msg.TrayDisconnect, "Disconnect", "Bağlantıyı kes", "Trennen"),
        E(Msg.TrayCheckUpdates, "Check for updates", "Güncellemeleri denetle", "Nach Updates suchen"),
        E(Msg.TrayUpdateTo, "Update to {0}", "{0} sürümüne güncelle", "Auf {0} aktualisieren"),
        E(Msg.TrayUpdateNow, "Update now", "Şimdi güncelle", "Jetzt aktualisieren"),
        E(Msg.TrayOpenWeb, "Open the web app", "Web uygulamasını aç", "Web-App öffnen"),
        E(Msg.TrayShowLog, "Show the log", "Günlüğü göster", "Protokoll anzeigen"),
        E(Msg.TrayQuit, "Quit the tray", "Tepsiyi kapat", "Tray beenden"),
        E(Msg.TrayMessages, "messages in: {0}, out: {1}", "gelen ileti: {0}, giden: {1}", "Nachrichten ein: {0}, aus: {1}"),
        E(Msg.TrayHlWaiting, "waiting for the agent to start", "ajanın başlaması bekleniyor", "warte auf den Start des Agents"),
        E(Msg.TrayHlNotConnected, "not connected", "bağlı değil", "nicht verbunden"),
        E(Msg.TrayHlCannotRead, "cannot read the agent's status ({0})", "ajanın durumu okunamıyor ({0})", "der Status des Agents ist nicht lesbar ({0})"),
        E(Msg.TrayHlRefused, "disconnected: the API refused this agent (see the window)", "bağlantı kesildi: API bu ajanı reddetti (pencereye bakın)", "getrennt: die API hat diesen Agent abgelehnt (siehe Fenster)"),
        E(Msg.TrayHlNotRunning, "connected, the agent is not running", "bağlı, ajan çalışmıyor", "verbunden, der Agent läuft nicht"),
        E(Msg.TrayHlConnected, "connected", "bağlı", "verbunden"),
        E(Msg.TrayHlReconnecting, "reconnecting to the server", "sunucuya yeniden bağlanılıyor", "Verbindung zum Server wird neu aufgebaut"),
        E(Msg.TrayHlStreamNotOpen, "connected, stream not open yet", "bağlı, akış henüz açılmadı", "verbunden, Datenstrom noch nicht offen"),
        E(Msg.TrayAgeSeconds, "{0} s", "{0} sn", "{0} s"),
        E(Msg.TrayAgeMinutes, "{0} min", "{0} dk", "{0} min"),
        E(Msg.TrayActReconnecting, "Reconnecting", "Yeniden bağlanılıyor", "Verbindung wird neu aufgebaut"),
        E(Msg.TrayActDisconnected, "Disconnected", "Bağlantı kesildi", "Getrennt"),
        E(Msg.TrayActGiveServer, "Give the server's address (https://…) to log in.", "Giriş yapmak için sunucunun adresini (https://…) verin.", "Geben Sie zum Anmelden die Adresse des Servers (https://…) an."),
        E(Msg.TrayActNoServer, "No server is saved yet: log in first.", "Henüz kayıtlı bir sunucu yok: önce giriş yapın.", "Noch kein Server gespeichert: melden Sie sich zuerst an."),
        E(Msg.TrayActNoLog, "There is no log yet.", "Henüz günlük yok.", "Es gibt noch kein Protokoll."),
        E(Msg.TrayActNotCommand, "That is not a command to run.", "Bu çalıştırılacak bir komut değil.", "Das ist kein auszuführender Befehl."),
        E(Msg.TrayActFailed, "aipod {0} failed (exit {1}).", "aipod {0} başarısız oldu (çıkış {1}).", "aipod {0} ist fehlgeschlagen (Exit {1})."),
        E(Msg.TrayActCouldNotRun, "aipod {0} could not run ({1}). Is aipod installed?", "aipod {0} çalıştırılamadı ({1}). aipod kurulu mu?", "aipod {0} konnte nicht ausgeführt werden ({1}). Ist aipod installiert?"),
        E(Msg.TrayActOnlyHttp, "Only http and https addresses are opened.", "Yalnızca http ve https adresleri açılır.", "Es werden nur http- und https-Adressen geöffnet."),
        E(Msg.TrayActCouldNotOpen, "Could not open it ({0}).", "Açılamadı ({0}).", "Konnte nicht geöffnet werden ({0})."),
    ];
}
