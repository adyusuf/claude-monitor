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
};
