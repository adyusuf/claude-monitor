import type { Dictionary } from "./en";

// İndirme sayfasındaki kurulum seçenekleri; tr.ts bunu `installOptions` olarak atar.
export const trInstallOptions: Dictionary["installOptions"] = {
  title: "Kurulum seçenekleri", hint: "Ajanın nasıl başlayacağı, hangi hesapla çalışacağı ve neler yapabileceği.",
  mode: "Nasıl başlar", modes: { plugin: "Claude Code ile (eklenti hook'ları)", standalone: "Bilgisayara giriş yaptığında", service: "Makine açılırken servis olarak" },
  modeHint: {
    plugin: "Claude Code her oturumla birlikte kendi hesabında başlatır. Önce makineyi bağla (aipod login).",
    standalone: "Claude Code olmadan, kendi hesabında çalışan bir arka plan süreci: önce giriş yapar, sonra her girişinde başlar.",
    service: "Sunucular için: kendi yetkisiz hesabıyla açılışta başlayan servis. Kurmak için yönetici gerekir.",
  },
  runsAs: "Çalışacağı hesap", ownAccount: "Kendi kullanıcı hesabın, senin yetkilerinle.",
  serviceAccount: "Kendi servis hesabı (Linux'ta aipod, macOS'ta _aipod, Windows'ta NT SERVICE\\aipod); asla root ya da SYSTEM değil.",
  runAccount: "Uzaktan çalıştırmaları ikinci bir yetkisiz hesapla başlat (Linux ve macOS)", runAccountName: "O hesabın adı", runAccountDefault: "varsayılan ad",
  admin: "Yönetici hakkı",
  adminUser: "Yönetici hakkı istemez; çalıştırdığı her şey yalnızca senin kullanıcının yetkisine sahiptir.",
  adminService: "Servis asla yönetici olarak çalışmaz: kurulum root, SYSTEM ve LocalSystem'i reddeder.",
  allowRoot: "Servis hesabının root ya da SYSTEM olmasına izin ver",
  allowRootOn: "Root olarak ajan yalnızca ölçüm ve oturum bildirir: hiçbir komut çalıştırmaz, uzaktan çalıştırma kapalıdır.",
  exec: "Bu makinede uzaktan çalıştırma", execs: { off: "Kapalı (varsayılan)", argv: "Yalnızca onaylı programlar (argv)", shell: "Onaylı shell komutları da" },
  runUser: "Makinede bir terminale şunu yaz:", runService: "Yönetici olarak bir terminale şunu yaz (sudo ya da yönetici PowerShell):",
};
