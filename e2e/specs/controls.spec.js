"use strict";
const { test, expect, openBoard, taskRow } = require("./fixtures.js");

// These change the shared seeded board, each on rows of its own (T-2 and S_IDLE; T-5).

test.describe("the page's controls", () => {
  test("Ask status shows only on a task whose state the board cannot read", async ({ page }) => {
    await openBoard(page);
    await expect(taskRow(page, "T-2").getByRole("button", { name: "Ask status" })).toBeVisible();
    await expect(taskRow(page, "T-1").getByRole("button", { name: "Ask status" })).toHaveCount(0);   // running
    await expect(taskRow(page, "T-4").getByRole("button", { name: "Ask status" })).toHaveCount(0);   // running
  });

  test("Ask status queues one message for the session that touched the task, and shows it queued", async ({ page }) => {
    await openBoard(page);
    const queuedFor = async () => {            // what the board holds for S_IDLE: the run may be repeated on one server
      const projects = await (await page.request.get("/api/projects")).json();
      const state = await (await page.request.get(`/api/state?p=${projects[0].id}`)).json();
      return state.costs.sessions.find((x) => x.id === "e2e0bbbb-2222").queued;
    };
    const before = (await queuedFor()).length;
    const sent = page.waitForRequest((r) => r.url().endsWith("/api/control") && r.method() === "POST");
    await taskRow(page, "T-2").getByRole("button", { name: "Ask status" }).click();
    const body = (await sent).postDataJSON();
    expect(body.action).toBe("queue_task");
    expect(body.value).toBe("e2e0bbbb-2222");                       // S_IDLE, the session that touched T-2
    expect(body.text).toContain("T-2");
    expect(body.text).toContain("board.py set T-2 --status");
    await expect(page.locator("#err")).toHaveText("");
    await expect(page.locator("#sessions")).toContainText(`queued: ${before + 1}`);
    // the page shows a count; the text itself is what the session will be given
    const queued = await queuedFor();
    expect(queued).toHaveLength(before + 1);
    expect(queued[queued.length - 1]).toContain("task T-2");
  });

  test("removing a task takes it off the list and Restore brings it back", async ({ page }) => {
    await openBoard(page);
    await page.locator("#tasksPager").getByRole("button", { name: /next/ }).click();   // T-5 is on page 2
    await expect(taskRow(page, "T-5")).toHaveCount(1);
    await taskRow(page, "T-5").getByRole("button", { name: "Remove" }).click();
    await expect(taskRow(page, "T-5")).toHaveCount(0);
    await page.locator("#showDoneTasks").check();
    await page.locator("#tasksPager").getByRole("button", { name: /next/ }).click();   // removed rows sort last
    const removed = taskRow(page, "T-5");
    await expect(removed).toHaveCount(1);
    await removed.getByRole("button", { name: "Restore" }).click();
    await page.locator("#showDoneTasks").uncheck();
    await expect(page.locator("#tasksPager")).toContainText("44 rows");               // back among the live ones
    await expect(page.locator("#err")).toHaveText("");
  });

  test.describe("a refusal", () => {
    test.use({ serviceWorkers: "block" });   // so that the route below can see the page's request

    test("a queued message that the server refuses shows the reason and keeps the page usable", async ({ page }) => {
      await openBoard(page);
      await page.route("**/api/control", (route) => route.fulfill({
        status: 400, contentType: "application/json", body: JSON.stringify({ error: "unknown session" }) }));
      await taskRow(page, "T-2").getByRole("button", { name: "Ask status" }).click();
      await expect(page.locator("#err")).toHaveText("unknown session");
      await page.unroute("**/api/control");
      await expect(page.locator("#tasks tr").first()).toBeVisible();
    });
  });
});
