namespace ClaudeMonitor.Agent.Config;

/// <summary>The keys of the Login messages (see <see cref="Strings"/>).</summary>
public static partial class Msg
{
    public const string LoginUnsupported = "login.unsupported";
    public const string LoginGiveServer = "login.giveserver";
    public const string LoginNoAnswer = "login.noanswer";
    public const string LoginItAnswered = "login.itanswered";
    public const string LoginRefusedUpgrade = "login.refused.upgrade";
    public const string LoginRefusedLimit = "login.refused.limit";
    public const string LoginRefusedOther = "login.refused.other";
    public const string LoginCodeMarker = "login.codemarker";
    public const string LoginOpen = "login.open";
    public const string LoginRetryMarker = "login.retrymarker";
    public const string LoginRetry = "login.retry";
    public const string LoginConnected = "login.connected";
    public const string LoginDenied = "login.denied";
    public const string LoginExpiredLost = "login.expiredlost";
    public const string LoginExpired = "login.expired";
    public const string LoginExpiredNoAnswer = "login.expirednoanswer";
    public const string LogoutDone = "logout.done";
    public const string ConnectAlready = "connect.already";
    public const string ConnectGiveServer = "connect.giveserver";
    public const string ConnectCouldNotStart = "connect.couldnotstart";
    public const string ConnectNoCodeYet = "connect.nocodeyet";
    public const string ConnectApprove = "connect.approve";
}

public static partial class Strings
{
    private static IEnumerable<Entry> LoginEntries() =>
    [
        E(Msg.LoginUnsupported, "aipod runs on macOS, Windows and Linux.", "aipod macOS, Windows ve Linux üzerinde çalışır.", "aipod läuft unter macOS, Windows und Linux."),
        E(Msg.LoginGiveServer, "Give the server: aipod login --server https://<your Claude Monitor address> (https, or http on localhost).", "Sunucuyu verin: aipod login --server https://<Claude Monitor adresiniz> (https, ya da localhost için http).", "Geben Sie den Server an: aipod login --server https://<Ihre Claude-Monitor-Adresse> (https, oder http auf localhost)."),
        E(Msg.LoginNoAnswer, "The server did not answer ({0}). Check the address and the connection, then run aipod login again.", "Sunucu yanıt vermedi ({0}). Adresi ve bağlantıyı denetleyin, sonra aipod login'i yeniden çalıştırın.", "Der Server hat nicht geantwortet ({0}). Prüfen Sie die Adresse und die Verbindung und führen Sie aipod login erneut aus."),
        E(Msg.LoginItAnswered, "it answered {0}", "{0} yanıtı verdi", "er antwortete {0}"),
        E(Msg.LoginRefusedUpgrade, "The server no longer accepts this aipod ({0}). Upgrade the agent, then run aipod login again.", "Sunucu artık bu aipod'u ({0}) kabul etmiyor. Ajanı yükseltin, sonra aipod login'i yeniden çalıştırın.", "Der Server akzeptiert dieses aipod ({0}) nicht mehr. Aktualisieren Sie den Agent und führen Sie aipod login erneut aus."),
        E(Msg.LoginRefusedLimit, "The server is limiting requests from this network. Wait a minute, then run aipod login again.", "Sunucu bu ağdan gelen istekleri sınırlıyor. Bir dakika bekleyin, sonra aipod login'i yeniden çalıştırın.", "Der Server begrenzt Anfragen aus diesem Netzwerk. Warten Sie eine Minute und führen Sie aipod login erneut aus."),
        E(Msg.LoginRefusedOther, "{0} answered {1}; it does not look like a Claude Monitor server. Check the address, then run aipod login again.", "{0} {1} yanıtı verdi; bir Claude Monitor sunucusuna benzemiyor. Adresi denetleyin, sonra aipod login'i yeniden çalıştırın.", "{0} antwortete {1}; das sieht nicht nach einem Claude-Monitor-Server aus. Prüfen Sie die Adresse und führen Sie aipod login erneut aus."),
        E(Msg.LoginCodeMarker, "enter the code", "kodu girin", "geben Sie den Code ein"),
        E(Msg.LoginOpen, "Open {0} and enter the code {1}", "{0} adresini açın ve kodu girin {1}", "Öffnen Sie {0} und geben Sie den Code ein {1}"),
        E(Msg.LoginRetryMarker, "trying again", "yeniden denenecek", "erneuter Versuch"),
        E(Msg.LoginRetry, "The server did not answer ({0}); trying again in {1} s.", "Sunucu yanıt vermedi ({0}); {1} sn içinde yeniden denenecek.", "Der Server hat nicht geantwortet ({0}); erneuter Versuch in {1} s."),
        E(Msg.LoginConnected, "Connected. This machine now reports to Claude Monitor.", "Bağlandı. Bu makine artık Claude Monitor'a bildirim gönderiyor.", "Verbunden. Diese Maschine meldet jetzt an Claude Monitor."),
        E(Msg.LoginDenied, "The request was denied on the web.", "İstek web'de reddedildi.", "Die Anfrage wurde im Web abgelehnt."),
        E(Msg.LoginExpiredLost, "The code expired, or it was used for an answer that did not arrive. Run aipod login again.", "Kodun süresi doldu ya da ulaşmayan bir yanıt için kullanıldı. aipod login'i yeniden çalıştırın.", "Der Code ist abgelaufen oder wurde für eine Antwort verwendet, die nicht angekommen ist. Führen Sie aipod login erneut aus."),
        E(Msg.LoginExpired, "The code expired. Run aipod login again.", "Kodun süresi doldu. aipod login'i yeniden çalıştırın.", "Der Code ist abgelaufen. Führen Sie aipod login erneut aus."),
        E(Msg.LoginExpiredNoAnswer, "The code expired while the server did not answer ({0}). Check the connection, then run aipod login again.", "Sunucu yanıt vermezken kodun süresi doldu ({0}). Bağlantıyı denetleyin, sonra aipod login'i yeniden çalıştırın.", "Der Code ist abgelaufen, während der Server nicht geantwortet hat ({0}). Prüfen Sie die Verbindung und führen Sie aipod login erneut aus."),
        E(Msg.LogoutDone, "Signed out on this machine. Revoke it on the web (Machines) to remove it there too.", "Bu makinede oturum kapatıldı. Web'de de kaldırmak için orada (Makineler) iptal edin.", "Auf dieser Maschine abgemeldet. Widerrufen Sie sie im Web (Maschinen), um sie auch dort zu entfernen."),
        E(Msg.ConnectAlready, "Already connected to {0}. Run aipod logout first to connect to another server.", "Zaten {0} sunucusuna bağlı. Başka bir sunucuya bağlanmak için önce aipod logout çalıştırın.", "Bereits mit {0} verbunden. Führen Sie zuerst aipod logout aus, um sich mit einem anderen Server zu verbinden."),
        E(Msg.ConnectGiveServer, "Give the server: aipod connect --server https://<your aipod server address> (https, or http on localhost).", "Sunucuyu verin: aipod connect --server https://<aipod sunucu adresiniz> (https, ya da localhost için http).", "Geben Sie den Server an: aipod connect --server https://<Ihre aipod-Serveradresse> (https, oder http auf localhost)."),
        E(Msg.ConnectCouldNotStart, "Could not start the login. Run: aipod login --server {0}", "Giriş başlatılamadı. Şunu çalıştırın: aipod login --server {0}", "Die Anmeldung konnte nicht gestartet werden. Führen Sie aus: aipod login --server {0}"),
        E(Msg.ConnectNoCodeYet, "The login did not show a code yet. Look at {0}, or run aipod login --server {1} in a terminal.", "Giriş henüz bir kod göstermedi. {0} dosyasına bakın ya da bir terminalde aipod login --server {1} çalıştırın.", "Die Anmeldung hat noch keinen Code gezeigt. Sehen Sie in {0} nach oder führen Sie in einem Terminal aipod login --server {1} aus."),
        E(Msg.ConnectApprove, "Approve it on that page while signed in. This machine then connects by itself; aipod status shows when.", "Bunu o sayfada, oturum açıkken onaylayın. Bu makine sonra kendiliğinden bağlanır; ne zaman bağlandığını aipod status gösterir.", "Geben Sie ihn auf dieser Seite frei, während Sie angemeldet sind. Diese Maschine verbindet sich dann von selbst; aipod status zeigt, wann."),
    ];
}
