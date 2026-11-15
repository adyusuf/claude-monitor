import type { Dictionary } from "./en";
import type { enApprovalRulesErrors } from "./en.approvalRules";

// Onay bandı, penceresi ve Onaylar sayfası (ADR-0019); tr.ts bunu `approvalRules` olarak atar.
export const trApprovalRules: Dictionary["approvalRules"] = {
  title: "Onaylar",
  intro: "Claude oturumlarının beklediği onaylar, bir süre geçerli bıraktığın onaylar ve son cevaplananlar.",
  bandOne: "1 onay bekliyor", bandMany: "{n} onay bekliyor", bandRuns: "{n} çalıştırma seni bekliyor", details: "Ayrıntılar", dialog: "Bekleyen onaylar",
  waiting: "Bekleyenler", noneWaiting: "Seni bekleyen bir şey yok.",
  rules: "Geçerli onaylar", noRules: "Geçerli bir onayın yok.",
  recent: "Son cevaplananlar", noRecent: "Son zamanlarda cevaplanan bir şey yok.",
  session: "Oturum", machine: "Makine", tool: "Araç", command: "Komut", input: "Girdi", expires: "Bekleme sonu", expired: "Süre doldu: bu istek artık makinede soruluyor.", untitled: "Adsız oturum",
  remember: "Şu süre izin ver", duration: { once: "Yalnızca bu sefer", "5m": "5 dakika", "10m": "10 dakika", "30m": "30 dakika", "1h": "1 saat", always: "Her zaman" },
  rememberHint: "Süre bitene kadar bu makinede, bu projede aynı araç kendiliğinden onaylanır: Bash için yalnızca bu komutun aynısı, dosya için yalnızca bu dosya.",
  rememberHintAlways: "Her zaman, Onaylar sayfasında geri alana ya da çalışma alanının oturumları sakladığı süre boyunca kullanılmayana kadar sürer ve kodunu sorar.",
  strictNoRemember: "Production ya da database makinesi: her istek ayrı onaylanır.",
  noRemember: "Bu istek yalnızca bir kez cevaplanır: bu araç, girdi ya da dosya için geçerli onay olmaz.",
  questionOnSession: "Bir soru: oturum sayfasında cevapla.", openSession: "Oturumu aç",
  allow: "İzin ver", deny: "Reddet", reason: "Gerekçe (isteğe bağlı)",
  ends: "Bitiş", never: "geri alınana kadar", uses: "Kullanım", lastUsed: "Son kullanım", neverUsed: "hiç", revoke: "Geri al", revokeConfirm: "Bu geçerli onay geri alınsın mı? Bir sonraki istek yine sorulur.",
  answeredBy: "Cevaplayan", byRule: "geçerli bir onay", decision: { allow: "İzin verildi", deny: "Reddedildi" }, when: "Zaman",
  anyInput: "her girdi",
  moreLine: "+1 satır daha", moreLines: "+{n} satır daha",
};

/** İzin cevabının ve geçerli onayların kodları (ADR-0019), `errors.<code>` olarak; tr.ts bunu `errors` içine yayar. */
export const trApprovalRulesErrors: typeof enApprovalRulesErrors = {
  rule_limit: "Sahip olabileceğiniz en çok geçerli onaya (200) zaten sahipsiniz. Onaylar sayfasında birini geri alın ya da yalnızca bu seferlik izin verin.",
  rule_not_allowed: "Bu istek şu anda geçerli bir onay alamaz (makine üretim ya da veritabanı olmuş olabilir). Yalnızca bu seferlik izin verin.",
};
