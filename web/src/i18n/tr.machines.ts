import type { Dictionary } from "./en";

// Turkish machines namespace: the same keys as en.machines.ts (the type makes a missing key a build error).
export const trMachines: Dictionary["machines"] = {
  title: "Makineler", empty: "Henüz bağlı makine yok.", connect: "Makine bağla", revoke: "Bağlantıyı kes",
  revokeConfirm: "Bu makinenin bağlantısı kesilsin mi? Ajanı raporlamayı bırakır.", revoked: "Bağlantı kesildi", lastSeen: "Son görülme",
  version: "Ajan", user: "Kullanıcı", moveTo: "Çalışma alanına taşı", enrolled: "Bağlandı",
  updateAvailable: "Güncelleme var: {version}",
  updateHint: "Makinede \"aipod update\" çalıştırın ya da kendini güncellemesine izin verin (çalışma alanı izin veriyorsa: aipod config auto-update on).",
  role: {
    title: "Rol", environment: "Ortam", function: "İşlev", notSet: "belirlenmedi", unknown: "bilinmiyor",
    claimed: "Makinenin kendi beyanı: {role}.", claimTightens: "Beyan rolü yalnızca sıkılaştırır; hiçbir zaman belirlemez ya da gevşetmez.",
    adminOnly: "Rolü yalnızca bir çalışma alanı yöneticisi belirler.", save: "Rolü kaydet", saved: "Rol kaydedildi.",
    lowerHint: "Bir makineyi production ya da database rolünden çıkarmak yakın zamanda giriş yapmış olmayı ister: doğrulama uygulamasındaki kodu girin ya da yeniden giriş yapın.",
    strict: "Sıkı hedef (production ya da database): ADR-0008'in daha sıkı onay kuralları burada geçerlidir.",
    env: { dev: "geliştirme", test: "test", stage: "staging", preprod: "ön-canlı", production: "canlı", other: "diğer" },
    fn: { development: "geliştirici makinesi", application: "uygulama sunucusu", web: "web sunucusu", database: "veritabanı sunucusu", ci: "CI koşucusu", storage: "depolama", other: "diğer" },
  },
  claude: {
    title: "Claude Code", none: "kurulu değil", unknown: "bilinmiyor (eski ajan)", idle: "{n} boşta", sessions: "{live} oturum, {idle} boşta",
    kind: { npm: "npm kurulumu", native: "yerel kurulum", unsupported: "ajanın güncellemediği bir kurulum" },
  },
  tools: {
    title: "Araçlar", none: "Listedeki araçlardan hiçbiri bulunamadı.", unknown: "Henüz bildirilmedi (eski ajan).",
    untrusted: "listelendi, ajan çalıştırmadı: dosya yönetici sahipli değil", interpreters: "Script yorumlayıcıları",
    hint: "Ajan sabit bir listeyi mutlak yollardan yoklar ve bulduklarını bildirir; --version'ı yalnızca güvenilen ya da kendi hesabının sahip olduğu dosyalar için çalıştırır.",
  },
  tags: {
    title: "Etiketler", label: "Etiketler (anahtar=değer, boşlukla ayrılmış)", none: "Etiket yok.", save: "Etiketleri kaydet", saved: "Etiketler kaydedildi.",
    hint: "En fazla {max} çift; anahtar ve değerde harf, rakam, nokta, alt çizgi ve tire kullanılır (1 ile 32 karakter). Claude bunları etiket olarak görür, talimat olarak değil.",
  },
  note: {
    title: "Çalıştırma notu", label: "Bu makinede çalışan kişiler ve Claude oturumları için not", none: "Not yok.", save: "Notu kaydet", saved: "Not kaydedildi.",
    hint: "Düz metin, en fazla {max} karakter. Claude bunu yalnızca bu makineyi sorduğunda, bir yönetici tarafından yazılmış metin olarak işaretli alır.",
  },
  services: {
    title: "Dinleyen servisler", off: "Bu makine dinlediği portları bildirmiyor.", waiting: "Açık; ajan henüz bildirmedi.",
    hidden: "Makinenin sahibi açtı. Listeyi yalnızca sahibi ve çalışma alanı yöneticileri görür.", at: "Bildirildi: {at}",
    hint: "Makinenin dinlediği TCP portları, sistem gösteriyorsa süreç adıyla. Varsayılan kapalı: yalnızca makinenin sahibi açar.",
    turnOn: "Dinleyen portları bildir", turnOff: "Bildirmeyi bırak", saved: "Kaydedildi.", ownerOnly: "Buna yalnızca makinenin sahibi karar verir.",
    enableHint: "Açmak yakın zamanda giriş yapmış olmayı ister: doğrulama uygulamasındaki kodu girin ya da yeniden giriş yapın.",
  },
  actions: {
    title: "Ajan işlemleri", hint: "Bu makinenin ajanından birkaç sabit işten birini yapmasını isteyin. Yazılacak komut yok: her düğme ajanın zaten bildiği tek bir işlemdir. Sonuç aşağıda görünür.",
    ownerOnly: "Bunları yalnızca makinenin sahibi isteyebilir.", offline: "Makine çevrimdışı: işlem 10 dakika bekler, ajan o zamana kadar almazsa düşer.",
    oldAgent: "Bu ajan {min} sürümünden eski ve bu istekleri yok sayar. Makinede \"aipod update\" ile güncelleyin; sonra düğmeler çalışır.",
    enableHint: "Yeniden başlatma ve güncelleme yakın zamanda giriş yapmış olmayı ister: doğrulama uygulamasındaki kodu girin ya da yeniden giriş yapın.",
    strictHint: "Bu makine canlı ya da veritabanı: yeniden başlatma ve güncelleme her seferinde doğrulama uygulamasından yeni bir kod ister.",
    sent: "İstendi: {action}. Ajanın cevabı aşağıda.", none: "Henüz istenen işlem yok.",
    kind: { status: "Durum", update_check: "Güncelleme var mı?", update: "Güncelle", restart: "Yeniden başlat" },
    status: { queued: "ajanı bekliyor", delivered: "ajanda", succeeded: "tamam", failed: "başarısız", expired: "zamanında alınmadı" },
  },
  asks: {
    title: "Diğer makinelerinizden sorular", hint: "Sizin başka bir makinenizdeki bir Claude oturumu, buradaki boşta bir Claude oturumuna soru sorabilir (canlı ya da veritabanı makinesine değil; bir yönetici bu makinenin ortamını belirlemişse). Cevap metin olarak döner. Varsayılan kapalı: yalnızca bu makinenin sahibi açar.",
    off: "Bu makine soru kabul etmiyor.", on: "Bu makine diğer makinelerinizden gelen soruları kabul ediyor.", turnOn: "Sorulara izin ver", turnOff: "Sorulara izin vermeyi bırak", saved: "Kaydedildi.",
    ownerOnly: "Buna yalnızca makinenin sahibi karar verir.", enableHint: "Açmak yakın zamanda giriş yapmış olmayı ister: doğrulama uygulamasındaki kodu girin ya da yeniden giriş yapın.",
    none: "Henüz soru yok.", question: "Soru", answer: "Cevap", answerCut: "Cevap sunucunun sınırında kesildi.",
    status: { queued: "bekliyor", delivered: "iletildi, cevap bekleniyor", answered: "cevaplandı", no_idle_session: "boşta oturum yok", not_accepting: "kabul etmiyor", no_answer: "cevap yok", expired: "süresi doldu", cancelled: "iptal edildi" },
  },
  projects: {
    title: "Projeler", none: "Bu makinede henüz bilinen proje yok.", noneHint: "Bir oturum projede çalışınca ya da bir klasör aipod config projects add ile listelenince proje görünür.",
    listed: "listelenen klasör", session: "bir oturumdan", errors: "7 günde {n} araç hatası", noRuns: "Bu proje için henüz çalıştırma yok.", run: "çalıştırma", cut: "çıktı kesildi",
    passed: "{n} geçti", failed: "{n} kaldı", skipped: "{n} atlandı", kind: { test: "test", build: "derleme", deploy: "dağıtım", other: "diğer" },
  },
};
