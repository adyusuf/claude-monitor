"use strict";
/* Live board UI (docs/live-board.md). Pure view functions plus start(), which wires
   them to a document. board.html loads it in the browser; scripts/tests/board_ui.test.js
   requires it in Node, so the same code is measured that the page runs. */
(function (root, factory) {
  const api = factory();
  if (typeof module === "object" && module.exports) {
    module.exports = api;
  } else {
    root.BoardView = api;
    api.start(root.document, root, root.fetch.bind(root));
  }
})(typeof window !== "undefined" ? window : globalThis, function () {
  const POLL_MS = 1500;
  const LANG_KEY = "board.lang";
  const PROJECT_KEY = "board.project";
  const LIVE = new Set(["starting", "running"]);
  const text = typeof module === "object" && module.exports
    ? require("./board_ui_text.js") : globalThis.BoardText;
  const panel = typeof module === "object" && module.exports
    ? require("./board_ui_sessions.js") : globalThis.BoardSessions;
  const { I18N, makeT, esc, fmtDate, fmtTime, fmtStamp, fmtDur, fill } = text;
  const SW_PATH = "/sw.js";

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

  /** The project tabs: name, live-agent dot, running count; the selected one marked. */
  function projectsHtml(projects, selected, t) {
    if (!projects.length) return `<span class="muted">${esc(t("noProjects"))}</span>`;
    return projects.map((p) => `<button class="tab${p.id === selected ? " on" : ""}" data-project="${esc(p.id)}"
      aria-pressed="${p.id === selected}"><span class="dot${p.turn_open ? " on" : ""}"></span>${esc(p.name)}
      <span class="cnt">${p.running}/${p.total}</span></button>`).join("");
  }

  /** Everything the page shows, computed from the server state; no DOM access. */
  function view(st, t, now = new Date(), extra = {}) {
    const skills = extra.skills || [];
    const drafts = extra.drafts || {};
    const taskCosts = (st.costs && st.costs.tasks) || {};
    const agentRows = (st.costs && st.costs.agent_rows) || { rows: {}, by_type: {}, unmeasured: 0, total: null };
    const tasks = Object.values(st.tasks);
    const agents = st.agents;
    const agentList = Object.values(agents).sort((a, b) => (b.started || "").localeCompare(a.started || ""));
    const sessions = Object.values(st.sessions);
    const turnOpen = sessions.some((s) => s.turn_open);
    const count = (pred) => tasks.filter(pred).length;
    const stats = [
      [t("stRunning"), count((x) => x.status === "running")],
      [t("stWaiting"), count((x) => x.status === "waiting" || x.status === "agent_done")],
      [t("stDecision"), count((x) => x.status === "needs_decision")],
      [t("stPlanned"), count((x) => x.status === "planned")],
      [t("stDone"), count((x) => x.status === "done")],
      [t("stRemoved"), count((x) => x.status === "removed")],
    ];
    const active = tasks.filter((x) => x.status !== "removed");
    const disabled = new Set(st.control.disabled_roles);
    // A role that was only ever DENIED gets no switch: it is not part of the mode.
    const started = agentList.filter((a) => a.status !== "denied").map((a) => a.type);
    const roleNames = [...new Set([...(st.roles || []), ...started, ...disabled])].filter(Boolean);
    return {
      mode: st.mode ?? "–",
      last: fmtStamp(st.last_event, now),
      turnOpen,
      turnText: !sessions.length ? t("noSession") : turnOpen ? t("turnOpen") : t("turnClosed"),
      statsHtml: stats.map(([l, n]) =>
        `<div class="stat"><div class="n">${n}</div><div class="l">${esc(l)}</div></div>`).join(""),
      allDone: active.length > 0 && active.every((x) => x.status === "done") &&
        !agentList.some((a) => LIVE.has(a.status)),
      rolesHtml: roleNames.map((r) => {
        const on = !disabled.has(r);
        const n = agentList.filter((a) => a.type === r && LIVE.has(a.status)).length;
        return `<div class="role${on ? "" : " off"}"><button class="switch" role="switch" aria-checked="${on}"
          aria-label="${esc(r)} ${esc(on ? t("roleOn") : t("roleOff"))}" data-role="${esc(r)}"></button>
          <span>${esc(r)}</span><span class="cnt">${n} ${esc(t("running"))}${esc(panel.roleCostText(t, st.costs && st.costs.agent_rows, r))}</span></div>`;
      }).join(""),
      sessionsHtml: panel.sessionsHtml(t, st, skills, drafts, now, extra.channelOnly),
      sessionsHelp: panel.helpHtml(t, st.costs, st.stop_wait_s),
      modesHtml: panel.modesHtml(t, st),
      tasksHtml: !tasks.length ? `<tr><td colspan="9" class="empty">${esc(t("noTasks"))}</td></tr>` :
        tasks.map((x) => {
          const [start, end] = taskSpan(x, agents);
          const removed = x.status === "removed";
          return `<tr class="${removed ? "removed" : ""}">
            <td><span class="tid">${esc(x.id)}</span><span class="title">${esc(x.title)}</span></td>
            <td>${x.branch ? `<code>${esc(x.branch)}</code>` : "—"}</td>
            <td>${esc(x.role || "—")}${liveAgents(x, agents)}</td><td>${badge(t, x.status)}</td>
            <td class="muted">${start ? esc(fmtDur(t, start, end, now)) : "—"}</td>
            <td>${mergeHtml(x)}</td><td>${panel.taskCostHtml(t, x, taskCosts[x.id])}</td>
            <td>${esc(x.note)}${decisionHtml(x, st.decision_defaults || [], t)}</td>
            <td><button class="act" data-task="${esc(x.id)}" data-action="${removed ? "restore_task" : "remove_task"}">
              ${esc(removed ? t("restore") : t("remove"))}</button></td></tr>`;
        }).join(""),
      agentsHtml: !agentList.length ? `<tr><td colspan="8" class="empty">${esc(t("noAgents"))}</td></tr>` :
        agentList.slice(0, 30).map((a) => `<tr><td>${esc(a.type)}</td><td>${esc(a.task || "—")}</td>
          <td>${badge(t, a.status)}</td><td class="muted">${esc(fmtStamp(a.started, now))}</td>
          <td class="muted">${esc(a.status === "denied" ? "—" : fmtDur(t, a.started, a.ended, now))}</td>
          ${a.status === "denied" ? `<td class="muted" colspan="2">—</td>` : panel.agentCostCells(t, agentRows.rows[a.key])}
          <td>${esc(a.reason || a.description)}</td></tr>`).join("") + panel.agentTotalRow(t, agentRows),
      agentsHelp: agentList.length ? `<p>${esc(t("undercount"))}</p><p>${esc(t("agentRowsNote"))}</p>` : "",
    };
  }

  function readKey(win, key) {
    try { return win.localStorage.getItem(key); } catch { return null; }
  }

  function writeKey(win, key, value) {
    try { win.localStorage.setItem(key, value); } catch { /* per-viewer convenience only */ }
  }

  /** Wires the view to a document: polling, controls, language switch. */
  function start(doc, win, fetchImpl) {
    const stored = readKey(win, LANG_KEY);
    let lang = I18N[stored] ? stored : "tr";  // tr is the primary locale
    let t = makeT(lang);
    let lastState = null;
    let project = readKey(win, PROJECT_KEY);
    let skills = [];
    const drafts = { queue: {}, skill: {} };  // what the user typed or picked, kept across redraws
    const $ = (id) => doc.getElementById(id);
    const focused = (key) => Boolean(doc.activeElement && doc.activeElement.dataset &&
      doc.activeElement.dataset[key]);

    function applyStatic() {
      doc.documentElement.lang = lang;
      doc.querySelectorAll("[data-i18n]").forEach((el) => { el.textContent = t(el.dataset.i18n); });
      doc.title = t("title");
      $("lang").textContent = lang === "tr" ? "EN" : "TR";
    }

    function render(st) {
      lastState = st;
      const v = view(st, t, new Date(), { skills, drafts, channelOnly: Boolean($("channelOnly").checked) });
      $("mode").textContent = v.mode;
      $("modes").innerHTML = v.modesHtml;
      $("last").textContent = v.last;
      $("dot").className = "dot" + (v.turnOpen ? " on" : "");
      $("turn").textContent = v.turnText;
      $("stats").innerHTML = v.statsHtml;
      $("banner").classList.toggle("show", v.allDone);
      $("roles").innerHTML = v.rolesHtml;
      // Typing a note: do not redraw the table under the cursor, or the text is lost.
      if (!focused("noteFor")) $("tasks").innerHTML = v.tasksHtml;
      if (!focused("queueFor") && !focused("skillFor")) $("sessions").innerHTML = v.sessionsHtml;
      $("sessionsHelp").innerHTML = v.sessionsHelp;
      $("agents").innerHTML = v.agentsHtml;
      $("agentsHelp").innerHTML = v.agentsHelp;
    }

    async function refresh() {
      try {
        const projects = await (await fetchImpl("/api/projects", { cache: "no-store" })).json();
        if (!projects.some((p) => p.id === project)) project = projects.length ? projects[0].id : null;
        $("projects").innerHTML = projectsHtml(projects, project, t);
        if (!project) { $("err").textContent = ""; return; }
        const res = await fetchImpl(`/api/state?p=${encodeURIComponent(project)}`, { cache: "no-store" });
        render(await res.json());
        $("err").textContent = "";
      } catch (err) {
        $("err").textContent = t("unreachable");
        win.console.error("board refresh failed", err);
      }
    }

    async function control(action, value, extra = {}) {
      const res = await fetchImpl("/api/control", {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ action, value, project, ...extra }),
      });
      const refused = res.ok ? "" : (await res.json()).error;
      await refresh();  // refresh clears the error line, so a refusal is written after it
      if (refused) $("err").textContent = refused;
      return !refused;
    }

    /** Send what the user typed or picked for a session; the draft comes back if refused. */
    async function sendTo(kind, sid, action, value, extra) {
      const kept = drafts[kind][sid];
      delete drafts[kind][sid];
      const ok = await control(action, value, extra);
      if (!ok && kept !== undefined) drafts[kind][sid] = kept;
      return ok;
    }

    function onInput(e) {
      const d = e.target.dataset || {};
      if (e.target.id === "channelOnly" && lastState) render(lastState);
      if (d.queueFor) drafts.queue[d.queueFor] = e.target.value;
      if (d.skillFor) drafts.skill[d.skillFor] = e.target.value;
    }

    function onClick(e) {
      if (e.target.id === "lang") {
        lang = lang === "tr" ? "en" : "tr";
        t = makeT(lang);
        writeKey(win, LANG_KEY, lang);
        applyStatic();
        if (lastState) render(lastState);
        return null;
      }
      const tab = e.target.closest("[data-project]");
      if (tab) {
        project = tab.dataset.project;
        writeKey(win, PROJECT_KEY, project);
        return refresh();
      }
      const pick = e.target.closest("[data-choice]");
      if (pick) {
        const task = pick.closest("[data-decide-task]").dataset.decideTask;
        const noteEl = doc.querySelector(`[data-note-for="${task}"]`);
        return control("decide", task, { choice: pick.dataset.choice, note: noteEl ? noteEl.value : "" });
      }
      const queue = e.target.closest("[data-queue-send]");
      if (queue) {
        const sid = queue.dataset.queueSend;
        const input = doc.querySelector(`[data-queue-for="${sid}"]`);
        return sendTo("queue", sid, "queue_task", sid, { text: input ? input.value : "" })
          .then((ok) => { if (ok && input) input.value = ""; return ok; });  // sent: the field empties
      }
      const run = e.target.closest("[data-skill-run]");
      if (run) {
        const sid = run.dataset.skillRun;
        const pickEl = doc.querySelector(`[data-skill-for="${sid}"]`);
        if (!pickEl || !pickEl.value) { $("err").textContent = t("pickSkill"); return null; }
        return sendTo("skill", sid, "run_skill", pickEl.value, { session: sid });
      }
      const mode = e.target.closest("[data-mode]");
      if (mode) {
        // A mode costs 1x-14x (modes/README.md): one misclick must not switch it.
        if (!win.confirm(fill(t("confirmMode"), { mode: mode.dataset.mode }))) return null;
        return control("set_mode", mode.dataset.mode);
      }
      const sw = e.target.closest("[data-role]");
      if (sw) return control(sw.getAttribute("aria-checked") === "true" ? "disable_role" : "enable_role", sw.dataset.role);
      const btn = e.target.closest("[data-task]");
      return btn ? control(btn.dataset.action, btn.dataset.task) : null;
    }

    doc.addEventListener("click", onClick);
    doc.addEventListener("input", onInput);
    doc.addEventListener("change", onInput);
    applyStatic();
    const skillsLoaded = fetchImpl("/api/skills", { cache: "no-store" }).then((r) => r.json())
      .then((list) => { skills = Array.isArray(list) ? list : []; })
      .catch((err) => win.console.error("board skills failed", err));
    const sw = win.navigator && win.navigator.serviceWorker;
    const swReady = sw ? sw.register(SW_PATH).catch((err) => win.console.error("service worker failed", err))
      : Promise.resolve();
    const first = skillsLoaded.then(refresh);
    win.setInterval(refresh, POLL_MS);
    return { first, swReady, refresh, control, onClick, onInput, drafts, lang: () => lang,
      project: () => project, skills: () => skills };
  }

  return { I18N, makeT, esc, fmtDate, fmtTime, fmtStamp, fmtDur, view, projectsHtml, decisionHtml, mergeHtml, start };
});
