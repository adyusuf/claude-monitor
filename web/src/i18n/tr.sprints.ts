import type { Dictionary } from "./en";

// Turkish sprints namespace: the same keys as en.sprints.ts (the type makes a missing key a build error).
export const trSprints: Dictionary["sprints"] = {
  title: "Sprintler", sprint: "Sprint {n}", sprintNamed: "Sprint {n} · {name}", backToSprints: "Sprintler", new: "Yeni sprint", create: "Sprint oluştur",
  startSprint: "Sprint başlat", activeSprints: "Aktif sprintler",
  filter: "Hangi sprintler", all: "Tümü", forApplication: "Uygulama: {name}",
  status: { planned: "Planlandı", active: "Aktif", closed: "Kapandı" },
  empty: "Henüz sprint yok.", emptyFiltered: "Bu durumda sprint yok.",
  emptyHint: "Sonraki çalışma dilimini planlayın: bir sprint oluşturun, sonra iş listesinden iş ekleyin.",
  name: "Ad", goal: "Hedef", startDate: "Başlangıç tarihi", endDate: "Bitiş tarihi", capacity: "Kapasite", capacityIn: "Kapasite ({unit})",
  release: "Sürüm", noRelease: "Sürüm yok", team: "Ekip", noTeam: "Ekipte kimse yok.", releaseIs: "Sürüm: {name}",
  dates: "{start} – {end}", datesLabel: "Tarihler", issuesLabel: "İşler", noDates: "Henüz tarih yok", issueCount: "İş sayısı: {n}", goalLabel: "Hedef",
  barLabel: "Kapasiteye göre taahhüt edilen tahminler", committedOf: "{capacity} {unit} kapasitenin {committed} kadarı taahhüt edildi",
  committed: "Taahhüt", done: "Biten", estimateIn: "{n} {unit}",
  start: "Başlat", delete: "Sil", deleteConfirm: "Sprint {n} silinsin mi? İşleri iş listesine döner.",
  edit: "Düzenle", editTitle: "Sprinti düzenle", close: "Sprinti kapat",
  closedBanner: "Bu sprint kapandı: yalnızca okunabilir.",
  closeTitle: "Bu sprinti kapat", closeQuestion: "{n} iş bitmedi. Nereye gitsinler?", closeNone: "Tüm işler bitti.",
  moveTo: "Bitmeyen işlerin gideceği yer", toBacklog: "İş listesine geri", confirmClose: "Sprinti kapat",
  boardTitle: "Sprintin işleri", noIssues: "Bu sprintte henüz iş yok.", noneInColumn: "Yok.",
  statusOf: "{mark} işinin durumu", remove: "Çıkar", removeFrom: "{mark} işini sprintten çıkar",
  addTitle: "İş ekle", candidates: "İş listesindeki işler", noCandidates: "İş listesinde bu sprint için başka iş kalmadı.",
  selectIssue: "{mark} işini seç", addSelected: "Seçilenleri ekle ({n})", createAndAdd: "Oluştur ve ekle", createTitle: "Yeni iş oluştur ve ekle",
  inSprint: "Bir sprintte", teamOf: "Sprintin ekibi",
};
