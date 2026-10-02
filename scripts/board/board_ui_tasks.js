"use strict";
/* The live board's tasks table rows and their helpers (docs/live-board.md). Pure functions from the
   server's state to HTML; no DOM access. */
(function (root, factory) {
  const node = typeof module === "object" && module.exports;
  const api = factory(node ? require("./board_ui_text.js") : root.BoardText,
    node ? require("./board_ui_sessions.js") : root.BoardSessions);
  if (node) {
    module.exports = api;
  } else {
    root.BoardTasks = api;
  }
})(typeof window !== "undefined" ? window : globalThis, function (text, panel) {
  const { esc, fmtDur } = text;
  const LIVE = new Set(["starting", "running"]);
  // A task the board cannot tell the state of: parked, handed back, or a status this page has never seen.
  const KNOWN = new Set(["planned", "running", "agent_done", "waiting", "done", "failed", "removed", "needs_decision"]);
  const ASKABLE = new Set(["waiting", "agent_done"]);
  const askable = (task) => ASKABLE.has(task.status) || !KNOWN.has(task.status);

  const badge = (t, status) => `<span class="badge s-${esc(status)}">${esc(t("s_" + status))}</span>`;

  function taskSpan(task, agents) {
    const own = task.agents.map((k) => agents[k]).filter(Boolean);
    if (!own.length) return [null, null];
    const start = own.map((a) => a.started).sort()[0];
    const live = own.some((a) => LIVE.has(a.status));
    const end = live ? null : own.map((a) => a.ended).filter(Boolean).sort().pop();
    return [start, end];
  }

  function liveAgents(task, agents) {
    const live = task.agents.map((k) => agents[k]).filter((a) => a && LIVE.has(a.status));
    return live.length ? `<div class="live-agents">${live.map((a) =>
      `<span class="agent-chip"><span class="dot on"></span>${esc(a.type)}</span>`).join("")}</div>` : "";
  }

  /** A question Claude put on the board: its choices and a note field, or the answer given. */
  function decisionHtml(task, defaults, t) {
    if (task.status !== "needs_decision") return "";
    if (task.decision) {
      return `<div class="decided">${esc(t("decided"))}: <b>${esc(task.decision.choice)}</b>${
        task.decision.note ? ` — ${esc(task.decision.note)}` : ""}</div>`;
    }
    const choices = task.options && task.options.length
      ? task.options.map((o) => [o, o]) : defaults.map((d) => [d, t(d)]);
    return `<div class="decide" data-decide-task="${esc(task.id)}">${choices.map(([value, label]) =>
      `<button class="act" data-choice="${esc(value)}">${esc(label)}</button>`).join("")}
      <input class="dnote" data-note-for="${esc(task.id)}" maxlength="500" placeholder="${esc(t("notePh"))}"></div>`;
  }

  /** Where the task's commits are, per branch, as the server read it from git. */
  function mergeHtml(task) {
    const entries = Object.entries(task.merged || {}).filter(([, ok]) => ok !== null);
    return entries.length ? entries.map(([branch, ok]) =>
      `<span class="mg${ok ? " on" : ""}">${esc(branch)} ${ok ? "✓" : "—"}</span>`).join(" ") : "—";
  }

  /** The session that touched the task last, or null: the one a status question is queued for. */
  function askSession(task) {
    const seen = Object.entries(task.sessions || {});
    seen.sort((a, b) => String((b[1] || {}).last || "").localeCompare(String((a[1] || {}).last || "")));
    return seen.length ? seen[0][0] : null;
  }

  /** "Ask for the status" under a waiting or unclear task: queues a question for its session. */
  function askHtml(task, t) {
    if (!askable(task)) return "";
    const sid = askSession(task);
    if (!sid) return `<button class="act ask" type="button" disabled title="${esc(t("askNoSession"))}">${esc(t("askStatus"))}</button>`;
    return `<button class="act ask" type="button" data-ask-task="${esc(task.id)}" data-ask-session="${esc(sid)}"
      title="${esc(t("askStatusTitle"))}">${esc(t("askStatus"))}</button>`;
  }

  /** The tasks table body for the given rows (already ordered and cut to the page). */
  function taskRowsHtml(t, rows, st, now) {
    const taskCosts = (st.costs && st.costs.tasks) || {};
    return rows.map((x) => {
      const [start, end] = taskSpan(x, st.agents);
      const removed = x.status === "removed";
      return `<tr class="${removed ? "removed" : ""}">
        <td><span class="tid">${esc(x.id)}</span><span class="title">${esc(x.title)}</span></td>
        <td>${x.branch ? `<code>${esc(x.branch)}</code>` : "—"}</td>
        <td>${esc(x.role || "—")}${liveAgents(x, st.agents)}</td><td>${badge(t, x.status)}</td>
        <td class="muted">${start ? esc(fmtDur(t, start, end, now)) : "—"}</td>
        <td>${mergeHtml(x)}</td><td>${panel.taskCostHtml(t, x, taskCosts[x.id])}</td>
        <td>${esc(x.note)}${decisionHtml(x, st.decision_defaults || [], t)}</td>
        <td><button class="act" data-task="${esc(x.id)}" data-action="${removed ? "restore_task" : "remove_task"}">
          ${esc(removed ? t("restore") : t("remove"))}</button>${askHtml(x, t)}</td></tr>`;
    }).join("");
  }

  return { LIVE, badge, decisionHtml, mergeHtml, askSession, askHtml, taskRowsHtml };
});
