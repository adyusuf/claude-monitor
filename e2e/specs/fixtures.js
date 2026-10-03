"use strict";
const base = require("@playwright/test");
const { LANG_KEY, LANG } = require("../config.js");

// Every spec starts on the board in English (only when no language is stored yet, so a reload keeps the user's choice).
const test = base.test.extend({
  page: async ({ page }, use) => {
    await page.addInitScript(([key, lang]) => {
      try { if (!localStorage.getItem(key)) localStorage.setItem(key, lang); } catch (e) { /* private mode */ }
    },
      [LANG_KEY, LANG]);
    await use(page);
  },
});

/** Load the board and wait until the first state has been drawn into the tasks table. */
async function openBoard(page) {
  await page.goto("/");
  await base.expect(page.locator("#tasks tr").first()).toBeVisible();
}

const taskRow = (page, id) => page.locator("#tasks tr", { has: page.locator(".tid", { hasText: new RegExp(`^${id}$`) }) });

module.exports = { test, expect: base.expect, openBoard, taskRow };
