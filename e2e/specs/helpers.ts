import { expect, type APIRequestContext, type Page } from "@playwright/test";
import { e2e, email } from "../config";

/** The newest mail to an address in Mailpit, and the link in it ("...?token=..."). */
export async function mailLink(request: APIRequestContext, to: string): Promise<string> {
  let link: string | undefined;
  await expect.poll(async () => {
    const search = await request.get(`${e2e.mailpitUrl}/api/v1/search?query=${encodeURIComponent(`to:"${to}"`)}`);
    const messages = (await search.json()).messages as { ID: string }[];
    if (!messages?.length) return false;
    const message = await (await request.get(`${e2e.mailpitUrl}/api/v1/message/${messages[0]!.ID}`)).json();
    link = /https?:\/\/\S+token=\S+/.exec(message.Text as string)?.[0];
    return Boolean(link);
  }, { timeout: 15_000 }).toBe(true);
  return new URL(link!).pathname + new URL(link!).search;
}

/** A new account through the pages: sign up, open the mailed link, sign in. Returns its address. */
export async function signUp(page: Page, tag: string, name = "Örnek Kişi"): Promise<string> {
  const address = email(tag);
  await page.goto("/register");
  await page.getByLabel("Your name").fill(name);
  await page.getByLabel("E-mail").fill(address);
  await page.getByLabel("Password").fill(e2e.password);
  await page.getByRole("button", { name: "Create account" }).click();
  await expect(page.getByText(/Check your mailbox/)).toBeVisible();
  await page.goto(await mailLink(page.request, address));
  await expect(page.getByText(/Your address is confirmed/)).toBeVisible();
  await signIn(page, address);
  return address;
}

export async function signIn(page: Page, address: string) {
  await page.goto("/login");
  await page.getByLabel("E-mail").fill(address);
  await page.getByLabel("Password").fill(e2e.password);
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page.getByRole("heading", { name: "Sessions" })).toBeVisible();
}

/** A stand-in for cm-agent: the same HTTP calls, made from the test. */
export class FakeAgent {
  token = "";
  private seq = 0;

  constructor(private readonly request: APIRequestContext) {}

  private headers() {
    return { Authorization: `Bearer ${this.token}`, "X-Agent-Version": e2e.agentVersion };
  }

  /** Starts the device flow and returns the code a person approves on /device. */
  async start(): Promise<{ deviceCode: string; userCode: string }> {
    const response = await this.request.post("/api/device/code", {
      data: { machineKey: `e2e-machine-${Date.now()}-${Math.random()}`, hostname: "e2e-laptop", os: "macos", arch: "arm64", agentVersion: e2e.agentVersion },
    });
    expect(response.ok()).toBe(true);
    return response.json();
  }

  async finish(deviceCode: string) {
    await expect.poll(async () => {
      const response = await this.request.post("/api/device/token", { data: { deviceCode } });
      if (!response.ok()) return false;
      this.token = (await response.json()).accessToken;
      return true;
    }, { intervals: [1000, 2000, 5000], timeout: 30_000 }).toBe(true);
  }

  async send(session: string, events: { hook?: string; kind?: string; payload: object }[]) {
    const response = await this.request.post("/api/agent/batches", {
      headers: this.headers(),
      data: {
        batchSeq: ++this.seq,
        events: events.map((e) => ({
          harnessKind: "claude_code", sessionExternalId: session, kind: e.kind ?? `hook:${e.hook}`, occurredAt: new Date().toISOString(),
          payload: e.payload, projectKey: "git:example.invalid/acme/widgets", projectName: "widgets", gitBranch: "main",
        })),
      },
    });
    expect(response.ok()).toBe(true);
  }

  async askPermission(session: string, toolName: string, toolInput: object): Promise<string> {
    const response = await this.request.post("/api/agent/permission-requests", {
      headers: this.headers(), data: { harnessKind: "claude_code", sessionExternalId: session, toolName, toolInput, waitSeconds: 120 },
    });
    expect(response.ok()).toBe(true);
    return (await response.json()).id;
  }

  async permission(id: string) {
    return (await this.request.get(`/api/agent/permission-requests/${id}`, { headers: this.headers() })).json();
  }

  async reportCommand(id: string, status: "delivered" | "applied", at?: string) {
    const response = await this.request.post(`/api/agent/commands/${id}/status`, { headers: this.headers(), data: { status, at } });
    expect(response.ok()).toBe(true);
  }
}
