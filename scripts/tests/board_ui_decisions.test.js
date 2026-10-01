"use strict";
// Decisions on the live board page (docs/live-board.md): the choices Claude
// offers, the note, the click that answers, and a table that is not redrawn under the
// user's cursor while a note is being typed.
const test = require("node:test");
const assert = require("node:assert/strict");
const { ui, tr, en, state, fakePage, target } = require("./board_ui_fixtures.js");

const DEFAULTS = ["continue", "reject"];
const ask = (over = {}) => ({ id: "T-3", title: "split?", branch: "", role: "", note: "one PR or two?",
  status: "needs_decision", agents: [], options: [], decision: null, ...over });

test("without options the server's defaults are offered, labelled in the page language", () => {
  const html = ui.decisionHtml(ask(), DEFAULTS, tr);
  assert.match(html, /data-decide-task="T-3"/);
  assert.match(html, /data-choice="continue">Devam</);
  assert.match(html, /data-choice="reject">Reddet</);
  assert.match(html, /data-note-for="T-3" maxlength="500" placeholder="not \(isteğe bağlı\)"/);
  assert.match(ui.decisionHtml(ask(), DEFAULTS, en), />Continue</);
});

test("Claude's own options are offered as written, and escaped", () => {
  const html = ui.decisionHtml(ask({ options: ["split", "<keep>"] }), DEFAULTS, tr);
  assert.match(html, /data-choice="split">split</);
  assert.match(html, /data-choice="&lt;keep&gt;">&lt;keep&gt;</);
  assert.doesNotMatch(html, /Devam/);
});

test("an answered question shows the answer instead of the buttons; other tasks show nothing", () => {
  const html = ui.decisionHtml(ask({ decision: { choice: "split", note: "tests first" } }), DEFAULTS, tr);
  assert.match(html, /Karar: <b>split<\/b> — tests first/);
  assert.doesNotMatch(html, /data-choice/);
  assert.equal(ui.decisionHtml(ask({ status: "waiting" }), DEFAULTS, tr), "");
});

test("the view counts open questions and renders them in the task table", () => {
  const v = ui.view(state({ tasks: { "T-3": ask() }, decision_defaults: DEFAULTS }), tr);
  assert.match(v.statsHtml, /<div class="n">1<\/div><div class="l">Karar bekliyor/);
  assert.match(v.tasksHtml, /data-choice="continue"/);
});

test("a click answers with the choice and the typed note, for the selected project", async () => {
  const page = fakePage({ notes: { '[data-note-for="T-3"]': "tests first" } });
  await page.app.first;
  const box = { dataset: { decideTask: "T-3" } };
  const button = { dataset: { choice: "continue" }, closest: () => box };
  await page.click(target({ closest: (sel) => (sel === "[data-choice]" ? button : null) }));
  const post = page.calls.find(([url]) => url === "/api/control");
  assert.deepEqual(JSON.parse(post[1].body),
    { action: "decide", value: "T-3", project: "aaaaaaaaaa", choice: "continue", note: "tests first" });
});

test("a click without a note field sends an empty note", async () => {
  const page = fakePage();
  await page.app.first;
  const button = { dataset: { choice: "reject" }, closest: () => ({ dataset: { decideTask: "T-9" } }) };
  await page.click(target({ closest: (sel) => (sel === "[data-choice]" ? button : null) }));
  const post = page.calls.find(([url]) => url === "/api/control");
  assert.equal(JSON.parse(post[1].body).note, "");
});

test("while a note is being typed the task table is not redrawn", async () => {
  const typing = fakePage({ active: { dataset: { noteFor: "T-3" } } });
  await typing.app.first;
  // The fake creates an element on first access: an untouched table was never even looked up.
  assert.ok(!typing.els.tasks || typing.els.tasks.innerHTML === "");
  const idle = fakePage();
  await idle.app.first;
  assert.notEqual(idle.els.tasks.innerHTML, "");
});
