"use strict";
// The live board's page script (scripts/board/board_ui.js, docs/live-board.md).
// Run: node --test scripts/tests/  — measured by scripts/coverage.sh (#29).
const test = require("node:test");
const assert = require("node:assert/strict");
const { ui, tr, en, NOW, iso, ago, state, fakePage, target } = require("./board_ui_fixtures.js");


test("dates are dd/mm/yyyy and times 24h, built by hand (#12)", () => {
  const d = new Date(2026, 0, 5, 7, 8, 9);
  assert.equal(ui.fmtDate(d), "05/01/2026");
  assert.equal(ui.fmtTime(d), "07:08:09");
  assert.equal(ui.fmtStamp(iso(d), new Date(2026, 0, 5, 23, 0, 0)), "07:08:09");
  assert.equal(ui.fmtStamp(iso(d), NOW), "05/01/2026 07:08:09");
  assert.equal(ui.fmtStamp(null, NOW), "–");
});

test("durations use i18n units and say 'not measured' without a start", () => {
  assert.equal(ui.fmtDur(tr, ago(42), null, NOW), "42 sn");
  assert.equal(ui.fmtDur(en, ago(125), null, NOW), "2 m 5 s");
  assert.equal(ui.fmtDur(tr, ago(3 * 3600 + 120), null, NOW), "3 sa 2 dk");
  assert.equal(ui.fmtDur(tr, ago(90), ago(30), NOW), "1 dk 0 sn");
  assert.equal(ui.fmtDur(tr, null, null, NOW), "ölçülemiyor");
});

test("text is escaped and i18n falls back to tr, then to the key", () => {
  assert.equal(ui.esc(`<a href="x">'&'</a>`), "&lt;a href=&quot;x&quot;&gt;&#39;&amp;&#39;&lt;/a&gt;");
  assert.equal(ui.esc(null), "");
  assert.equal(ui.makeT("de")("remove"), "Çıkar");
  assert.equal(tr("no_such_key"), "no_such_key");
});

test("view counts tasks and reports the turn state", () => {
  const st = state({
    tasks: {
      "T-1": { id: "T-1", title: "a", branch: "fix/a", role: "developer", note: "", status: "running", agents: [] },
      "T-2": { id: "T-2", title: "b", branch: "", role: "", note: "", status: "agent_done", agents: [] },
      "T-3": { id: "T-3", title: "c", branch: "", role: "", note: "", status: "removed", agents: [] },
    },
    sessions: { s: { turn_open: true } },
  });
  const v = ui.view(st, tr, NOW);
  assert.match(v.statsHtml, /<div class="n">1<\/div><div class="l">Çalışıyor/);
  assert.match(v.statsHtml, /<div class="n">1<\/div><div class="l">Bekliyor/);
  assert.equal(v.turnText, "Claude çalışıyor");
  assert.equal(v.turnOpen, true);
  assert.equal(v.allDone, false);
  assert.match(v.tasksHtml, /<code>fix\/a<\/code>/);
  assert.match(v.tasksHtml, /data-action="restore_task"/);
  assert.equal(ui.view(state({ sessions: { s: { turn_open: false } } }), tr, NOW).turnText, "Tur bitti");
  assert.equal(ui.view(state(), en, NOW).turnText, "No session");
});

test("empty board shows empty states and no completion banner", () => {
  const v = ui.view(state({ mode: null, roles: null }), tr, NOW);
  assert.equal(v.rolesHtml, "");
  assert.equal(v.mode, "–");
  assert.match(v.tasksHtml, /Henüz iş yok/);
  assert.match(v.agentsHtml, /Henüz ajan başlatılmadı/);
  assert.equal(v.allDone, false);
});

test("the banner shows only when every kept task is done and no agent is live", () => {
  const agents = { a: { key: "a", type: "qa", task: "T-1", status: "running", started: ago(5), ended: null } };
  const tasks = {
    "T-1": { id: "T-1", title: "x", branch: "", role: "qa", note: "", status: "done", agents: ["a"] },
    "T-2": { id: "T-2", title: "y", branch: "", role: "", note: "", status: "removed", agents: [] },
  };
  assert.equal(ui.view(state({ tasks, agents }), tr, NOW).allDone, false);
  agents.a.status = "done";
  agents.a.ended = ago(1);
  const v = ui.view(state({ tasks, agents }), tr, NOW);
  assert.equal(v.allDone, true);
  assert.match(v.tasksHtml, /4 sn/);
});

test("live agents show per task; a role that was only denied gets no switch", () => {
  const agents = {
    a: { key: "a", type: "developer", task: "T-1", status: "running", started: ago(10), ended: null, description: "[T-1] x" },
    b: { key: "b", type: "security", task: "T-1", status: "denied", started: ago(3), ended: ago(3), reason: "outside mode" },
  };
  const tasks = { "T-1": { id: "T-1", title: "x", branch: "", role: "developer", note: "", status: "running", agents: ["a"] } };
  const v = ui.view(state({ tasks, agents, control: { removed_tasks: [], disabled_roles: ["qa"] } }), tr, NOW);
  assert.match(v.tasksHtml, /agent-chip"><span class="dot on"><\/span>developer/);
  assert.doesNotMatch(v.rolesHtml, /data-role="security"/);
  assert.match(v.rolesHtml, /class="role off"><button class="switch" role="switch" aria-checked="false"/);
  assert.match(v.rolesHtml, /data-role="developer"><\/button>\s*<span>developer<\/span><span class="cnt">1 çalışan/);
  assert.match(v.agentsHtml, /outside mode/);
  assert.match(v.agentsHtml, /<td class="muted">—<\/td>/);
});

// ---- start(): the DOM wiring, driven through a minimal fake document ----


test("start renders the first state in Turkish by default", async () => {
  const page = fakePage();
  await page.app.first;
  assert.equal(page.app.lang(), "tr");
  assert.equal(page.doc.documentElement.lang, "tr");
  assert.equal(page.labels[0].textContent, "Claude Monitor");
  assert.equal(page.els.lang.textContent, "EN");
  assert.equal(page.els.mode.textContent, "C");
  assert.equal(page.els.dot.className, "dot on");
  assert.equal(page.els.banner.shown, false);
  assert.equal(page.els.err.textContent, "");
});

test("a stored language is honoured and blocked storage falls back to tr", async () => {
  const page = fakePage({ stored: { "board.lang": "en" } });
  await page.app.first;
  assert.equal(page.els.turn.textContent, "Claude is working");
  const blocked = fakePage({ storageThrows: true });
  await blocked.app.first;
  assert.equal(blocked.app.lang(), "tr");
});

test("the language button switches, re-renders and remembers the choice", async () => {
  const page = fakePage();
  await page.app.first;
  assert.equal(page.click(target({ id: "lang" })), null);
  assert.equal(page.app.lang(), "en");
  assert.equal(page.saved["board.lang"], "en");
  assert.equal(page.els.turn.textContent, "Claude is working");
  page.click(target({ id: "lang" }));
  assert.equal(page.app.lang(), "tr");
});

test("switches and task buttons post the right control", async () => {
  const page = fakePage();
  await page.app.first;
  const sw = { dataset: { role: "qa" }, getAttribute: () => "true" };
  await page.click(target({ closest: (sel) => (sel === "[data-role]" ? sw : null) }));
  const off = { dataset: { role: "qa" }, getAttribute: () => "false" };
  await page.click(target({ closest: (sel) => (sel === "[data-role]" ? off : null) }));
  const btn = { dataset: { task: "T-4", action: "remove_task" } };
  await page.click(target({ closest: (sel) => (sel === "[data-task]" ? btn : null) }));
  const posts = page.calls.filter(([url]) => url === "/api/control");
  assert.deepEqual(posts.map(([, o]) => JSON.parse(o.body)), [
    { action: "disable_role", value: "qa", project: "aaaaaaaaaa" },
    { action: "enable_role", value: "qa", project: "aaaaaaaaaa" },
    { action: "remove_task", value: "T-4", project: "aaaaaaaaaa" },
  ]);
  assert.equal(posts[0][1].headers["Content-Type"], "application/json");
  assert.equal(page.click(target()), null);
});

test("a refused control shows the server's error", async () => {
  const page = fakePage({ server: { control: { ok: false, json: async () => ({ error: "invalid value" }) } } });
  await page.app.first;
  await page.app.control("remove_task", "bad");
  assert.equal(page.els.err.textContent, "invalid value");
  await page.app.refresh();
  assert.equal(page.els.err.textContent, "");
});

test("every registered project gets a tab; the first is selected by default", async () => {
  const page = fakePage();
  await page.app.first;
  const html = page.els.projects.innerHTML;
  assert.match(html, /class="tab on" data-project="aaaaaaaaaa"/);
  assert.match(html, /data-project="bbbbbbbbbb"/);
  assert.match(html, /<span class="dot on"><\/span>alpha\s*<span class="cnt">1\/2<\/span>/);
  assert.ok(page.calls.some(([url]) => url === "/api/state?p=aaaaaaaaaa"));
});

test("a remembered project is reopened; a vanished one falls back to the first", async () => {
  const kept = fakePage({ stored: { "board.project": "bbbbbbbbbb" } });
  await kept.app.first;
  assert.equal(kept.app.project(), "bbbbbbbbbb");
  const gone = fakePage({ stored: { "board.project": "cccccccccc" } });
  await gone.app.first;
  assert.equal(gone.app.project(), "aaaaaaaaaa");
});

test("clicking a tab switches the board and remembers the choice", async () => {
  const page = fakePage();
  await page.app.first;
  const tab = { dataset: { project: "bbbbbbbbbb" } };
  await page.click(target({ closest: (sel) => (sel === "[data-project]" ? tab : null) }));
  assert.equal(page.app.project(), "bbbbbbbbbb");
  assert.equal(page.saved["board.project"], "bbbbbbbbbb");
  assert.equal(page.calls.at(-1)[0], "/api/state?p=bbbbbbbbbb");
});

test("no project yet: the tabs say so and no board is requested", async () => {
  const page = fakePage({ server: { projects: [] } });
  await page.app.first;
  assert.match(page.els.projects.innerHTML, /Henüz proje yok/);
  assert.ok(!page.calls.some(([url]) => url.startsWith("/api/state")));
  assert.equal(page.els.err.textContent, "");
});

test("an unreachable server is shown and logged, not swallowed", async () => {
  const page = fakePage({ fetchImpl: async () => { throw new Error("down"); } });
  await page.app.first;
  assert.equal(page.els.err.textContent, "Sunucuya ulaşılamıyor");
  // start() logs the failed skill fetch and the refresh failure; both must be visible.
  assert.deepEqual(page.errors.map((e) => e[0]), ["board skills failed", "board refresh failed"]);
  assert.ok(page.errors.every((e) => e[1] instanceof Error && e[1].message === "down"));
});

test("the Merge column shows each existing branch, merged or not, and a dash without commits", () => {
  const html = ui.mergeHtml({ merged: { dev: true, test: false, prod: null } });
  assert.match(html, /<span class="mg on">dev ✓<\/span>/);
  assert.match(html, /<span class="mg">test —<\/span>/);
  assert.doesNotMatch(html, /prod/);
  assert.equal(ui.mergeHtml({ merged: {} }), "—");
  assert.equal(ui.mergeHtml({}), "—");
});
