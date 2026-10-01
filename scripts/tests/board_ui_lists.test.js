"use strict";
// The board's table lists (scripts/board/board_ui_lists.js, docs/live-board.md §2h): ordering,
// finished rows hidden by default, 30-row pages, and the click / checkbox flow through start().
// Run: node --test scripts/tests/*.test.js  — measured by scripts/coverage.sh (#29).
const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");
const { ui, tr, en, NOW, ago, state, SHOW_ALL, fakePage, target } = require("./board_ui_fixtures.js");
const lists = require(path.join(__dirname, "..", "board", "board_ui_lists.js"));

const task = (n, status, over = {}) => ({ id: `T-${n}`, title: `title-${n}`, branch: "", role: "", note: "",
  status, agents: [], updated: ago(1000 - n), ...over });
const agent = (n, status, over = {}) => ({ key: `k${n}`, type: "analyst", task: "T-1", status,
  started: ago(1000 - n), ended: null, description: `desc-${n}`, reason: null, ...over });
const session = (n, st, over = {}) => ({ id: `s${String(n).padStart(7, "0")}-aaaa`, state: st, since: ago(1000 - n),
  last: ago(1), has_transcript: true, orchestration: null, agents: { cost: null }, subagents: 0, total: null,
  rate_per_h: null, context: null, context_warn: false, projected: null, queued: [], ...over });
const byKey = (list, key) => Object.fromEntries(list.map((x) => [x[key], x]));
const costs = (sessions = []) => ({ sessions, tasks: {}, basis: { remaining_h: 0, eta_tasks: 0, no_eta_tasks: 0, window_s: 3600 },
  agent_rows: { rows: {}, by_type: {}, unmeasured: 0, total: null } });
const ids = (list) => list.map((x) => x.id);
const rows = (html) => html.match(/<tr[\s\S]*?<\/tr>/g) || [];
const hit = (selector, el) => target({ closest: (sel) => (sel === selector ? el : null) });
const pagerBtn = (key, dir) => hit("[data-page]", { dataset: { page: key, dir: String(dir) } });
const many = (n, status, make = task) => Array.from({ length: n }, (_, i) => make(i + 1, status));

test("tasks: needs a decision, running, waiting, planned, unknown, done, removed - newest first inside a group", () => {
  const list = [task(1, "done"), task(2, "planned"), task(3, "removed"), task(4, "running"), task(5, "agent_done"),
    task(6, "needs_decision"), task(7, "brand_new_status"), task(8, "running", { updated: ago(5) }), task(9, "waiting")];
  assert.deepEqual(ids(lists.orderTasks(list)),
    ["T-6", "T-8", "T-4", "T-9", "T-5", "T-2", "T-7", "T-1", "T-3"]);
});

test("tasks: with no stamp the start time orders them, and the higher id wins a tie", () => {
  const list = [task(1, "running", { updated: null, started: ago(50) }), task(2, "running", { updated: null, started: ago(10) }),
    task(10, "planned", { updated: null }), task(9, "planned", { updated: null })];
  assert.deepEqual(ids(lists.orderTasks(list)), ["T-2", "T-1", "T-10", "T-9"]);
});

test("agents: running or starting, then failed / denied / unknown, then done - newest first", () => {
  const list = [agent(1, "done"), agent(2, "denied"), agent(3, "running"), agent(4, "starting"), agent(5, "failed"), agent(6, "done")];
  assert.deepEqual(lists.orderAgents(list).map((a) => a.key), ["k4", "k3", "k5", "k2", "k6", "k1"]);
});

test("sessions: busy, then unknown, then idle - newest first", () => {
  const list = [session(1, "idle"), session(2, "busy"), session(3, "unknown"), session(4, "idle"), session(5, "busy"), session(6, "odd")];
  assert.deepEqual(lists.orderSessions(list).map((s) => s.state), ["busy", "busy", "odd", "unknown", "idle", "idle"]);
  assert.equal(lists.orderSessions(list)[0].id.slice(0, 8), "s0000005");
});

test("ordering returns a copy and leaves the input alone", () => {
  const list = [task(1, "done"), task(2, "running")];
  const ordered = lists.orderTasks(list);
  assert.notEqual(ordered, list);
  assert.deepEqual(ids(list), ["T-1", "T-2"]);
});

test("paginate: 30 rows are one page, 31 are two, 65 are three with 5 on the last", () => {
  const of = (n) => lists.paginate(Array.from({ length: n }, (_, i) => i), 1);
  assert.deepEqual([of(0).pages, of(30).pages, of(31).pages, of(65).pages], [1, 1, 2, 3]);
  assert.equal(of(30).rows.length, 30);
  const last = lists.paginate(Array.from({ length: 65 }, (_, i) => i), 3);
  assert.deepEqual([last.rows.length, last.rows[0], last.page, last.total], [5, 60, 3, 65]);
});

test("paginate: a page outside the range, or not a number, is held inside it", () => {
  const list = Array.from({ length: 65 }, (_, i) => i);
  assert.equal(lists.paginate(list, 99).page, 3);
  assert.equal(lists.paginate(list, 0).page, 1);
  assert.equal(lists.paginate(list, -4).page, 1);
  assert.equal(lists.paginate(list, "x").page, 1);
  assert.equal(lists.paginate(list, 2.7).page, 2);
  assert.deepEqual(lists.paginate([], 5), { rows: [], page: 1, pages: 1, total: 0 });
});

test("select: finished rows are hidden and counted until the toggle is on", () => {
  const list = [task(1, "done"), task(2, "removed"), task(3, "running")];
  const opts = (showDone) => ({ order: lists.orderTasks, finished: lists.isFinishedTask, state: { page: 1, showDone } });
  const hidden = lists.select(list, opts(false));
  assert.deepEqual([ids(hidden.rows), hidden.hidden, hidden.all], [["T-3"], 2, 3]);
  const shown = lists.select(list, opts(true));
  assert.deepEqual([ids(shown.rows), shown.hidden], [["T-3", "T-1", "T-2"], 0]);
  const noFilter = lists.select(list, { order: lists.orderTasks, state: { page: 1 } });
  assert.equal(noFilter.rows.length, 3);
  assert.equal(lists.isFinishedAgent({ status: "done" }) && !lists.isFinishedAgent({ status: "failed" }), true);
});

test("pagerHtml: empty for one page with nothing hidden; the hidden count alone; buttons at the ends", () => {
  const v = (over) => ({ page: 1, pages: 1, total: 5, hidden: 0, ...over });
  assert.equal(lists.pagerHtml(tr, "tasks", v()), "");
  assert.match(lists.pagerHtml(tr, "tasks", v({ hidden: 4 })), /^<span class="muted">4 biten gizli<\/span>$/);
  const first = lists.pagerHtml(en, "agents", v({ pages: 3, total: 65 }));
  assert.match(first, /data-page="agents" data-dir="-1" disabled/);
  assert.match(first, /data-dir="1">next/);
  assert.match(first, /Page 1 \/ 3 · 65 rows/);
  const last = lists.pagerHtml(en, "agents", v({ page: 3, pages: 3, total: 65, hidden: 2 }));
  assert.match(last, /data-dir="1" disabled/);
  assert.match(last, /2 finished hidden/);
});

test("makeLists: turning, the toggle, the first page, reset and remembering a clamped page", () => {
  const l = lists.makeLists();
  l.turn("tasks", 1); l.turn("tasks", 1); l.turn("tasks", "-1");
  assert.equal(l.state().tasks.page, 2);
  l.turn("tasks", -9);
  assert.equal(l.state().tasks.page, 1);
  l.turn("agents", 1);
  l.showDone("agents", true);
  assert.deepEqual(l.state().agents, { page: 1, showDone: true });
  l.turn("sessions", 1); l.firstPage("sessions");
  assert.equal(l.state().sessions.page, 1);
  l.turn("tasks", 1); l.remember({ tasks: 4, agents: 1, sessions: 1 });
  assert.equal(l.state().tasks.page, 4);
  l.remember(null);
  l.turn("nope", 1); l.showDone("nope", true); l.firstPage("nope");  // an unknown table is ignored
  l.reset();
  assert.deepEqual(l.state().tasks, { page: 1, showDone: false });
});

test("view: done and removed tasks are hidden by default, but the stats and the banner still count them", () => {
  const tasks = byKey([task(1, "done"), task(2, "removed"), task(3, "running")], "id");
  const v = ui.view(state({ tasks }), tr, NOW);
  assert.equal(rows(v.tasksHtml).length, 1);
  assert.match(v.tasksHtml, /title-3/);
  assert.match(v.statsHtml, /<div class="n">1<\/div><div class="l">Bitti/);
  assert.match(v.pagers.tasks, /2 biten gizli/);
  assert.equal(rows(ui.view(state({ tasks }), tr, NOW, { lists: SHOW_ALL }).tasksHtml).length, 3);
  const finished = byKey([task(1, "done"), task(2, "removed")], "id");
  const all = ui.view(state({ tasks: finished }), tr, NOW);
  assert.equal(all.allDone, true);
  assert.match(all.tasksHtml, /Yalnızca biten satırlar var \(2\)/);
  assert.match(ui.view(state({ tasks: finished }), en, NOW).tasksHtml, /Only finished rows exist \(2\)/);
});

test("view: tasks come in pages of 30, active first, and a page past the end is clamped", () => {
  const tasks = byKey([...many(40, "running"), ...many(30, "planned").map((x) => ({ ...x, id: `T-${100 + Number(x.id.slice(2))}` }))], "id");
  const st = state({ tasks });
  const first = ui.view(st, tr, NOW);
  assert.equal(rows(first.tasksHtml).length, 30);
  assert.match(rows(first.tasksHtml)[0], /T-40/);             // newest running first
  assert.equal(first.pages.tasks, 1);
  const third = ui.view(st, tr, NOW, { lists: { ...SHOW_ALL, tasks: { page: 3, showDone: false } } });
  assert.equal(rows(third.tasksHtml).length, 10);
  assert.match(third.tasksHtml, /title-1</);                  // the planned ones come after the 40 running
  assert.doesNotMatch(third.tasksHtml, /title-30</);
  const past = ui.view(st, tr, NOW, { lists: { ...SHOW_ALL, tasks: { page: 9, showDone: false } } });
  assert.equal(past.pages.tasks, 3);
});

test("view: agents hide the done ones by default; the total row and the notes stay", () => {
  const agents = byKey([agent(1, "done"), agent(2, "running"), agent(3, "denied")], "key");
  const st = state({ agents, costs: { ...costs(), agent_rows: { rows: {}, by_type: {}, unmeasured: 0, total: null } } });
  const v = ui.view(st, tr, NOW);
  assert.deepEqual(rows(v.agentsHtml).map((r) => /desc-(\d)/.exec(r)[1]), ["2", "3"]);
  assert.match(v.pagers.agents, /1 biten gizli/);
  const done = ui.view(state({ agents: byKey([agent(1, "done")], "key"), costs: costs() }), en, NOW);
  assert.match(done.agentsHtml, /Only finished rows exist \(1\)/);
  assert.notEqual(v.agentsHelp, "");
});

test("view: sessions are ordered, paged and filtered by the channel checkbox", () => {
  const list = [...Array.from({ length: 32 }, (_, i) => session(i + 1, "idle", { channel: i % 2 === 0 })), session(40, "busy")];
  const st = state({ costs: costs(list) });
  const v = ui.view(st, tr, NOW);
  assert.equal(rows(v.sessionsHtml).length, 30);
  assert.match(rows(v.sessionsHtml)[0], /s0000040/);          // the busy one is first
  assert.equal(v.pages.sessions, 1);
  assert.match(v.pagers.sessions, /Sayfa 1 \/ 2 · 33 satır/);
  const second = ui.view(st, tr, NOW, { lists: { ...SHOW_ALL, sessions: { page: 2 } } });
  assert.equal(rows(second.sessionsHtml).length, 3);
  const channel = ui.view(st, tr, NOW, { channelOnly: true });
  assert.equal(rows(channel.sessionsHtml).length, 16);
  assert.match(ui.view(state({ costs: costs([session(1, "idle")]) }), tr, NOW, { channelOnly: true }).sessionsHtml, /Kanalla ulaşılabilen oturum yok/);
});

const bigBoard = (over = {}) => state({ tasks: byKey(many(65, "running"), "id"), agents: byKey(many(40, "running", agent), "key"),
  costs: costs(Array.from({ length: 35 }, (_, i) => session(i + 1, "busy"))), ...over });

test("page: the pager buttons turn the pages and the pager text follows", async () => {
  const page = fakePage({ server: { state: bigBoard() } });
  await page.app.first;
  assert.equal(rows(page.els.tasks.innerHTML).length, 30);
  assert.match(page.els.tasksPager.innerHTML, /Sayfa 1 \/ 3 · 65 satır/);
  assert.match(page.els.tasksPager.innerHTML, /data-dir="-1" disabled/);
  assert.equal(page.click(pagerBtn("tasks", 1)), null);
  assert.match(page.els.tasksPager.innerHTML, /Sayfa 2 \/ 3/);
  page.click(pagerBtn("tasks", 1));
  assert.equal(rows(page.els.tasks.innerHTML).length, 5);
  assert.match(page.els.tasksPager.innerHTML, /data-dir="1" disabled/);
  page.click(pagerBtn("tasks", 1));                              // past the end: held on the last page
  assert.match(page.els.tasksPager.innerHTML, /Sayfa 3 \/ 3/);
  page.click(pagerBtn("agents", 1)); page.click(pagerBtn("sessions", 1));
  assert.match(page.els.agentsPager.innerHTML, /Sayfa 2 \/ 2 · 40 satır/);
  assert.match(page.els.sessionsPager.innerHTML, /Sayfa 2 \/ 2 · 35 satır/);
});

test("page: the finished-rows checkboxes show them and go back to page 1", async () => {
  const done = [...many(3, "running"), ...many(40, "done").map((x) => ({ ...x, id: `T-${100 + Number(x.id.slice(2))}` }))];
  const page = fakePage({ server: { state: state({ tasks: byKey(done, "id"), agents: byKey([agent(1, "done"), agent(2, "running")], "key"), costs: costs() }) } });
  await page.app.first;
  assert.equal(rows(page.els.tasks.innerHTML).length, 3);
  assert.match(page.els.tasksPager.innerHTML, /40 biten gizli/);
  page.change({ id: "showDoneTasks", checked: true, dataset: {} });
  assert.equal(rows(page.els.tasks.innerHTML).length, 30);
  page.click(pagerBtn("tasks", 1));
  assert.equal(rows(page.els.tasks.innerHTML).length, 13);
  page.change({ id: "showDoneTasks", checked: false, dataset: {} });
  assert.equal(rows(page.els.tasks.innerHTML).length, 3);
  assert.equal(rows(page.els.agents.innerHTML).length, 1);
  page.change({ id: "showDoneAgents", checked: true, dataset: {} });
  assert.equal(rows(page.els.agents.innerHTML).length, 2);
});

test("page: the channel checkbox goes back to the first sessions page", async () => {
  const page = fakePage({ server: { state: bigBoard() } });
  await page.app.first;
  page.click(pagerBtn("sessions", 1));
  assert.match(page.els.sessionsPager.innerHTML, /Sayfa 2 \/ 2/);
  page.els.channelOnly.checked = false;
  page.change({ id: "channelOnly", dataset: {} });
  assert.equal(page.app.project(), "aaaaaaaaaa");
  assert.doesNotMatch(page.els.sessionsPager.innerHTML, /Sayfa 2 \/ 2/);
});

test("page: switching project starts every table on its first page again", async () => {
  const page = fakePage({ server: { state: bigBoard() } });
  await page.app.first;
  page.click(pagerBtn("tasks", 1));
  assert.match(page.els.tasksPager.innerHTML, /Sayfa 2 \/ 3/);
  await page.click(hit("[data-project]", { dataset: { project: "bbbbbbbbbb" } }));
  assert.match(page.els.tasksPager.innerHTML, /Sayfa 1 \/ 3/);
});

test("page: a table that shrinks pulls the reader back into range", async () => {
  let current = bigBoard();
  const page = fakePage({ server: { get state() { return current; } } });
  await page.app.first;
  page.click(pagerBtn("tasks", 1)); page.click(pagerBtn("tasks", 1));
  assert.match(page.els.tasksPager.innerHTML, /Sayfa 3 \/ 3/);
  current = bigBoard({ tasks: byKey(many(40, "running"), "id") });  // 65 -> 40 rows: two pages
  await page.app.refresh();
  assert.match(page.els.tasksPager.innerHTML, /Sayfa 2 \/ 2 · 40 satır/);
  assert.equal(rows(page.els.tasks.innerHTML).length, 10);
});

test("page: a table that did not change is not rewritten on the next poll", async () => {
  const page = fakePage({ server: { state: bigBoard() } });
  await page.app.first;
  const writes = [];
  Object.defineProperty(page.els.tasks, "innerHTML", { get: () => "", set: (v) => writes.push(v), configurable: true });
  await page.app.refresh();
  await page.app.refresh();
  assert.equal(writes.length, 0);
});

test("page: while a note is being typed the tasks table is left alone, the pager still moves", async () => {
  const page = fakePage({ server: { state: bigBoard() }, active: { dataset: { noteFor: "T-1" } } });
  await page.app.first;
  assert.equal(page.els.tasks, undefined);                       // never written
  assert.match(page.els.tasksPager.innerHTML, /Sayfa 1 \/ 3/);
});
