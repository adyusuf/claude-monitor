"use strict";
const { test, expect, openBoard, taskRow, taskIds, goToTaskPage } = require("./fixtures.js");
const { PAGE_SIZE } = require("../config.js");

const SLOW = 15000;   // a budget, not a retry: the page polls every 1.5 s, a loaded machine can stall for seconds

test.describe("the board page", () => {
  test("loads with the seeded project, the mode and no error", async ({ page }) => {
    await openBoard(page);
    await expect(page).toHaveTitle("Claude Monitor");
    await expect(page.locator("#projects")).toContainText("alpha");
    await expect(page.locator("#mode")).toHaveText("B");
    await expect(page.locator("#err")).toHaveText("");
  });

  test("lists tasks newest first by date, and hides finished rows until asked", async ({ page }) => {
    await openBoard(page);
    // T-1..T-4 were updated last (T-4 newest); the rest follow by when they were added; T-3 is done and hidden
    const first = await taskIds(page);
    expect(first).toEqual(["T-4", "T-2", "T-1", "T-45", "T-44", "T-43", "T-42", "T-41", "T-40", "T-39"]);
    await page.locator("#showDoneTasks").check();
    await expect(page.locator("#tasksPager")).toContainText("45 rows");         // the done row is counted again
    expect((await taskIds(page)).slice(0, 4)).toEqual(["T-4", "T-3", "T-2", "T-1"]);   // by date, not by status
    await page.locator("#showDoneTasks").uncheck();
    await expect(page.locator("#tasksPager")).toContainText("44 rows");
    await expect(taskRow(page, "T-3")).toHaveCount(0);
  });

  test("lists sessions newest state change first", async ({ page }) => {
    await openBoard(page);
    const ids = await page.locator("#sessions tr").evaluateAll((rows) => rows.map((r) => r.textContent));
    expect(ids).toHaveLength(2);
    expect(ids[0]).toContain("e2e0bbbb");     // the idle one changed state last
    expect(ids[1]).toContain("e2e0aaaa");
  });

  test("pages the tasks by 10 and keeps the previous button off on the first page", async ({ page }) => {
    await openBoard(page);
    const pager = page.locator("#tasksPager");
    await expect(pager).toContainText("Page 1 / 5");
    await expect(page.locator("#tasks tr")).toHaveCount(PAGE_SIZE);
    await expect(pager.getByRole("button", { name: /previous/ })).toBeDisabled();
    await goToTaskPage(page, 5);
    await expect(pager).toContainText("Page 5 / 5");
    await expect(page.locator("#tasks tr")).toHaveCount(4);            // 44 shown rows: 4 pages of 10 and 4 left
    expect(await taskIds(page)).toEqual(["T-8", "T-7", "T-6", "T-5"]);  // the oldest are last
    await expect(pager.getByRole("button", { name: /next/ })).toBeDisabled();
    await pager.getByRole("button", { name: /previous/ }).click();
    await expect(pager).toContainText("Page 4 / 5");
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
