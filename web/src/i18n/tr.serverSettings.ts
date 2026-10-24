import type { Dictionary } from "./en";

// The serverSettings namespace of the Turkish dictionary (ADR-0017): the same keys as en.serverSettings.ts.
export const trServerSettings: Dictionary["serverSettings"] = {
  title: "Sunucu ayarları: entegrasyonlar",
  intro: "Tüm sunucu için ayarlar. Sunucunun kendisinde tanımlanan değerler burada değiştirilemez.",
  noAccess: "Bu sayfa bu sunucunun yöneticileri içindir.",
  cannotSave: "Sunucuda entegrasyon anahtarı eksik, bu yüzden burada hiçbir şey kaydedilemez. Önce sunucuda AIPOD_INTEGRATION_KEY değerini tanımlayın.",
  names: { smtp: "E-posta (SMTP)", github: "GitHub ile giriş", google: "Google ile giriş", jira: "Jira" },
  status: { ready: "Hazır", incomplete: "Eksik", off: "Kapalı" },
  statusLabel: "Durum: {status}",
  notes: { integration_key_missing: "Sunucuda entegrasyon anahtarı eksik: bu anahtar tanımlanana kadar Jira jetonlarını saklayamaz." },
  fields: {
    host: "Sunucu", port: "Port", user: "Kullanıcı adı", password: "Parola", from: "Gönderen adresi", tls: "STARTTLS kullan",
    client_id: "İstemci kimliği", client_secret: "İstemci gizli anahtarı",
  },
  required: "zorunlu",
  setOnServer: "Sunucuda tanımlı ({env})",
  saved: "Kayıtlı", notSet: "Tanımlı değil",
  secretKeep: "Kayıtlı değeri korumak için boş bırakın.",
  remove: "Kaldır", removeNamed: "Kayıtlı {name} değerini kaldır", removing: "Kaydettiğinizde kaldırılacak.", undoRemove: "Kalsın",
  callback: "Geri dönüş adresi", callbackHint: "Bu adresi sağlayıcıya kaydedin.",
  copy: "Kopyala", copyNamed: "{name} için geri dönüş adresini kopyala", copied: "Kopyalandı", copyFailed: "Adres kopyalanamadı. Seçip elle kopyalayın.", hiddenSaved: "Bu sayfada kaydedilmiş bir değer, sunucudakiyle gizlenmiş durumda. Sunucudaki kaldırılırsa yeniden geçerli olur.", hiddenRemove: "Kayıtlı değeri kaldır", hiddenRemoveNamed: "Sunucudaki değerin gizlediği kayıtlı {name} değerini kaldır", resetDefault: "Varsayılanı kullan", resetNamed: "Kayıtlı {name} değerini kaldır ve varsayılanı kullan",
  save: "Kaydet", saveNamed: "{name} ayarlarını kaydet", savedDone: "Kaydedildi.",
  testMail: "Deneme e-postası gönder", testMailHint: "Kayıtlı ayarları kullanır ve e-postayı kendi adresinize gönderir.",
  testMailSent: "Adresinize bir deneme e-postası gönderildi. Gelen kutunuza bakın.",
  errors: {
    invalid_setting: "Bu entegrasyonda böyle bir ayar yok.",
    invalid_value: "Bu değer bu ayar için geçerli değil.",
  },
};
