"use strict";
/* The live board's table lists (docs/live-board.md §2h): which rows come first, which are hidden
   until asked for, and the 10-row pages. Pure functions plus the small page / toggle state the page
   keeps between redraws; no DOM access. Newest first, by date alone. */
(function (root, factory) {
  const api = factory(typeof module === "object" && module.exports
    ? require("./board_ui_text.js") : root.BoardText);
  if (typeof module === "object" && module.exports) {
    module.exports = api;
  } else {
    root.BoardLists = api;
  }
})(typeof window !== "undefined" ? window : globalThis, function (text) {
  const { esc, fill } = text;
  const PAGE_SIZE = 10;
  const TABLES = ["tasks", "agents", "sessions"];

  const FINISHED_TASK = new Set(["done", "removed"]);
  const FINISHED_AGENT = new Set(["done"]);

  const idNumber = (id) => Number(String(id).replace(/\D/g, "")) || 0;

  // ISO-8601 stamps of one format compare correctly as plain strings; no stamp sorts last.
  const newer = (sa, sb) => (sb > sa) - (sb < sa);

  /** A copy of `list`, newest stamp first, the higher id first on a tie. A row with no stamp sorts last. */
  function order(list, stamp, id) {
    return [...list].sort((a, b) => newer(stamp(a) || "", stamp(b) || "") || idNumber(id(b)) - idNumber(id(a)));
  }

  const orderTasks = (list) => order(list, (x) => x.updated || x.started, (x) => x.id);
  const orderAgents = (list) => order(list, (a) => a.started, (a) => a.key);
  const orderSessions = (list) => order(list, (s) => s.since, (s) => s.id);

  const isFinishedTask = (x) => FINISHED_TASK.has(x.status);
  const isFinishedAgent = (a) => FINISHED_AGENT.has(a.status);

  /** One page of `list`: the rows, the page number held inside 1..pages, and the counts. */
  function paginate(list, page, size = PAGE_SIZE) {
    const pages = Math.max(1, Math.ceil(list.length / size));
    const at = Math.min(Math.max(1, Math.floor(Number(page)) || 1), pages);
    return { rows: list.slice((at - 1) * size, at * size), page: at, pages, total: list.length };
  }

  /** Orders `list`, hides finished rows unless `showDone`, and cuts the page asked for. */
  function select(list, { order: sort, finished, state }) {
    const ordered = sort(list);
    const shown = finished && !state.showDone ? ordered.filter((x) => !finished(x)) : ordered;
    return { ...paginate(shown, state.page), hidden: ordered.length - shown.length, all: ordered.length };
  }

  /** The line under a table: previous / next buttons and where the reader is. Empty when there is
      one page and nothing hidden. */
  function pagerHtml(t, key, view) {
    const hidden = view.hidden ? `${esc(fill(t("hiddenDone"), { n: view.hidden }))}` : "";
    if (view.pages <= 1) return hidden ? `<span class="muted">${hidden}</span>` : "";
    const button = (dir, label, off) => `<button class="act" type="button" data-page="${key}" data-dir="${dir}"${
      off ? " disabled" : ""}>${esc(t(label))}</button>`;
    return `${button(-1, "pagePrev", view.page <= 1)} <span class="muted">${esc(fill(t("pageOf"), {
      p: view.page, n: view.pages, total: view.total }))}${hidden ? ` · ${hidden}` : ""}</span> ${
      button(1, "pageNext", view.page >= view.pages)}`;
  }

  /** The page and toggle state of the three tables, kept by the page between redraws. */
  function makeLists() {
    const fresh = () => ({ tasks: { page: 1, showDone: false }, agents: { page: 1, showDone: false },
      sessions: { page: 1 } });
    let state = fresh();
    return {
      state: () => state,
      reset: () => { state = fresh(); },
      turn: (key, dir) => { if (state[key]) state[key].page = Math.max(1, state[key].page + (Number(dir) || 0)); },
      showDone: (key, on) => { if (state[key]) { state[key].showDone = Boolean(on); state[key].page = 1; } },
      firstPage: (key) => { if (state[key]) state[key].page = 1; },
      // The view clamps a page that no longer exists (the table shrank); keep the clamped number.
      remember: (pages) => { for (const key of TABLES) if (pages && pages[key]) state[key].page = pages[key]; },
    };
  }

  return { PAGE_SIZE, TABLES, orderTasks, orderAgents, orderSessions, isFinishedTask, isFinishedAgent,
    paginate, select, pagerHtml, makeLists };
});
