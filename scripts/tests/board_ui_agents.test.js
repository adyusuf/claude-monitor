"use strict";
// The Agent-activity cost cells and total row (scripts/board/board_ui_sessions.js tokenBreakdown,
// agentCostCells, agentTotalRow, roleCostText) and their use in view() (scripts/board/board_ui.js).
// Run: node --test scripts/tests/  — measured by scripts/coverage.sh (#29).
const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");
const { ui, tr, en, NOW, ago, state, SHOW_ALL, fakePage } = require("./board_ui_fixtures.js");
const panel = require(path.join(__dirname, "..", "board", "board_ui_sessions.js"));

const tokens = (over = {}) => ({ input: 1500, output: 2000000, cache_read: 430000, cache_write_5m: 100, cache_write_1h: 250, ...over });
const summary = (over = {}) => ({ tokens: tokens(), messages: 3, unpriced: [], first: null, cost: 1.234, window_cost: 0, ...over });
const measured = (over) => ({ measured: true, summary: summary(over) });
const NOT_YET = { measured: false, summary: null };
const agentRows = (over = {}) => ({ rows: {}, total: summary({ cost: 5.5 }), unmeasured: 0, by_type: {}, ...over });
const agent = (key, over = {}) => ({ key, type: "analyst", task: "T-1", status: "done", started: ago(60), ended: ago(30),
  description: `desc-${key}`, reason: null, ...over });
const withCosts = (agents, rows, over = {}) => state({ agents, costs: { sessions: [], tasks: {}, basis: {}, agent_rows: rows, ...over } });
const trs = (html) => html.match(/<tr[\s\S]*?<\/tr>/g) || [];
const columns = (row) => [...row.matchAll(/<td([^>]*)>/g)]
  .reduce((n, m) => n + Number((/colspan="(\d+)"/.exec(m[1]) || [0, 1])[1]), 0);

test("tokenBreakdown: in / out / cache read / cache write (5m + 1h summed), abbreviated by fmtTok", () => {
  assert.equal(panel.tokenBreakdown(tr, tokens()), "gir 2k · çık 2.0M · ön.oku 430k · ön.yaz 350");
  assert.equal(panel.tokenBreakdown(en, tokens()), "in 2k · out 2.0M · c.read 430k · c.write 350");
  assert.match(panel.tokenBreakdown(en, tokens({ cache_write_5m: 0 })), /c\.write 250$/);
  assert.match(panel.tokenBreakdown(en, tokens({ cache_write_1h: 0 })), /c\.write 100$/);
});

test("tokenBreakdown: a missing key counts as zero, never NaN", () => {
  assert.equal(panel.tokenBreakdown(en, { input: 7 }), "in 7 · out 0 · c.read 0 · c.write 0");
});

test("agentCostCells: no row or an unmeasured row is one colspan=2 'cannot be measured yet' cell", () => {
  const one = '<td class="muted" colspan="2">henüz ölçülemiyor</td>';
  assert.equal(panel.agentCostCells(tr, null), one);
  assert.equal(panel.agentCostCells(tr, undefined), one);
  assert.equal(panel.agentCostCells(tr, NOT_YET), one);
  assert.equal(panel.agentCostCells(en, NOT_YET), '<td class="muted" colspan="2">cannot be measured yet</td>');
});

test("agentCostCells: a measured row is a tokens cell and a bold cost cell", () => {
  const html = panel.agentCostCells(tr, measured());
  assert.equal(html, '<td class="muted">gir 2k · çık 2.0M · ön.oku 430k · ön.yaz 350</td><td><b>$1.23</b></td>');
  assert.equal(columns(html), 2);
  assert.match(panel.agentCostCells(tr, measured({ cost: 0.004 })), /<b>&lt;\$0\.01<\/b>/);
});

test("agentCostCells: an unpriced model is named and its cost is 'not measured', never a number", () => {
  const html = panel.agentCostCells(tr, measured({ cost: null, unpriced: ["claude-x", "claude-y"] }));
  assert.match(html, /<b>ölçülemiyor<\/b><div class="muted">fiyatı bilinmeyen model: claude-x, claude-y<\/div><\/td>$/);
  assert.doesNotMatch(html, /\$\d/);
  assert.match(panel.agentCostCells(en, measured({ cost: null, unpriced: ["m"] })), /<b>not measured<\/b><div class="muted">unpriced model: m</);
});

test("agentCostCells: the unpriced model name is HTML-escaped", () => {
  const html = panel.agentCostCells(tr, measured({ cost: null, unpriced: ['<img src=x onerror="1">'] }));
  assert.doesNotMatch(html, /<img/);
  assert.match(html, /&lt;img src=x onerror=&quot;1&quot;&gt;/);
});

test("agentTotalRow: empty without agentRows or without a total (an older server)", () => {
  assert.equal(panel.agentTotalRow(tr, undefined), "");
  assert.equal(panel.agentTotalRow(tr, null), "");
  assert.equal(panel.agentTotalRow(tr, agentRows({ total: null })), "");
});

test("agentTotalRow: a Total row with a 5-column label, tokens, cost and an empty note cell", () => {
  const html = panel.agentTotalRow(tr, agentRows());
  assert.equal(html, '<tr class="total"><td colspan="5"><b>Toplam (ölçülen ajanlar)</b></td>'
    + '<td class="muted">gir 2k · çık 2.0M · ön.oku 430k · ön.yaz 350</td><td><b>$5.50</b></td><td></td></tr>');
  assert.equal(columns(html), 8);
  assert.match(panel.agentTotalRow(en, agentRows()), /<b>Total \(measured agents\)<\/b>/);
});

test("agentTotalRow: 'N cannot be measured yet' only when some agents are unmeasured", () => {
  assert.doesNotMatch(panel.agentTotalRow(tr, agentRows({ unmeasured: 0 })), /henüz/);
  assert.match(panel.agentTotalRow(tr, agentRows({ unmeasured: 3 })), /<b>\$5\.50<\/b><div class="muted">3 henüz ölçülemiyor<\/div><\/td>/);
  assert.match(panel.agentTotalRow(en, agentRows({ unmeasured: 1 })), /1 cannot be measured yet/);
});

test("agentTotalRow: an unpriced total says 'not measured'", () => {
  const html = panel.agentTotalRow(tr, agentRows({ total: summary({ cost: null, unpriced: ["m"] }) }));
  assert.match(html, /<b>ölçülemiyor<\/b>/);
});

test("roleCostText: empty without data or for a role that has no measured agent, ' · $x.xx' otherwise", () => {
  assert.equal(panel.roleCostText(tr, undefined, "analyst"), "");
  assert.equal(panel.roleCostText(tr, null, "analyst"), "");
  assert.equal(panel.roleCostText(tr, agentRows({ by_type: {} }), "analyst"), "");
  assert.equal(panel.roleCostText(tr, agentRows({ by_type: { qa: summary() } }), "analyst"), "");
  assert.equal(panel.roleCostText(tr, agentRows({ by_type: { analyst: summary({ cost: 2.5 }) } }), "analyst"), " · $2.50");
  assert.equal(panel.roleCostText(tr, agentRows({ by_type: { analyst: summary({ cost: null }) } }), "analyst"), " · ölçülemiyor");
});

test("view: the agents table has 8 columns, measured and unmeasured rows, and the total row last", () => {
  const agents = { a: agent("a"), b: agent("b", { started: ago(120), type: "qa" }) };
  const rows = agentRows({ rows: { a: measured(), b: NOT_YET }, unmeasured: 1 });
  const list = trs(ui.view(withCosts(agents, rows), tr, NOW, { lists: SHOW_ALL }).agentsHtml);
  assert.equal(list.length, 3);
  assert.match(list[0], /desc-a/);
  assert.match(list[1], /desc-b/);
  assert.match(list[2], /^<tr class="total">/);
  assert.match(list[0], /<b>\$1\.23<\/b>/);
  assert.match(list[1], /colspan="2">henüz ölçülemiyor/);
  assert.deepEqual(list.map(columns), [8, 8, 8]);  // colspan=2 keeps the unmeasured row as wide as the header
  assert.match(list[2], /1 henüz ölçülemiyor/);
});

test("view: a row that reached the page before its costs shows 'cannot be measured yet'", () => {
  const html = ui.view(withCosts({ a: agent("a") }, agentRows({ rows: {} })), tr, NOW, { lists: SHOW_ALL }).agentsHtml;
  assert.match(trs(html)[0], /colspan="2">henüz ölçülemiyor/);
});

test("view: an older server without costs still lists the agents and adds no total row", () => {
  const html = ui.view(state({ agents: { a: agent("a") } }), tr, NOW, { lists: SHOW_ALL }).agentsHtml;
  assert.equal(trs(html).length, 1);
  assert.match(html, /colspan="2">henüz ölçülemiyor/);
  assert.doesNotMatch(html, /class="total"/);
  const partial = ui.view(state({ agents: { a: agent("a") }, costs: { sessions: [] } }), tr, NOW, { lists: SHOW_ALL }).agentsHtml;
  assert.doesNotMatch(partial, /class="total"/);
});

test("view: the empty agents table spans all 8 columns and has no total row", () => {
  const html = ui.view(state(), tr, NOW).agentsHtml;
  assert.match(html, /<td colspan="8" class="empty">Henüz ajan başlatılmadı/);
  assert.doesNotMatch(html, /total/);
  const withEmptyCosts = ui.view(withCosts({}, agentRows()), tr, NOW, { lists: SHOW_ALL }).agentsHtml;
  assert.match(withEmptyCosts, /<td colspan="8" class="empty">/);
});

test("view: a denied agent keeps its reason and shows a dash, not 'cannot be measured yet'", () => {
  const denied = agent("d", { status: "denied", reason: "outside mode", type: "security" });
  const html = ui.view(withCosts({ d: denied }, agentRows({ rows: { d: NOT_YET }, unmeasured: 0 })), tr, NOW, { lists: SHOW_ALL }).agentsHtml;
  assert.match(html, /outside mode/);
  assert.match(trs(html)[0], /<td class="muted" colspan="2">—<\/td>/);
  assert.doesNotMatch(trs(html)[0], /henüz ölçülemiyor/);
});

test("view: agentsHelp has the undercount note and the T-2 note only when there is an agent", () => {
  const some = ui.view(withCosts({ a: agent("a") }, agentRows()), tr, NOW).agentsHelp;
  assert.match(some, /<p>Not: ajan transcript/);
  assert.match(some, /<p>Bir ajanın maliyeti[^<]*T-2&#39;ye bağlıdır\.<\/p>/);
  const english = ui.view(withCosts({ a: agent("a") }, agentRows()), en, NOW).agentsHelp;
  assert.match(english, /undercounted/);
  assert.match(english, /depend on T-2\./);
  assert.equal(ui.view(state(), tr, NOW).agentsHelp, "");
});

test("view: the roles panel appends a role's cost to its running count", () => {
  const agents = { a: agent("a", { status: "running", ended: null }), q: agent("q", { type: "qa" }) };
  const rows = agentRows({ rows: { a: measured(), q: NOT_YET }, by_type: { analyst: summary({ cost: 2.5 }) } });
  const v = ui.view({ ...withCosts(agents, rows), roles: ["analyst", "qa"] }, tr, NOW);
  assert.match(v.rolesHtml, /<span>analyst<\/span><span class="cnt">1 çalışan · \$2\.50<\/span>/);
  assert.match(v.rolesHtml, /<span>qa<\/span><span class="cnt">0 çalışan<\/span>/);
  const older = ui.view(state({ agents, roles: ["analyst"] }), tr, NOW);
  assert.match(older.rolesHtml, /<span class="cnt">1 çalışan<\/span>/);
});

test("the page writes agentsHelp and the agent rows into their elements", async () => {
  const st = withCosts({ a: agent("a") }, agentRows({ rows: { a: measured() } }));
  const page = fakePage({ server: { state: st } });
  await page.app.first;
  assert.match(page.els.agentsHelp.innerHTML, /T-2/);
  assert.match(page.els.agents.innerHTML, /class="total"/);
});

const many = (n) => Object.fromEntries(Array.from({ length: n }, (_, i) => [`k${i}`,
  agent(`k${i}`, { started: ago(1000 - i), description: `desc-${i}` })]));

test("view: a page holds 30 agents, newest first, and the total row counts every one", () => {
  const st = withCosts(many(35), agentRows({ total: summary({ cost: 99 }) }));
  const list = trs(ui.view(st, tr, NOW, { lists: SHOW_ALL }).agentsHtml);
  assert.equal(list.length, 31);                       // 30 rows + the total row
  assert.match(list[0], /desc-34/);                    // newest first
  assert.match(list[29], /desc-5</);                   // desc-0..4 are on the next page
  assert.doesNotMatch(list.join(""), /desc-4</);
  assert.match(list[30], /<b>\$99\.00<\/b>/);          // the total is the server's, over all 35
  const second = trs(ui.view(st, tr, NOW, { lists: { ...SHOW_ALL, agents: { page: 2, showDone: true } } }).agentsHtml);
  assert.equal(second.length, 6);                      // 5 rows + the same total row
  assert.match(second[0], /desc-4</);
  assert.match(second[5], /<b>\$99\.00<\/b>/);
});
