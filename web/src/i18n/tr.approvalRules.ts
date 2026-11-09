import type { Dictionary } from "./en";

// Onay bandı, penceresi ve Onaylar sayfası (ADR-0019); tr.ts bunu `approvalRules` olarak atar.
export const trApprovalRules: Dictionary["approvalRules"] = {
  title: "Onaylar",
  intro: "Claude oturumlarının beklediği onaylar, bir süre geçerli bıraktığın onaylar ve son cevaplananlar.",
  bandOne: "1 onay bekliyor", bandMany: "{n} onay bekliyor", bandRuns: "{n} çalıştırma seni bekliyor", details: "Ayrıntılar", dialog: "Bekleyen onaylar",
  waiting: "Bekleyenler", noneWaiting: "Seni bekleyen bir şey yok.",
  rules: "Geçerli onaylar", noRules: "Geçerli bir onayın yok.",
  recent: "Son cevaplananlar", noRecent: "Son zamanlarda cevaplanan bir şey yok.",
  session: "Oturum", machine: "Makine", tool: "Araç", command: "Komut", input: "Girdi", expires: "Bekleme sonu", untitled: "Adsız oturum",
  remember: "Şu süre izin ver", duration: { once: "Yalnızca bu sefer", "5m": "5 dakika", "10m": "10 dakika", "30m": "30 dakika", "1h": "1 saat", always: "Her zaman" },
  rememberHint: "Süre bitene kadar bu makinede, bu projede aynı araç kendiliğinden onaylanır: Bash için yalnızca bu komutun aynısı, dosya için yalnızca bu dosya.",
  rememberHintAlways: "Her zaman, Onaylar sayfasında geri alana ya da çalışma alanının oturumları sakladığı süre boyunca kullanılmayana kadar sürer ve kodunu sorar.",
  strictNoRemember: "Production ya da database makinesi: her istek ayrı onaylanır.",
  noRemember: "Bu istek yalnızca bir kez cevaplanır: bu araç, girdi ya da dosya için geçerli onay olmaz.",
  questionOnSession: "Bir soru: oturum sayfasında cevapla.", openSession: "Oturumu aç",
  allow: "İzin ver", deny: "Reddet", reason: "Gerekçe (isteğe bağlı)",
  ends: "Bitiş", never: "geri alınana kadar", uses: "Kullanım", lastUsed: "Son kullanım", revoke: "Geri al", revokeConfirm: "Bu geçerli onay geri alınsın mı? Bir sonraki istek yine sorulur.",
  answeredBy: "Cevaplayan", byRule: "geçerli bir onay", decision: { allow: "İzin verildi", deny: "Reddedildi" }, when: "Zaman",
  anyInput: "her girdi",
  moreLine: "+1 satır daha", moreLines: "+{n} satır daha",
};
