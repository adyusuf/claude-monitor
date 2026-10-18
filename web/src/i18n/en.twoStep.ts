// The two-step sign-in guide (/two-step): what the code is, and how to set the authenticator app up; en.ts assigns it as `twoStep`.
export const enTwoStep = {
  title: "Authenticator app (two-step sign-in)",
  nav: "Authenticator app", navOff: "not set up",
  intro: "Some actions ask for a 6-digit code: allowing asks, restarting or updating an agent, approving access on production machines. The code comes from an authenticator app on your phone and changes every 30 seconds. You set the app up once, here.",
  stepsTitle: "How to set it up",
  step1: "Install an authenticator app on your phone: Google Authenticator, Microsoft Authenticator, Authy or 1Password. Any of them works the same way.",
  step2: "Press \"Set up\" in the box below. A square QR code appears.",
  step3: "In the app press + (add an account) and choose \"Scan a QR code\", then point the camera at the code on this screen. No camera? Choose \"Enter a setup key\" and type the key shown under the code.",
  step4: "The app now shows a 6-digit code. Type it in the box and press \"Turn on\".",
  step5: "Save the recovery codes that appear. They are shown only once and let you sign in if you lose the phone.",
  after: "From now on, whenever Claude Monitor asks for the code from your authenticator app, open the app and type the 6-digit code it shows at that moment.",
  on: "Two-step sign-in is on for your account.", off: "Two-step sign-in is off. Follow the steps above to turn it on.",
  help: "Where do I get this code?", qrLabel: "QR code for your authenticator app", scan: "Scan this code with your authenticator app:",
  manual: "Cannot scan? Enter this setup key in the app instead:", guide: "Step-by-step guide",
};
