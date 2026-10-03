import { expect, test } from "@playwright/test";
import { FakeAgent, signUp } from "./helpers";

test("a machine is connected on /device and its session appears live, then takes a prompt and a permission answer", async ({ page, request }) => {
  await signUp(page, "agent");
  const sessionsUrl = page.url();

  const agent = new FakeAgent(request);
  const code = await agent.start();
  await page.goto(`/device?code=${code.userCode}`);
  await expect(page.getByText(/e2e-laptop · macos arm64/)).toBeVisible();
  await page.getByRole("button", { name: "Connect this machine" }).click();
  await expect(page.getByText(/The machine is connected/)).toBeVisible();
  await agent.finish(code.deviceCode);

  // The session arrives while the list is open: the live stream shows it without a reload.
  await page.goto(sessionsUrl);
  await expect(page.getByText("No sessions yet.")).toBeVisible();
  const session = `e2e-${Date.now()}`;
  await agent.send(session, [
    { hook: "SessionStart", payload: { session_id: session, model: "claude-opus-5-5" } },
    { hook: "UserPromptSubmit", payload: { session_id: session, prompt: "Make the login faster" } },
    { hook: "PostToolUse", payload: { session_id: session, tool_name: "TaskCreate", tool_input: { subject: "Profile the query" }, tool_response: { task: { id: "1" } } } },
  ]);
  await expect(page.getByText("Make the login faster")).toBeVisible();
  await expect(page.locator(".pill-active")).toBeVisible();

  await page.getByText("Make the login faster").click();
  await expect(page.getByText("Profile the query")).toBeVisible();
  await expect(page.getByText("widgets").first()).toBeVisible();

  // A prompt from the web: queued, then reported applied by the agent, shown live.
  await page.getByPlaceholder(/A message for Claude/).fill("Also add a test");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Sent.")).toBeVisible();
  await expect(page.getByText("Queued")).toBeVisible();
  const commands = await (await page.request.get(page.url().replace(/\/w\/[^/]+\/sessions\//, "/api/sessions/") + "/commands")).json();
  await agent.reportCommand(commands[0].id, "applied");
  await expect(page.getByText("Applied")).toBeVisible();

  // A permission request: it shows up live, the owner allows it, the agent reads the answer.
  const permission = await agent.askPermission(session, "Bash", { command: "npm test" });
  await expect(page.getByText("Waiting for permission")).toBeVisible();
  await page.getByRole("button", { name: "Allow" }).click();
  await expect(page.getByText("Waiting for permission")).toBeHidden();
  const answered = await agent.permission(permission);
  expect(answered.status).toBe("answered");
  expect(answered.answer.decision).toBe("allow");

  // The machine is listed, and disconnecting it stops the agent's token.
  await page.getByRole("link", { name: /Machines/ }).click();
  await expect(page.getByText("e2e-laptop")).toBeVisible();
  page.once("dialog", (d) => void d.accept());
  await page.getByRole("button", { name: "Disconnect" }).click();
  await expect(page.getByText("Disconnected")).toBeVisible();
  const refused = await request.post("/api/agent/heartbeat", { headers: { Authorization: `Bearer ${agent.token}`, "X-Agent-Version": "0.3.0" } });
  expect(refused.status()).toBe(401);
});
