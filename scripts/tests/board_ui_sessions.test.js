"use strict";
// The live board's sessions panel and cost / ETA cells (scripts/board/board_ui_sessions.js,
// docs/live-board-sessions.md §2c, docs/live-board.md §2d): pure functions from the server's `costs` block to HTML.
// Run: node --test scripts/tests/  — measured by scripts/coverage.sh (#29).
const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");
const { tr, en, NOW, ago } = require("./board_ui_fixtures.js");
const panel = require(path.join(__dirname, "..", "board", "board_ui_sessions.js"));

const tokens = (input = 100, output = 50) => ({ input, output, cache_read: 0, cache_write_5m: 0, cache_write_1h: 0 });
const summary = (over = {}) => ({ tokens: tokens(), messages: 3, unpriced: [], first: null, cost: 1.234, window_cost: 0.5, ...over });
const session = (over = {}) => ({
  id: "abcdef1234567890", state: "busy", since: ago(90), last: ago(5), has_transcript: true,
  orchestration: summary({ cost: 1 }), agents: summary({ cost: 0.234 }), subagents: 2, total: summary(),
  rate_per_h: 2.5, context: { model: "m", tokens: 86000, window: 200000, ratio: 0.43 },
  context_warn: false, projected: 12.5, queued: [], ...over,
});
const basis = { remaining_h: 3.5, eta_tasks: 2, no_eta_tasks: 1, window_s: 3600 };
const stateOf = (sessions, over = {}) => ({ costs: { sessions, tasks: {}, basis, warn_ratio: 0.8 }, queue_text_max: 400, ...over });
const rows = (st, drafts = {}, t = tr, skills = ["brainstorm", "review"]) => panel.sessionsHtml(t, st, skills, drafts, NOW);
const cells = (html) => [...html.matchAll(/<td>([\s\S]*?)<\/td>/g)].map((m) => m[1]);

test("fmtUsd: unmeasured is said so, sub-cent is bounded, the rest has two decimals", () => {
  assert.equal(panel.fmtUsd(tr, null), "ölçülemiyor");
  assert.equal(panel.fmtUsd(tr, undefined), "ölçülemiyor");
  assert.equal(panel.fmtUsd(en, null), "not measured");
  assert.equal(panel.fmtUsd(tr, 0.004), "<$0.01");
  assert.equal(panel.fmtUsd(tr, 0.01), "$0.01");
  assert.equal(panel.fmtUsd(tr, 0), "$0.00");
  assert.equal(panel.fmtUsd(tr, 1.234), "$1.23");
});

test("fmtTok: plain below 1k, rounded k, one-decimal M", () => {
  assert.equal(panel.fmtTok(0), "0");
  assert.equal(panel.fmtTok(950), "950");
  assert.equal(panel.fmtTok(1000), "1k");
  assert.equal(panel.fmtTok(1500), "2k");
  assert.equal(panel.fmtTok(430000), "430k");
  assert.equal(panel.fmtTok(1000000), "1.0M");
  assert.equal(panel.fmtTok(1200000), "1.2M");
});

test("sessionsHtml: no session says so in one full-width row", () => {
  const html = rows({ costs: { sessions: [], basis } });
  assert.match(html, /<td colspan="7" class="empty">Son 24 saatte oturum yok\.<\/td>/);
  assert.match(rows({}, {}, en), /No session in the last 24 hours\./);
  assert.match(rows({ costs: {} }), /Son 24 saatte oturum yok/);
});

test("sessionsHtml: a busy row shows the short id, the state with its duration and the cost", () => {
  const html = rows(stateOf([session()]));
  const c = cells(html);
  assert.equal(c.length, 7);
  assert.match(c[0], /<code title="abcdef1234567890">abcdef12<\/code>/);
  assert.match(c[1], /<span class="badge st-busy">meşgul 1 dk 30 sn<\/span>/);
  assert.equal(c[2], "43% · 86k/200k");
  assert.match(c[3], /^<b>\$1\.23<\/b><div class="muted">150 token<\/div><div class="muted">orkestrasyon \$1\.00 · ajanlar \(2\) \$0\.23<\/div>$/);
  assert.equal(c[4], "$2.50/sa");
  assert.match(cells(rows(stateOf([session({ state: "idle" })])))[1], /<span class="badge st-idle">boşta /);
  assert.match(cells(rows(stateOf([session({ state: "unknown", since: null })])))[1], /st-unknown">bilinmiyor ölçülemiyor</);
  assert.equal((html.match(/<tr>/g) || []).length, 1);
  assert.equal((rows(stateOf([session(), session({ id: "zzzzzzzz9999" })])).match(/<tr>/g) || []).length, 2);
});

test("sessionsHtml: the compact badge shows only when the server flags the context", () => {
  assert.doesNotMatch(cells(rows(stateOf([session()])))[2], /compact/);
  const warned = cells(rows(stateOf([session({ context_warn: true })])))[2];
  assert.equal(warned, '43% · 86k/200k <span class="badge warn">compact önerilir</span>');
});

test("sessionsHtml: an unknown window shows tokens and 'not measured', never a percentage or a badge", () => {
  const ctx = { model: "m", tokens: 12000, window: null, ratio: null };
  const c = cells(rows(stateOf([session({ context: ctx, context_warn: true })])))[2];
  assert.equal(c, "12k · ölçülemiyor");
  assert.equal(cells(rows(stateOf([session({ context: null })])))[2], "ölçülemiyor");
  assert.equal(panel.contextHtml(en, session({ context: null })), "not measured");
});

test("sessionsHtml: unpriced models are named and their cost is never invented", () => {
  const total = summary({ cost: null, unpriced: ["mystery-1", "<b>x"] });
  const c = cells(rows(stateOf([session({ total })])))[3];
  assert.match(c, /^<b>ölçülemiyor<\/b>/);
  assert.match(c, /<div class="muted">fiyatı bilinmeyen model: mystery-1, &lt;b&gt;x<\/div>/);
  assert.doesNotMatch(cells(rows(stateOf([session()])))[3], /fiyatı bilinmeyen/);
});

test("costHtml: a missing total, missing orchestration and missing tokens are all handled", () => {
  assert.match(panel.costHtml(tr, session({ total: null, orchestration: null })), /^ölçülemiyor<div class="muted">orkestrasyon ölçülemiyor · ajanlar \(2\) \$0\.23<\/div>$/);
  const bare = panel.costHtml(tr, session({ total: summary({ tokens: null, cost: 0 }) }));
  assert.match(bare, /<b>\$0\.00<\/b><div class="muted">0 token<\/div>/);
  assert.match(panel.costHtml(en, session({ agents: summary({ cost: null }) })), /agents \(2\) not measured/);
});

test("sessionsHtml: the projection carries its basis with hours and task counts filled in", () => {
  const c = cells(rows(stateOf([session()])))[5];
  assert.match(c, /^≈ \$12\.50<div class="muted">tahmin: son 60 dk \$\/sa × açık işlerin kalan ETA&#39;sı \(3\.5 sa, 2 iş; ETA&#39;sız 1 iş sayılmadı\)<\/div>$/);
  assert.equal(cells(rows(stateOf([session({ projected: null })])))[5], "ölçülemiyor");
  assert.equal(panel.projectionHtml(tr, session({ projected: null }), basis), "ölçülemiyor");
  assert.match(panel.projectionHtml(en, session(), { remaining_h: 0, eta_tasks: 0, no_eta_tasks: 4 }),
    /\(0 h, 0 tasks; 4 without ETA not counted\)/);
});

test("sessionsHtml: without a rate there is no '/sa' unit; with one it has the unit", () => {
  assert.equal(cells(rows(stateOf([session({ rate_per_h: null })])))[4], "ölçülemiyor");
  assert.equal(cells(rows(stateOf([session({ rate_per_h: 0 })])))[4], "$0.00/sa");
  assert.equal(cells(rows(stateOf([session()]), {}, en))[4], "$2.50/h");
});

test("sessionsHtml: the send cell shows the queued count only when something is queued", () => {
  const some = cells(rows(stateOf([session({ queued: ["a", "b"] })])))[6];
  assert.match(some, /<div class="muted">sırada: 2<\/div>/);
  assert.doesNotMatch(cells(rows(stateOf([session()])))[6], /sırada/);
  const bare = session();
  delete bare.queued;
  assert.doesNotMatch(cells(rows(stateOf([bare])))[6], /sırada/);
});

test("sessionsHtml: the send cell has a text field with the server's max length and a skill select", () => {
  const c = cells(rows(stateOf([session()])))[6];
  assert.match(c, /data-queue-for="abcdef1234567890" maxlength="400"/);
  assert.match(c, /placeholder="yeni görev metni" value=""/);
  assert.match(c, /data-queue-send="abcdef1234567890">Gönder<\/button>/);
  assert.match(c, /<select data-skill-for="abcdef1234567890"><option value="">skill seç<\/option><option value="brainstorm">brainstorm<\/option><option value="review">review<\/option><\/select>/);
  assert.match(c, /data-skill-run="abcdef1234567890">Çalıştır<\/button>/);
  assert.match(cells(rows(stateOf([session()], { queue_text_max: undefined })))[6], /maxlength="0"/);
});

test("sessionsHtml: drafts refill the field and reselect the picked skill, for their own session only", () => {
  const drafts = { queue: { abcdef1234567890: 'half "typed" <b>', other: "not mine" }, skill: { abcdef1234567890: "review" } };
  const c = cells(rows(stateOf([session()]), drafts))[6];
  assert.match(c, /value="half &quot;typed&quot; &lt;b&gt;"/);
  assert.match(c, /<option value="review" selected>review<\/option>/);
  assert.match(c, /<option value="brainstorm">brainstorm<\/option>/);
  assert.doesNotMatch(c, /not mine/);
  assert.equal((c.match(/ selected/g) || []).length, 1);
});

test("sessionsHtml: session ids, skill names and drafts are HTML-escaped", () => {
  const id = '"><img src=x>';
  const html = rows(stateOf([session({ id })]), { queue: { [id]: "<script>" } }, tr, ["<i>skill"]);
  for (const raw of ["<img", "<script", "<i>"]) assert.equal(html.toLowerCase().includes(raw), false, raw);
  assert.match(html, /data-queue-for="&quot;&gt;&lt;img src=x&gt;"/);
  assert.match(html, /<option value="&lt;i&gt;skill">&lt;i&gt;skill<\/option>/);
  assert.match(html, /value="&lt;script&gt;"/);
});

test("helpHtml: the wait window is filled in; the undercount note only when subagents ran", () => {
  const none = panel.helpHtml(tr, stateOf([session({ subagents: 0 })]).costs, 45);
  assert.match(none, /<p>\/compact bir CLI komutudur/);
  assert.match(none, /en çok 45 sn/);
  assert.doesNotMatch(none, /output_tokens/);
  assert.match(panel.helpHtml(tr, stateOf([session({ subagents: 0 }), session({ subagents: 1 })]).costs, 45), /<p>Not: ajan transcript&#39;leri output_tokens/);
  assert.doesNotMatch(panel.helpHtml(tr, undefined, 45), /output_tokens/);
  assert.match(panel.helpHtml(en, { sessions: [session()] }, 30), /at most 30 s\).*<p>Note: subagent transcripts/);
});

test("modesHtml: the active mode is marked; 'picked on the board' only when the board chose it", () => {
  const html = panel.modesHtml(tr, { modes: ["A", "B", "C"], mode: "B", mode_by: "board" });
  assert.match(html, /^<span class="muted">Modu değiştir:<\/span> /);
  assert.match(html, /<button class="act mode on" data-mode="B" aria-pressed="true">B<\/button>/);
  assert.match(html, /<button class="act mode" data-mode="A" aria-pressed="false">A<\/button>/);
  assert.match(html, /<button class="act mode" data-mode="C" aria-pressed="false">C<\/button>/);
  assert.match(html, /\(panodan seçildi\)/);
  assert.doesNotMatch(panel.modesHtml(tr, { modes: ["A"], mode: "A", mode_by: null }), /panodan/);
  assert.doesNotMatch(panel.modesHtml(tr, { modes: ["A"], mode: "A", mode_by: "cli" }), /panodan/);
  assert.equal(panel.modesHtml(tr, {}), '<span class="muted">Modu değiştir:</span> ');
});

test("modesHtml: English text, and a mode value is escaped", () => {
  const html = panel.modesHtml(en, { modes: ["<x>"], mode: "A", mode_by: "board" });
  assert.match(html, /Switch mode:/);
  assert.match(html, /data-mode="&lt;x&gt;"/);
  assert.match(html, /\(picked on the board\)/);
});

// ---- taskCostHtml: a task's Cost / ETA cell ----

const cost = (over = {}) => ({ agents: 1, measured_agents: 1, spent: summary({ cost: 1.5, tokens: tokens(1000, 500) }),
  remaining_min: null, projected: null, est_left: null, over_estimate: false, ...over });
const taskCell = (task, c, t = tr) => panel.taskCostHtml(t, task, c);

test("taskCostHtml: a task the server has no cost for shows a dash", () => {
  assert.equal(taskCell({}, undefined), "—");
  assert.equal(taskCell({}, null), "—");
});

test("taskCostHtml: no linked agent says so; agents without a measurement say 'not measured'", () => {
  assert.equal(taskCell({}, cost({ agents: 0, spent: null })), "bağlı ajan yok");
  assert.equal(taskCell({}, cost({ agents: 2, spent: null })), "harcanan ölçülemiyor");
  assert.equal(taskCell({}, cost({ agents: 2, spent: null }), en), "spent not measured");
  assert.equal(taskCell({}, cost()), "harcanan <b>$1.50</b> · 2k token");
});

test("taskCostHtml: the ETA left is rounded and shown, also when it is zero", () => {
  assert.match(taskCell({}, cost({ remaining_min: 12.6 })), /<div class="muted">ETA kalan 13 dk<\/div>/);
  assert.match(taskCell({}, cost({ remaining_min: 0 })), /ETA kalan 0 dk/);
  assert.doesNotMatch(taskCell({}, cost()), /ETA kalan/);
});

test("taskCostHtml: the estimated budget shows with what is left and the over-estimate badge", () => {
  assert.doesNotMatch(taskCell({ est_cost: null }, cost()), /tahmini bütçe/);
  assert.doesNotMatch(taskCell({}, cost({ est_left: 2 })), /tahmini bütçe/);
  assert.match(taskCell({ est_cost: 5 }, cost({ est_left: 3.5 })), /<div class="muted">tahmini bütçe \$5\.00 · kalan \$3\.50<\/div>/);
  const noLeft = taskCell({ est_cost: 5 }, cost());
  assert.match(noLeft, /tahmini bütçe \$5\.00<\/div>/);
  assert.doesNotMatch(noLeft, /kalan|tahmini aştı/);
  assert.match(taskCell({ est_cost: 0 }, cost()), /tahmini bütçe \$0\.00/);
  assert.match(taskCell({ est_cost: 5 }, cost({ est_left: -1, over_estimate: true })),
    /kalan \$-1\.00 <span class="badge warn">tahmini aştı<\/span><\/div>/);
  assert.match(taskCell({ est_cost: 5 }, cost({ over_estimate: true }), en), /est\. cost \$5\.00 <span class="badge warn">over the estimate<\/span>/);
});

test("taskCostHtml: a projection shows with its basis; without one there is no line", () => {
  assert.doesNotMatch(taskCell({}, cost()), /bitiş/);
  assert.match(taskCell({}, cost({ projected: 9 })),
    /<div class="muted">bitiş ≈ \$9\.00 <span class="muted">\(tahmin: görevin kendi \$\/sa hızı × kalan ETA\)<\/span><\/div>/);
  assert.match(taskCell({}, cost({ projected: 0 })), /bitiş ≈ \$0\.00/);
});

test("taskCostHtml: the first line is plain, every later line is muted, in order", () => {
  const html = taskCell({ est_cost: 5 }, cost({ remaining_min: 30, est_left: 3.5, projected: 9 }));
  assert.equal(html, 'harcanan <b>$1.50</b> · 2k token<div class="muted">ETA kalan 30 dk</div>' +
    '<div class="muted">tahmini bütçe $5.00 · kalan $3.50</div>' +
    '<div class="muted">bitiş ≈ $9.00 <span class="muted">(tahmin: görevin kendi $/sa hızı × kalan ETA)</span></div>');
});

test("a channel-reachable session carries the channel badge, the others do not (tr + en)", () => {
  const html = rows(stateOf([session({ channel: true, state: "idle" })]));
  assert.match(html, /<span class="badge ch" title="[^"]*board-channel[^"]*">kanal<\/span>/);
  assert.match(rows(stateOf([session({ channel: true })]), {}, en), /<span class="badge ch" title="[^"]+">channel<\/span>/);
  assert.doesNotMatch(rows(stateOf([session({ channel: false })])), /badge ch/);
  assert.doesNotMatch(rows(stateOf([session()])), /badge ch/);  // an older server sends no flag
});

test("the channel-only filter keeps just the reachable sessions and says so when there are none", () => {
  const list = [session({ id: "aaaaaaaa11111111", channel: true }), session({ id: "bbbbbbbb22222222", channel: false })];
  const all = panel.sessionsHtml(tr, stateOf(list), [], {}, NOW);
  const only = panel.sessionsHtml(tr, stateOf(list), [], {}, NOW, true);
  assert.match(all, /aaaaaaaa/);
  assert.match(all, /bbbbbbbb/);
  assert.match(only, /aaaaaaaa/);
  assert.doesNotMatch(only, /bbbbbbbb/);
  const none = panel.sessionsHtml(tr, stateOf([session({ channel: false })]), [], {}, NOW, true);
  assert.match(none, /<td colspan="7" class="empty">Kanalla ulaşılabilen oturum yok\.<\/td>/);
  assert.match(panel.sessionsHtml(en, stateOf([]), [], {}, NOW, true), /No session a channel can reach\./);
  assert.match(panel.sessionsHtml(en, stateOf([]), [], {}, NOW), /No session in the last 24 hours\./);
});

test("helpHtml names how many sessions a channel reaches and how to start one", () => {
  const costs = { sessions: [session({ channel: true }), session({ channel: true }), session({ channel: false })] };
  assert.match(panel.helpHtml(tr, costs, 180), /Kanal: 2 oturum kanalla ulaşılabilir[^<]*server:board-channel/);
  assert.match(panel.helpHtml(en, costs, 180), /Channel: 2 session\(s\) reachable by channel[^<]*desktop app cannot/);
  assert.match(panel.helpHtml(en, { sessions: [] }, 180), /Channel: 0 session/);
  assert.match(panel.helpHtml(en, undefined, 180), /Channel: 0 session/);
});

test("todo line: done/total and the running item; absent when the session sent no list; escaped", () => {
  const withTodos = rows(stateOf([session({ todos: { done: 2, total: 5, current: "Running <b>tests</b>" } })]));
  assert.match(withTodos, /yapılacaklar 2\/5 · Running &lt;b&gt;tests&lt;\/b&gt;/);
  assert.match(rows(stateOf([session({ todos: { done: 0, total: 1, current: null } })]), {}, en), /todo 0\/1</);
  assert.doesNotMatch(rows(stateOf([session({ todos: null })])), /yapılacaklar/);
  assert.doesNotMatch(rows(stateOf([session()])), /yapılacaklar/);
});

test("session name: shown above the id, escaped, absent when the transcript has none", () => {
  const named = rows(stateOf([session({ title: "live board <b>x</b>" })]));
  assert.match(named, /<div class="sname" title="live board &lt;b&gt;x&lt;\/b&gt;">live board &lt;b&gt;x&lt;\/b&gt;<\/div><code title="abcdef1234567890">abcdef12<\/code>/);
  assert.doesNotMatch(rows(stateOf([session({ title: null })])), /sname/);
  assert.doesNotMatch(rows(stateOf([session()])), /sname/);
});

test("task cost cell: the orchestrator estimate replaces 'no agent linked', with its basis and sharing", () => {
  const orch = { summary: { cost: 2.5, tokens: tokens(1000, 500) }, sessions: ["abcdef1234567890", "<i>x</i>zzzzzzz"], shared: 2 };
  const only = panel.taskCostHtml(tr, { est_cost: null }, { agents: 0, spent: null, orchestration: orch });
  assert.doesNotMatch(only, /bağlı ajan yok/);
  assert.match(only, /orkestratör ≈ <b>\$2\.50<\/b> · 2k token/);
  assert.match(only, /oturum abcdef12, &lt;i&gt;x&lt;\/i&gt;, görevin zaman penceresi/);
  assert.match(only, /aynı pencerede 2 başka görev/);
  const none = panel.taskCostHtml(tr, { est_cost: null }, { agents: 0, spent: null, orchestration: null });
  assert.match(none, /bağlı ajan yok/);
  const both = panel.taskCostHtml(en, { est_cost: null }, { agents: 1, spent: { cost: 1, tokens: tokens() }, orchestration: { ...orch, shared: 0 } });
  assert.match(both, /spent/);
  assert.match(both, /orchestrator ≈/);
  assert.doesNotMatch(both, /other task/);
});
