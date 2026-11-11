import type { enViews } from "./en.views";

export const trViews: typeof enViews = {
  members: {
    tabs: "Üyeler bölümleri", tabMembers: "Üyeler", tabInvitations: "Davetler",
    colMember: "Üye", colRole: "Rol", colJoined: "Katılma", colInvited: "Davet edilen e-posta", colExpires: "Son geçerlilik",
    inviteTitle: "Birini davet et",
  },
  workspace: {
    tabs: "Çalışma alanı ayarları bölümleri", colWhen: "Zaman", colAction: "İşlem", colActor: "Yapan", colTarget: "Hedef", colDetail: "Ayrıntı",
    actorUser: "kullanıcı", actorAgent: "makine", showDetail: "Göster",
  },
  sessions: {
    pick: "Oturumlarını görmek için bir makine seçin.", activeNow: "şu an etkin", waiting: "{n} bekliyor", total: "toplam {n}",
  },
  integrations: {
    colConnection: "Bağlantı", colConnectedBy: "Bağlayan", colProjects: "İçe aktarılan proje", colLastCheck: "Son kontrol", colDetails: "Ayrıntılar",
    openSettings: "Ayarlar", none: "Henüz bağlı bir Jira sitesi yok.",
  },
  machine: {
    tabs: "Makine bölümleri", overview: "Genel bakış", control: "Denetim", work: "İşler", monitoring: "İzleme", access: "Erişim",
  },
};
