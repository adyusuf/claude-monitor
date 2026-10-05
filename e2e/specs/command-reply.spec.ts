import { expect, test } from "@playwright/test";
import { FakeAgent, signUp } from "./helpers";

test("a command sent from the web shows Claude's answer under it, live, without going to the desktop", async ({ page, request }) => {
  await signUp(page, "reply");
  const agent = new FakeAgent(request);
  const code = await agent.start();
  await page.goto(`/device?code=${code.userCode}`);
  await page.getByRole("button", { name: "Connect this machine" }).click();
  await expect(page.getByText(/The machine is connected/)).toBeVisible();
  await agent.finish(code.deviceCode);

  const session = `e2e-reply-${Date.now()}`;
  await agent.send(session, [{ hook: "SessionStart", payload: { session_id: session, model: "claude-opus-5-5" } }]);
  await page.getByRole("link", { name: /Sessions/ }).first().click();
  await page.locator("a", { hasText: "e2e-reply" }).or(page.locator("tr, li").filter({ hasText: "widgets" }).first()).first().click();

  await page.getByPlaceholder(/A message for Claude/).fill("Run the tests");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText("Sent.")).toBeVisible();
  const commands = await (await page.request.get(page.url().replace(/\/w\/[^/]+\/sessions\//, "/api/sessions/") + "/commands")).json();
  const handedOver = new Date().toISOString(); // what the hook saw; the report below comes a moment later
  await agent.reportCommand(commands[0].id, "applied", handedOver);

  // Applied is not answered: the command says it waits for the reply.
  const row = page.locator(".commands li").filter({ hasText: "Run the tests" });
  await expect(row.getByText("Applied")).toBeVisible();
  await expect(row.getByText("Waiting for Claude's reply")).toBeVisible();

  // The answer is recorded by the agent's transcript tailer: it appears under the command live.
  const long = `All 12 tests pass. ${"The suite covers the parser, the writer and the CLI. ".repeat(8)}THE-END-OF-THE-ANSWER`;
  await agent.send(session, [{ kind: "transcript", payload: { type: "assistant", timestamp: new Date().toISOString(), message: { content: [{ type: "text", text: long }] } } }]);
  await expect(row.getByText("Claude replied")).toBeVisible();
  await expect(row.getByText(/^All 12 tests pass\./)).toBeVisible();
  await expect(row.getByText("Waiting for Claude's reply")).toBeHidden();
  await expect(row.getByText(/THE-END-OF-THE-ANSWER/)).toBeHidden(); // only the start is shown

  // The whole answer on request, and folded back.
  await row.getByRole("button", { name: "See the whole reply" }).click();
  await expect(row.getByText(/THE-END-OF-THE-ANSWER/)).toBeVisible();
  await row.getByRole("button", { name: "Show less" }).click();
  await expect(row.getByText(/THE-END-OF-THE-ANSWER/)).toBeHidden();
});
