import type { Dictionary } from "./en";

// Çalışma alanı panosu; tr.ts bunu `dashboard` olarak atar.
export const trDashboard: Dictionary["dashboard"] = {
  title: "Pano", intro: "Bu çalışma alanının makineleri ve son {days} günde üzerlerinde olanlar.",
  machines: "Makineler", machinesOnline: "{n} makineden {on} tanesi çevrimiçi", noMachines: "Bu çalışma alanına henüz makine katılmadı.",
  alerts: "Açık alarmlar", projects: "Projeler", projectsActive: "{n} aktif", projectsArchived: "{n} arşivde",
  sprints: "Sprintler", sprintsActive: "Aktif", sprintsPassive: "Pasif", sprintsPassiveHint: "planlanan ya da kapanan",
  cpu: "CPU", memory: "Bellek", disk: "Disk", notMeasured: "ölçülmedi", software: "Kurulu yazılımlar", noSoftware: "Bildirilen yok",
  runs: "Durumlarına göre çalıştırmalar", noRuns: "Bu dönemde çalıştırma yok.", approvals: "İzin istekleri", noApprovals: "Bu dönemde izin isteği yok.",
  approval: { allow: "Bir kişi izin verdi", auto_allowed: "Geçerli bir onay izin verdi", deny: "Reddedildi", expired: "Zamanında cevaplanmadı", open: "Bekliyor" },
  approvedShare: "%{pct} izin verildi", count: "{n}",
};
