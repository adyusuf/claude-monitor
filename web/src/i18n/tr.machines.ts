import type { Dictionary } from "./en";

// Turkish machines namespace: the same keys as en.machines.ts (the type makes a missing key a build error).
export const trMachines: Dictionary["machines"] = {
  title: "Makineler", empty: "Henüz bağlı makine yok.", connect: "Makine bağla", revoke: "Bağlantıyı kes",
  revokeConfirm: "Bu makinenin bağlantısı kesilsin mi? Ajanı raporlamayı bırakır.", revoked: "Bağlantı kesildi", lastSeen: "Son görülme",
  version: "Ajan", user: "Kullanıcı", moveTo: "Çalışma alanına taşı", enrolled: "Bağlandı",
  updateAvailable: "Güncelleme var: {version}",
  updateHint: "Makinede \"cm-agent update\" çalıştırın ya da kendini güncellemesine izin verin (çalışma alanı izin veriyorsa: cm-agent config auto-update on).",
  role: {
    title: "Rol", environment: "Ortam", function: "İşlev", notSet: "belirlenmedi", unknown: "bilinmiyor",
    claimed: "Makinenin kendi beyanı: {role}.", claimTightens: "Beyan rolü yalnızca sıkılaştırır; hiçbir zaman belirlemez ya da gevşetmez.",
    adminOnly: "Rolü yalnızca bir çalışma alanı yöneticisi belirler.", save: "Rolü kaydet", saved: "Rol kaydedildi.",
    lowerHint: "Bir makineyi production ya da database rolünden çıkarmak yakın zamanda giriş yapmış olmayı ister: doğrulama uygulamasındaki kodu girin ya da yeniden giriş yapın.",
    strict: "Bu makinedeki scriptler, onaylayanın doğrulama uygulamasından bir kod ister.",
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
};
