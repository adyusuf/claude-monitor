"use strict";
const { test, expect, openBoard, taskRow } = require("./fixtures.js");

const SLOW = 15000;   // a budget, not a retry: the page polls every 1.5 s, a loaded machine can stall for seconds

test.describe("the board page", () => {
  test("loads with the seeded project, the mode and no error", async ({ page }) => {
    await openBoard(page);
    await expect(page).toHaveTitle("Claude Monitor");
    await expect(page.locator("#projects")).toContainText("alpha");
    await expect(page.locator("#mode")).toHaveText("B");
    await expect(page.locator("#err")).toHaveText("");
  });

  test("shows active work first and hides finished rows until asked", async ({ page }) => {
    await openBoard(page);
    await expect(taskRow(page, "T-3")).toHaveCount(0);                 // done: hidden by default
    const firstId = await page.locator("#tasks tr .tid").first().textContent();
    expect(["T-1", "T-2", "T-4"]).toContain(firstId);                  // running or waiting, not a planned one
    await page.locator("#showDoneTasks").check();
    await expect(page.locator("#tasksPager")).toContainText("45 rows");         // the done row is counted again
    await page.locator("#tasksPager").getByRole("button", { name: /next/ }).click();
    await expect(taskRow(page, "T-3")).toHaveCount(1);                         // finished rows sort last
    await page.locator("#showDoneTasks").uncheck();
    await expect(page.locator("#tasksPager")).toContainText("44 rows");
    await page.locator("#tasksPager").getByRole("button", { name: /next/ }).click();
    await expect(taskRow(page, "T-3")).toHaveCount(0);
  });

  test("pages the tasks by 30 and keeps the previous button off on the first page", async ({ page }) => {
    await openBoard(page);
    const pager = page.locator("#tasksPager");
    await expect(pager).toContainText("Page 1 / 2");
    await expect(page.locator("#tasks tr")).toHaveCount(30);
    await expect(pager.getByRole("button", { name: /previous/ })).toBeDisabled();
    await pager.getByRole("button", { name: /next/ }).click();
    await expect(pager).toContainText("Page 2 / 2");
    await expect(page.locator("#tasks tr")).toHaveCount(14);           // 44 shown (T-3 is done and hidden)
    await expect(pager.getByRole("button", { name: /next/ })).toBeDisabled();
    await pager.getByRole("button", { name: /previous/ }).click();
    await expect(pager).toContainText("Page 1 / 2");
  });

  test("a focused pager button keeps its focus across polls", async ({ page }) => {
    await openBoard(page);
    const next = page.locator("#tasksPager").getByRole("button", { name: /next/ });
    await next.focus();
    for (let polls = 0; polls < 2; polls += 1) await page.waitForResponse((r) => r.url().includes("/api/state"));
    await expect(next).toBeFocused();
  });

  test("a task title is text, never markup", async ({ page }) => {
    const dialogs = [];
    page.on("dialog", (d) => { dialogs.push(d.message()); d.dismiss(); });
    await openBoard(page);
    await expect(taskRow(page, "T-4").locator(".title")).toHaveText('<img src=x onerror="window.__xss=1">');
    await expect(page.locator("#tasks img")).toHaveCount(0);
    expect(await page.evaluate(() => window.__xss)).toBeUndefined();
    expect(dialogs).toEqual([]);
  });

  test("switches language and keeps it", async ({ page }) => {
    await openBoard(page);
    await expect(page.locator("h2").first()).toHaveText("Agents");
    await page.locator("#lang").click();
    await expect(page.locator("html")).toHaveAttribute("lang", "tr");
    await expect(page.locator("h2").first()).toHaveText("Ajanlar");
    await page.reload();
    await expect(page.locator("html")).toHaveAttribute("lang", "tr");   // stored, so it survives a reload
  });

  test("registers its service worker", async ({ page }) => {
    await openBoard(page);
    const active = await page.evaluate(async () => Boolean((await navigator.serviceWorker.ready).active));
    expect(active).toBe(true);
  });

  // The board's service worker passes every request through, and Playwright cannot intercept a request a service
  // worker makes: the page's own error path is exercised with the worker blocked.
  test.describe("without the service worker", () => {
    test.use({ serviceWorkers: "block" });

    test("says so when the server cannot be reached, and recovers", async ({ page }) => {
      await openBoard(page);
      await page.route("**/api/state*", (route) => route.abort());
      await expect(page.locator("#err")).not.toHaveText("", { timeout: SLOW });
      await page.unroute("**/api/state*");
      await expect(page.locator("#err")).toHaveText("", { timeout: SLOW });
    });
  });

  test("every button has a name a screen reader can read", async ({ page }) => {
    await openBoard(page);
    const unnamed = await page.evaluate(() => [...document.querySelectorAll("button")]
      .filter((b) => !(b.getAttribute("aria-label") || b.textContent || b.title || "").trim()).length);
    expect(unnamed).toBe(0);
    await expect(page.locator("nav#projects")).toHaveAttribute("aria-label", "projects");
  });
});
