// The e2e suite's one configuration module (global #2): where the app and its mail catcher are, and test addresses.
const base = process.env.E2E_BASE_URL?.replace(/\/$/, "");
const port = process.env.E2E_PORT ?? "5190";

export const e2e = {
  local: !base,
  baseUrl: base ?? `http://localhost:${port}`,
  mailpitUrl: process.env.E2E_MAILPIT_URL ?? "http://127.0.0.1:8125",
  agentVersion: "0.3.0",
  password: "e2e password long enough",
};

/** Test addresses (global #30): <account>+<tag>@<domain> from E2E_EMAIL_BASE, unique per call. */
export function email(tag: string): string {
  const configured = process.env.E2E_EMAIL_BASE ?? "monitor.e2e@gmail.com";
  const [account, domain] = configured.split("@");
  return `${account}+${tag}-${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}@${domain}`;
}
