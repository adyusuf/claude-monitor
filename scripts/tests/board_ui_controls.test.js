"use strict";
// The live board's session controls, driven through start() with a fake document: queueing a
// task, running a skill, switching the mode, drafts kept across redraws, the skill list and the
// service worker registration (scripts/board/board_ui.js, docs/live-board.md §2c-§2e).
// Run: node --test scripts/tests/  — measured by scripts/coverage.sh (#29).
const test = require("node:test");
const assert = require("node:assert/strict");
const { ui, tr, en, ago, state, SKILLS, serverLike, fakePage, target } = require("./board_ui_fixtures.js");

const SID = "sess-0001-aaaa";
const PROJECT = "aaaaaaaaaa";
const withSession = () => state({
  mode: "C", modes: ["A", "B", "C"],
  costs: { sessions: [{ id: SID, state: "idle", since: ago(5), last: ago(1), has_transcript: true,
    orchestration: null, agents: { cost: null }, subagents: 0, total: null, rate_per_h: null,
    context: null, context_warn: false, projected: null, queued: [] }],
  tasks: {}, basis: { remaining_h: 0, eta_tasks: 0, no_eta_tasks: 0, window_s: 3600 }, warn_ratio: 0.8 },
});
const hit = (selector, el) => target({ closest: (sel) => (sel === selector ? el : null) });
const QUEUE_SEL = `[data-queue-for="${SID}"]`;
const SKILL_SEL = `[data-skill-for="${SID}"]`;
const sendBtn = () => hit("[data-queue-send]", { dataset: { queueSend: SID } });
const runBtn = () => hit("[data-skill-run]", { dataset: { skillRun: SID } });
const posts = (page) => page.calls.filter(([url]) => url === "/api/control").map(([, o]) => JSON.parse(o.body));
const board = async (opts = {}) => {
  const page = fakePage({ ...opts, server: { state: withSession(), ...opts.server } });
  await page.app.first;
  return page;
};

test("sending a task posts the queued text for that session and clears the draft", async () => {
  const page = await board({ notes: { [QUEUE_SEL]: "write the tests" } });
  page.input({ dataset: { queueFor: SID }, value: "write the tests" });
  assert.equal(page.app.drafts.queue[SID], "write the tests");
  const ok = await page.click(sendBtn());
  assert.equal(ok, true);
  assert.deepEqual(posts(page), [{ action: "queue_task", value: SID, project: PROJECT, text: "write the tests" }]);
  assert.equal(SID in page.app.drafts.queue, false);
  assert.doesNotMatch(page.els.sessions.innerHTML, /write the tests/);
  assert.equal(page.fields[QUEUE_SEL].value, "");  // emptied even while it keeps the focus
});

test("a refused task keeps the typed text and shows the server's reason", async () => {
  const control = { ok: false, json: async () => ({ error: "text too long" }) };
  const page = await board({ notes: { [QUEUE_SEL]: "long text" }, server: { control } });
  page.input({ dataset: { queueFor: SID }, value: "long text" });
  const ok = await page.click(sendBtn());
  assert.equal(ok, false);
  assert.equal(page.app.drafts.queue[SID], "long text");
  assert.equal(page.els.err.textContent, "text too long");
  assert.equal(page.fields[QUEUE_SEL].value, "long text");
  await page.app.refresh();
  assert.match(page.els.sessions.innerHTML, /value="long text"/);
});

test("a refused send with nothing typed does not invent a draft; a missing field sends empty text", async () => {
  const control = { ok: false, json: async () => ({ error: "empty" }) };
  const page = await board({ server: { control } });
  assert.equal(await page.click(sendBtn()), false);
  assert.deepEqual(page.app.drafts, { queue: {}, skill: {} });
  assert.equal(posts(page)[0].text, "");
});

test("running a skill without a pick writes 'pick a skill' and posts nothing", async () => {
  const none = await board();
  assert.equal(none.click(runBtn()), null);
  assert.equal(none.els.err.textContent, "skill seç");
  const empty = await board({ notes: { [SKILL_SEL]: "" } });
  assert.equal(empty.click(runBtn()), null);
  assert.equal(empty.els.err.textContent, "skill seç");
  assert.deepEqual([...posts(none), ...posts(empty)], []);
});

test("running a picked skill posts it with the session and clears the pick", async () => {
  const page = await board({ notes: { [SKILL_SEL]: "review" } });
  page.change({ dataset: { skillFor: SID }, value: "review" });
  assert.equal(page.app.drafts.skill[SID], "review");
  assert.equal(await page.click(runBtn()), true);
  assert.deepEqual(posts(page), [{ action: "run_skill", value: "review", project: PROJECT, session: SID }]);
  assert.equal(SID in page.app.drafts.skill, false);
  assert.doesNotMatch(page.els.sessions.innerHTML, / selected/);
});

test("a refused skill run keeps the pick selected", async () => {
  const control = { ok: false, json: async () => ({ error: "unknown skill" }) };
  const page = await board({ notes: { [SKILL_SEL]: "review" }, server: { control } });
  page.input({ dataset: { skillFor: SID }, value: "review" });
  assert.equal(await page.click(runBtn()), false);
  assert.equal(page.app.drafts.skill[SID], "review");
  assert.equal(page.els.err.textContent, "unknown skill");
  await page.app.refresh();
  assert.match(page.els.sessions.innerHTML, /<option value="review" selected>/);
});

test("a mode button asks first: declined posts nothing, and the question names the mode", async () => {
  const page = await board({ confirm: false });
  const result = page.click(hit("[data-mode]", { dataset: { mode: "C" } }));
  assert.equal(result, null);
  assert.deepEqual(posts(page), []);
  assert.deepEqual(page.confirms, [tr("confirmMode").replace("{mode}", "C")]);
  assert.match(page.confirms[0], /Çalışma modu C olsun mu\?/);
});

test("a confirmed mode button posts set_mode; the question is in the page language", async () => {
  const page = await board({ confirm: true, stored: { "board.lang": "en" } });
  await page.click(hit("[data-mode]", { dataset: { mode: "B" } }));
  assert.deepEqual(posts(page), [{ action: "set_mode", value: "B", project: PROJECT }]);
  assert.equal(page.confirms.length, 1);
  assert.equal(page.confirms[0], en("confirmMode").replace("{mode}", "B"));
  assert.match(page.els.modes.innerHTML, /data-mode="C" aria-pressed="true"/);
});

test("onInput keeps drafts for queue and skill fields and ignores every other field", async () => {
  const page = await board();
  page.input({ dataset: { queueFor: "s1" }, value: "abc" });
  page.change({ dataset: { skillFor: "s1" }, value: "review" });
  page.input({ dataset: { noteFor: "T-1" }, value: "a note" });
  page.input({ dataset: {}, value: "loose" });
  page.input({ value: "no dataset" });
  assert.deepEqual(page.app.drafts, { queue: { s1: "abc" }, skill: { s1: "review" } });
  assert.equal(page.handlers.input.length + page.handlers.change.length, 2);
});

test("a typed draft survives a redraw of the table", async () => {
  const page = await board();
  page.input({ dataset: { queueFor: SID }, value: "half typed" });
  await page.app.refresh();
  assert.match(page.els.sessions.innerHTML, /value="half typed"/);
});

test("the sessions table is not redrawn under a focused task field or skill select", async () => {
  for (const key of ["queueFor", "skillFor"]) {
    const page = fakePage({ active: { dataset: { [key]: SID } }, server: { state: withSession() } });
    await page.app.first;
    assert.ok(!page.els.sessions || page.els.sessions.innerHTML === "", `${key} focused: table untouched`);
    assert.notEqual(page.els.tasks.innerHTML, "", "the other tables still redraw");
  }
});

test("the sessions table is redrawn when nothing, a note field or a plain element has focus", async () => {
  for (const active of [null, { dataset: {} }, {}, { dataset: { noteFor: "T-1" } }]) {
    const page = fakePage({ active, server: { state: withSession() } });
    await page.app.first;
    assert.match(page.els.sessions.innerHTML, /sess-000/);
  }
});

test("skills are fetched once, before the first refresh, and offered in the select", async () => {
  const page = await board();
  const urls = page.calls.map(([url]) => url);
  assert.equal(urls.filter((u) => u === "/api/skills").length, 1);
  assert.ok(urls.indexOf("/api/skills") < urls.indexOf("/api/projects"));
  assert.deepEqual(page.app.skills(), SKILLS);
  assert.match(page.els.sessions.innerHTML, /<option value="brainstorm">/);
});

test("a skill answer that is not a list yields no skills; a failed fetch is logged, not fatal", async () => {
  const odd = await board({ server: { skills: { error: "nope" } } });
  assert.deepEqual(odd.app.skills(), []);
  const calls = [];
  const base = serverLike(calls, { state: withSession() });
  const broken = fakePage({ fetchImpl: async (url, opts) => {
    if (url === "/api/skills") throw new Error("no skills");
    return base(url, opts);
  } });
  await broken.app.first;
  assert.deepEqual(broken.app.skills(), []);
  assert.deepEqual(broken.errors.map((e) => e[0]), ["board skills failed"]);
  assert.equal(broken.els.mode.textContent, "C");
});

test("the service worker is registered at /sw.js when the browser supports it", async () => {
  const page = await board({ serviceWorker: "ok" });
  await page.app.swReady;
  assert.deepEqual(page.swCalls, ["/sw.js"]);
  assert.deepEqual(page.errors, []);
});

test("a refused service worker registration is logged, not thrown", async () => {
  const page = await board({ serviceWorker: "fail" });
  await page.app.swReady;
  assert.equal(page.errors.length, 1);
  assert.equal(page.errors[0][0], "service worker failed");
  assert.equal(page.errors[0][1].message, "sw refused");
});

test("without navigator.serviceWorker nothing is registered and nothing is logged", async () => {
  for (const serviceWorker of [undefined, "none"]) {
    const page = await board({ serviceWorker });
    await page.app.swReady;
    assert.deepEqual(page.swCalls, []);
    assert.deepEqual(page.errors, []);
  }
  assert.equal(typeof ui.start, "function");
});

test("the channel-only checkbox filters the sessions table without another fetch", async () => {
  const st = withSession();
  st.costs.sessions.push({ ...st.costs.sessions[0], id: "chan-0002-bbbb", channel: true });
  const page = fakePage({ server: { state: st } });
  await page.app.first;
  assert.match(page.els.sessions.innerHTML, /sess-000/);
  assert.match(page.els.sessions.innerHTML, /chan-000/);
  const fetches = page.calls.length;
  page.els.channelOnly.checked = true;
  page.change({ id: "channelOnly", dataset: {} });
  assert.doesNotMatch(page.els.sessions.innerHTML, /sess-000/);
  assert.match(page.els.sessions.innerHTML, /chan-000/);
  assert.equal(page.calls.length, fetches);
  page.els.channelOnly.checked = false;
  page.change({ id: "channelOnly", dataset: {} });
  assert.match(page.els.sessions.innerHTML, /sess-000/);
});

test("ticking the checkbox before any state has loaded is harmless", () => {
  const page = fakePage({ server: { state: withSession() } });
  assert.doesNotThrow(() => page.change({ id: "channelOnly", dataset: {} }));
});
