"use strict";
// The "Ask status" button on a waiting or unclear task (scripts/board/board_ui_tasks.js askHtml,
// board_ui.js onClick; docs/live-board.md §2i). Run: node --test scripts/tests/*.test.js — measured
// by scripts/coverage.sh (#29).
const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");
const { ui, tr, en, NOW, ago, state, SHOW_ALL, fakePage, target } = require("./board_ui_fixtures.js");
const tasks = require(path.join(__dirname, "..", "board", "board_ui_tasks.js"));

const task = (id, status, sessions) => ({ id, title: `title-${id}`, branch: "", role: "", note: "", status, agents: [], sessions });
const SPAN = (last) => ({ first: ago(500), last: ago(last) });
const hit = (selector, el) => target({ closest: (sel) => (sel === selector ? el : null) });
const posts = (page) => page.calls.filter(([url]) => url === "/api/control").map(([, o]) => JSON.parse(o.body));

test("the session asked is the one that touched the task last; none when no session is linked", () => {
  assert.equal(tasks.askSession(task("T-1", "waiting", { old: SPAN(300), fresh: SPAN(5), mid: SPAN(60) })), "fresh");
  assert.equal(tasks.askSession(task("T-1", "waiting", {})), null);
  assert.equal(tasks.askSession({ id: "T-1", status: "waiting" }), null);
  assert.equal(tasks.askSession(task("T-1", "waiting", { a: null, b: SPAN(1) })), "b");
});

test("the button shows for waiting, handed-back and unknown statuses, and for no other", () => {
  const sessions = { s1: SPAN(1) };
  for (const status of ["waiting", "agent_done", "brand_new_status"]) {
    assert.match(tasks.askHtml(task("T-1", status, sessions), tr), /data-ask-task="T-1" data-ask-session="s1"/, status);
  }
  for (const status of ["planned", "running", "done", "failed", "removed", "needs_decision"]) {
    assert.equal(tasks.askHtml(task("T-1", status, sessions), tr), "", status);
  }
});

test("with no linked session the button is there but disabled, and says why", () => {
  const html = tasks.askHtml(task("T-1", "waiting", {}), en);
  assert.match(html, /disabled/);
  assert.match(html, /No session is linked to this task/);
  assert.doesNotMatch(html, /data-ask-task/);
});

test("the button is in the task row, with the text in both languages", () => {
  const st = state({ tasks: { "T-1": task("T-1", "waiting", { s1: SPAN(1) }) } });
  assert.match(ui.view(st, tr, NOW, { lists: SHOW_ALL }).tasksHtml, />Durumu sor<\/button>/);
  assert.match(ui.view(st, en, NOW, { lists: SHOW_ALL }).tasksHtml, />Ask status<\/button>/);
});

test("clicking it queues one message for that session, naming the task and the command", async () => {
  const st = state({ tasks: { "T-7": task("T-7", "agent_done", { s1: SPAN(1) }) } });
  const page = fakePage({ server: { state: st } });
  await page.app.first;
  await page.click(hit("[data-ask-task]", { dataset: { askTask: "T-7", askSession: "s1" } }));
  const sent = posts(page);
  assert.equal(sent.length, 1);
  assert.equal(sent[0].action, "queue_task");
  assert.equal(sent[0].value, "s1");
  assert.equal(sent[0].project, "aaaaaaaaaa");
  assert.match(sent[0].text, /T-7/);
  assert.match(sent[0].text, /board\.py set T-7 --status/);
  assert.ok(sent[0].text.length < 1000);
});

test("a refused question shows the server's reason", async () => {
  const st = state({ tasks: { "T-7": task("T-7", "waiting", { s1: SPAN(1) }) } });
  const control = { ok: false, json: async () => ({ error: "unknown session" }) };
  const page = fakePage({ server: { state: st, control } });
  await page.app.first;
  await page.click(hit("[data-ask-task]", { dataset: { askTask: "T-7", askSession: "s1" } }));
  assert.equal(page.els.err.textContent, "unknown session");
});
