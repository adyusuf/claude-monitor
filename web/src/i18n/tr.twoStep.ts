// İki adımlı giriş rehberi (/two-step): kodun ne olduğu ve doğrulama uygulamasının nasıl kurulacağı; tr.ts bunu `twoStep` olarak atar.
export const trTwoStep = {
  title: "Doğrulama uygulaması (iki adımlı giriş)",
  nav: "Doğrulama uygulaması", navOff: "kurulu değil",
  intro: "Bazı işlemler 6 haneli bir kod ister: sorulara izin vermek, bir ajanı yeniden başlatmak ya da güncellemek, canlı makinelerde erişim onaylamak. Kod, telefonunuzdaki bir doğrulama uygulamasından gelir ve 30 saniyede bir değişir. Uygulamayı burada bir kez kurarsınız.",
  stepsTitle: "Nasıl kurulur",
  step1: "Telefonunuza bir doğrulama uygulaması kurun: Google Authenticator, Microsoft Authenticator, Authy ya da 1Password. Hepsi aynı şekilde çalışır.",
  step2: "Aşağıdaki kutuda \"Kur\" düğmesine basın. Kare bir QR kod çıkar.",
  step3: "Uygulamada + (hesap ekle) düğmesine basıp \"QR kod tara\" deyin, kamerayı ekrandaki koda tutun. Kamera yoksa \"Kurulum anahtarı gir\" deyip kodun altındaki anahtarı yazın.",
  step4: "Uygulama artık 6 haneli bir kod gösterir. Onu kutuya yazıp \"Aç\" düğmesine basın.",
  step5: "Çıkan kurtarma kodlarını saklayın. Yalnızca bir kez gösterilir; telefonu kaybederseniz girişi onlar sağlar.",
  after: "Bundan sonra Podium doğrulama uygulamasından kod istediğinde uygulamayı açın ve o anda gösterdiği 6 haneli kodu yazın.",
  on: "Hesabınızda iki adımlı giriş açık.", off: "İki adımlı giriş kapalı. Açmak için yukarıdaki adımları izleyin.",
  help: "Bu kodu nereden alırım?", qrLabel: "Doğrulama uygulamanız için QR kod", scan: "Bu kodu doğrulama uygulamanızla tarayın:",
  manual: "Tarayamıyor musunuz? Bu kurulum anahtarını uygulamaya elle girin:", guide: "Adım adım rehber",
};
