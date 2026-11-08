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
  runAccount: "Uzaktan çalıştırmaları ikinci bir yetkisiz hesapla başlat (Linux ve macOS)", runAccountName: "O hesabın adı", runAccountDefault: "varsayılan ad", runAccountInvalid: "Ajanın kabul ettiği bir ad değil: harf, rakam, _ ve -; harf ya da _ ile başlar, en çok 32 karakter, root gibi sistem adı olamaz.",
  admin: "Yönetici hakkı",
  adminUser: "Yönetici hakkı istemez; çalıştırdığı her şey yalnızca senin kullanıcının yetkisine sahiptir.",
  adminService: "Servis asla yönetici olarak çalışmaz: kendi yetkisiz hesabıyla çalışır, kurulum root, SYSTEM ve LocalSystem'i reddeder. Yalnızca kurmak için yönetici gerekir.",
  exec: "Bu makinede uzaktan çalıştırma", execs: { off: "Kapalı (varsayılan)", argv: "Yalnızca onaylı programlar (argv)", shell: "Onaylı shell komutları da" },
  os: { linux: "Linux", macos: "macOS" },
  runUser: "Makinede bir terminale şunu yaz (satırlar sırayla):", runService: "Bunu yönetici olarak bir terminale, sistemine uyan satırla yaz: sudo ajanın tam yolunu ister. Windows'ta yönetici PowerShell'de aipod install --service komutunu aynı seçeneklerle çalıştır.",
};
