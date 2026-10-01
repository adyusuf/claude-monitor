"use strict";
// Shared fixtures for the live board's page tests: a fake document and window, and a
// fetch that answers like the v2 server. Lives under tests/, so it is outside the
// coverage denominator (scripts/coverage.sh excludes scripts/tests/**).
const path = require("node:path");
const ui = require(path.join(__dirname, "..", "board", "board_ui.js"));

const tr = ui.makeT("tr");
const en = ui.makeT("en");
const NOW = new Date(2026, 8, 29, 10, 0, 0);
const iso = (d) => d.toISOString();
const ago = (sec) => iso(new Date(NOW.getTime() - sec * 1000));

function state(over = {}) {
  return {
    mode: "C", roles: ["analyst", "developer"], last_event: iso(NOW),
    tasks: {}, agents: {}, sessions: {}, control: { removed_tasks: [], disabled_roles: [] },
    ...over,
  };
}

const PROJECTS = [
  { id: "aaaaaaaaaa", name: "alpha", running: 1, total: 2, turn_open: true },
  { id: "bbbbbbbbbb", name: "beta", running: 0, total: 1, turn_open: false },
];

const SKILLS = ["brainstorm", "review"];

// Answers like the v2 server: the project list, the skill list, a project's state, and controls.
// over.skills replaces the skill answer (any JSON); over.state replaces the served state.
const serverLike = (calls, over = {}) => async (url, opts) => {
  calls.push([url, opts]);
  if (url === "/api/projects") return { ok: true, json: async () => over.projects ?? PROJECTS };
  if (url === "/api/skills") return { ok: true, json: async () => ("skills" in over ? over.skills : SKILLS) };
  if (url === "/api/control") return over.control ?? { ok: true, json: async () => ({ version: 1 }) };
  return { ok: true, json: async () => over.state ?? state({ sessions: { s: { turn_open: true } } }) };
};

// confirm: what win.confirm answers (messages are recorded in `confirms`).
// serviceWorker: undefined = no navigator at all; "none" = a navigator without serviceWorker;
// "ok" or "fail" = register() resolves or rejects (each path is recorded in `swCalls`).
function fakePage({ stored = {}, storageThrows = false, fetchImpl, server, notes = {}, active = null,
  confirm = true, serviceWorker } = {}) {
  const els = {};
  const el = (id) => (els[id] ||= { id, textContent: "", innerHTML: "", className: "",
    shown: null, classList: { toggle: (_c, on) => { els[id].shown = on; } } });
  const labels = [{ dataset: { i18n: "title" }, textContent: "" }];
  const handlers = {};
  const fields = {};
  const doc = {
    documentElement: {}, title: "",
    getElementById: el,
    activeElement: active,
    // One element per selector, so a test can see what the page wrote back into it.
    querySelector: (sel) => (sel in notes ? (fields[sel] ||= { value: notes[sel] }) : null),
    querySelectorAll: () => labels,
    addEventListener: (type, fn) => { (handlers[type] ||= []).push(fn); },
  };
  const saved = {};
  const errors = [];
  const confirms = [];
  const swCalls = [];
  const win = {
    setInterval: () => 0,
    confirm: (message) => { confirms.push(message); return confirm; },
    console: { error: (...a) => errors.push(a) },
    get localStorage() {
      if (storageThrows) throw new Error("blocked");
      return { getItem: (k) => stored[k] ?? null, setItem: (k, v) => { saved[k] = v; } };
    },
  };
  if (serviceWorker === "none") win.navigator = {};  // a navigator without serviceWorker support
  else if (serviceWorker) {
    win.navigator = { serviceWorker: { register: (path) => {
      swCalls.push(path);
      return serviceWorker === "fail" ? Promise.reject(new Error("sw refused")) : Promise.resolve({});
    } } };
  }
  const calls = [];
  const fetchFn = fetchImpl || serverLike(calls, server);
  const app = ui.start(doc, win, fetchFn);
  const fire = (type, event) => (handlers[type] || []).map((fn) => fn(event));
  return { app, els, fields, labels, doc, saved, errors, calls, confirms, swCalls, handlers,
    click: (t) => fire("click", { target: t })[0],
    input: (t) => fire("input", { target: t }),
    change: (t) => fire("change", { target: t }) };
}

// The lists state with finished rows shown (they are hidden by default): view(st, t, now, { lists: SHOW_ALL }).
const SHOW_ALL = { tasks: { page: 1, showDone: true }, agents: { page: 1, showDone: true }, sessions: { page: 1 } };

const target = (over = {}) => ({ id: "", closest: () => null, ...over });

module.exports = { ui, tr, en, NOW, iso, ago, state, PROJECTS, SKILLS, SHOW_ALL, serverLike, fakePage, target };
