"use strict";
/* The live board's sessions panel and cost / ETA cells (docs/live-board-sessions.md §2c, docs/live-board.md §2d).
   Pure functions from the server's `costs` block to HTML; no DOM access. Every projection is
   labelled an estimate with its basis; a figure the server could not measure says so. */
(function (root, factory) {
  const api = factory(typeof module === "object" && module.exports
    ? require("./board_ui_text.js") : root.BoardText);
  if (typeof module === "object" && module.exports) {
    module.exports = api;
  } else {
    root.BoardSessions = api;
  }
})(typeof window !== "undefined" ? window : globalThis, function (text) {
  const { esc, fmtDur, fmtStamp, fill } = text;
  const CENT = 0.01;
  const MILLION = 1e6;
  const THOUSAND = 1e3;
  const PERCENT = 100;

  const fmtUsd = (t, x) => (x == null ? t("notMeasured") : x > 0 && x < CENT ? "<$0.01" : `$${x.toFixed(2)}`);

  function fmtTok(n) {
    if (n >= MILLION) return `${(n / MILLION).toFixed(1)}M`;
    if (n >= THOUSAND) return `${Math.round(n / THOUSAND)}k`;
    return String(n);
  }

  const tokSum = (tokens) => Object.values(tokens || {}).reduce((a, b) => a + b, 0);
  const muted = (html) => `<div class="muted">${html}</div>`;

  function stateHtml(t, s, now) {
    const channel = s.channel
      ? ` <span class="badge ch" title="${esc(t("channelTitle"))}">${esc(t("channel"))}</span>` : "";
    const todo = s.todos
      ? muted(`${esc(t("todos"))} ${s.todos.done}/${s.todos.total}${s.todos.current ? ` · ${esc(s.todos.current)}` : ""}`) : "";
    return `<span class="badge st-${esc(s.state)}">${esc(t(s.state))} ${
      esc(fmtDur(t, s.since, null, now))}</span>${channel}${todo}`;
  }

  function contextHtml(t, s) {
    const c = s.context;
    if (!c) return esc(t("notMeasured"));
    if (c.ratio == null) return `${esc(fmtTok(c.tokens))} · ${esc(t("notMeasured"))}`;
    const warn = s.context_warn ? ` <span class="badge warn">${esc(t("compact"))}</span>` : "";
    return `${Math.round(c.ratio * PERCENT)}% · ${esc(fmtTok(c.tokens))}/${esc(fmtTok(c.window))}${warn}`;
  }

  /** A summary's cost with its tokens underneath; unpriced models are named, never priced. */
  function summaryHtml(t, sum) {
    if (!sum) return esc(t("notMeasured"));
    const unpriced = sum.unpriced.length
      ? muted(`${esc(t("unpriced"))}: ${sum.unpriced.map(esc).join(", ")}`) : "";
    return `<b>${esc(fmtUsd(t, sum.cost))}</b>${muted(`${esc(fmtTok(tokSum(sum.tokens)))} ${esc(t("tokens"))}`)}${unpriced}`;
  }

  function costHtml(t, s) {
    const parts = `${esc(t("orchestration"))} ${esc(fmtUsd(t, s.orchestration ? s.orchestration.cost : null))} · ${
      esc(t("agentsCost"))} (${s.subagents}) ${esc(fmtUsd(t, s.agents.cost))}`;
    return `${summaryHtml(t, s.total)}${muted(parts)}`;
  }

  function projectionHtml(t, s, basis) {
    if (s.projected == null) return esc(t("notMeasured"));
    const why = fill(t("basisSession"), { h: basis.remaining_h, n: basis.eta_tasks, m: basis.no_eta_tasks });
    return `≈ ${esc(fmtUsd(t, s.projected))}${muted(esc(why))}`;
  }

  function sendHtml(t, s, skills, drafts, max) {
    const id = esc(s.id);
    const draft = (drafts.queue || {})[s.id] || "";
    const picked = (drafts.skill || {})[s.id] || "";
    const options = skills.map((k) => `<option value="${esc(k)}"${k === picked ? " selected" : ""}>${esc(k)}</option>`);
    const queued = s.queued && s.queued.length ? muted(`${esc(t("queued"))}: ${s.queued.length}`) : "";
    return `<div class="send"><input class="dnote" data-queue-for="${id}" maxlength="${Number(max) || 0}"
      placeholder="${esc(t("queuePh"))}" value="${esc(draft)}"><button class="act" data-queue-send="${id}">${esc(t("send"))}</button></div>
      <div class="send"><select data-skill-for="${id}"><option value="">${esc(t("pickSkill"))}</option>${options.join("")}</select>
      <button class="act" data-skill-run="${id}">${esc(t("runSkill"))}</button></div>${queued}`;
  }

  const reachable = (costs) => ((costs && costs.sessions) || []).filter((s) => s.channel);

  /** The sessions table body: one row per session the server shows, or only the ones a channel
      server can reach when `channelOnly` (the page's checkbox). `rows` is the already ordered page. */
  function sessionsHtml(t, st, skills, drafts, now, channelOnly = false, rows = null) {
    const costs = st.costs;
    const list = rows || (channelOnly ? reachable(costs) : (costs && costs.sessions) || []);
    if (!list.length) return `<tr><td colspan="7" class="empty">${esc(t(channelOnly ? "noChannelSessions" : "noSessions"))}</td></tr>`;
    return list.map((s) => `<tr><td>${s.title ? `<div class="sname" title="${esc(s.title)}">${esc(s.title)}</div>` : ""}<code title="${esc(s.id)}">${esc(s.id.slice(0, 8))}</code></td>
      <td>${stateHtml(t, s, now)}</td><td>${contextHtml(t, s)}</td><td>${costHtml(t, s)}</td>
      <td>${esc(fmtUsd(t, s.rate_per_h))}${s.rate_per_h == null ? "" : esc(t("perHour"))}</td>
      <td>${projectionHtml(t, s, costs.basis)}</td><td>${sendHtml(t, s, skills, drafts, st.queue_text_max)}</td></tr>`).join("");
  }

  /** The notes under the sessions table: what the board cannot do, said plainly. */
  function helpHtml(t, costs, waitS) {
    const undercount = ((costs && costs.sessions) || []).some((s) => s.subagents > 0)
      ? `<p>${esc(t("undercount"))}</p>` : "";
    const channel = fill(t("helpChannel"), { n: reachable(costs).length, name: "board-channel" });
    return `<p>${esc(t("helpCompact"))}</p><p>${esc(fill(t("helpQueue"), { s: waitS }))}</p><p>${esc(channel)}</p>${undercount}`;
  }

  /** A task's Cost / ETA cell: spent by its agents, time left, the user's estimate, projection. */
  /** The orchestrator's share of a task: the linked session's own transcript inside the task's time window.
      Always labelled an estimate with its basis, and says how many other tasks share that window. */
  function orchestrationHtml(t, o) {
    const s = o.summary;
    const basis = fill(t("orchBasis"), { s: o.sessions.map((id) => id.slice(0, 8)).join(", ") });
    const shared = o.shared ? ` · ${fill(t("orchShared"), { n: o.shared })}` : "";
    return `${esc(t("orchEst"))} <b>${esc(fmtUsd(t, s.cost))}</b> · ${esc(fmtTok(tokSum(s.tokens)))} ${esc(t("tokens"))}`
      + muted(`${esc(basis)}${esc(shared)}`);
  }

  function taskCostHtml(t, task, cost) {
    if (!cost) return "—";
    const orch = cost.orchestration;
    let spent = esc(t("noAgentLinked"));
    if (cost.spent) spent = `${esc(t("spent"))} <b>${esc(fmtUsd(t, cost.spent.cost))}</b> · ${esc(fmtTok(tokSum(cost.spent.tokens)))} ${esc(t("tokens"))}`;
    else if (cost.agents) spent = `${esc(t("spent"))} ${esc(t("notMeasured"))}`;
    // No agent linked but the orchestrating session is known: that estimate replaces "no agent linked".
    const lines = !cost.spent && !cost.agents && orch ? [] : [spent];
    if (orch) lines.push(orchestrationHtml(t, orch));
    if (cost.remaining_min != null) lines.push(`${esc(t("etaLeft"))} ${Math.round(cost.remaining_min)} ${esc(t("min"))}`);
    if (task.est_cost != null) {
      const left = cost.est_left == null ? "" : ` · ${esc(t("left"))} ${esc(fmtUsd(t, cost.est_left))}`;
      const over = cost.over_estimate ? ` <span class="badge warn">${esc(t("overEst"))}</span>` : "";
      lines.push(`${esc(t("estCost"))} ${esc(fmtUsd(t, task.est_cost))}${left}${over}`);
    }
    if (cost.projected != null) {
      lines.push(`${esc(t("projected"))} ${esc(fmtUsd(t, cost.projected))} <span class="muted">(${esc(t("basisTask"))})</span>`);
    }
    return lines.map((l, i) => (i ? muted(l) : l)).join("");
  }

  /** in / out / cache read / cache write, each abbreviated the same way everywhere. */
  function tokenBreakdown(t, tokens) {
    const write = (tokens.cache_write_5m || 0) + (tokens.cache_write_1h || 0);
    return [[t("tokIn"), tokens.input], [t("tokOut"), tokens.output], [t("tokCacheR"), tokens.cache_read],
      [t("tokCacheW"), write]].map(([label, n]) => `${esc(label)} ${esc(fmtTok(n || 0))}`).join(" · ");
  }

  /** An Agent-activity row's Tokens and Cost cells. Not measured = no agent id or no transcript yet. */
  function agentCostCells(t, row) {
    if (!row || !row.measured) return `<td class="muted" colspan="2">${esc(t("notMeasuredYet"))}</td>`;
    const s = row.summary;
    const unpriced = s.unpriced.length ? muted(`${esc(t("unpriced"))}: ${s.unpriced.map(esc).join(", ")}`) : "";
    return `<td class="muted">${tokenBreakdown(t, s.tokens)}</td><td><b>${esc(fmtUsd(t, s.cost))}</b>${unpriced}</td>`;
  }

  /** The closing row of the Agent-activity table: every measured agent, and how many are not. */
  function agentTotalRow(t, agentRows) {
    if (!agentRows || !agentRows.total) return "";  // an older server sends no costs
    const s = agentRows.total;
    const missing = agentRows.unmeasured ? muted(`${agentRows.unmeasured} ${esc(t("notMeasuredYet"))}`) : "";
    return `<tr class="total"><td colspan="5"><b>${esc(t("total"))}</b></td><td class="muted">${
      tokenBreakdown(t, s.tokens)}</td><td><b>${esc(fmtUsd(t, s.cost))}</b>${missing}</td><td></td></tr>`;
  }

  /** The Agent-activity table body: the page's rows (already ordered), then the total row, which
      counts every agent, not only this page. A denied agent never ran, so its cost cells are a dash.
      `emptyRow` is shown when the page has no rows. */
  function agentsTableHtml(t, agentList, agentRows, badge, now, emptyRow) {
    if (!agentList.length) return emptyRow + agentTotalRow(t, agentRows);
    const rows = agentList.map((a) => `<tr><td>${esc(a.type)}</td><td>${esc(a.task || "—")}</td>
      <td>${badge(a.status)}</td><td class="muted">${esc(fmtStamp(a.started, now))}</td>
      <td class="muted">${esc(a.status === "denied" ? "—" : fmtDur(t, a.started, a.ended, now))}</td>
      ${a.status === "denied" ? `<td class="muted" colspan="2">—</td>` : agentCostCells(t, agentRows.rows[a.key])}
      <td>${esc(a.reason || a.description)}</td></tr>`).join("");
    return rows + agentTotalRow(t, agentRows);
  }

  /** The notes under the Agent-activity table. */
  function agentsHelpHtml(t, count) {
    return count ? `<p>${esc(t("undercount"))}</p><p>${esc(t("agentRowsNote"))}</p>` : "";
  }

  /** What a role's agents have cost so far, for the roles panel; empty until one is measured. */
  function roleCostText(t, agentRows, role) {
    const s = agentRows && agentRows.by_type[role];
    return s ? ` · ${fmtUsd(t, s.cost)}` : "";
  }

  /** Mode buttons A-E; the active one is marked. */
  function modesHtml(t, st) {
    const note = st.mode_by === "board" ? ` <span class="muted">(${esc(t("modeByBoard"))})</span>` : "";
    return `<span class="muted">${esc(t("modeSwitch"))}</span> ${(st.modes || []).map((m) =>
      `<button class="act mode${m === st.mode ? " on" : ""}" data-mode="${esc(m)}" aria-pressed="${m === st.mode}">${esc(m)}</button>`).join("")}${note}`;
  }

  return { fmtUsd, fmtTok, tokenBreakdown, agentCostCells, agentTotalRow, roleCostText, agentsTableHtml, agentsHelpHtml, sessionsHtml, helpHtml, taskCostHtml, modesHtml, contextHtml, costHtml, projectionHtml };
});
