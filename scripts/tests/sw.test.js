"use strict";
// The live board's service worker (scripts/board/sw.js, docs/live-board.md §2e):
// installable, but it caches nothing — every request goes to the local server.
// Run: node --test scripts/tests/  — measured by scripts/coverage.sh (#29).
const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");
const { wire } = require(path.join(__dirname, "..", "board", "sw.js"));

// A fake ServiceWorkerGlobalScope that records every call the worker makes on it.
function fakeScope() {
  const listeners = {};
  const calls = { skipWaiting: 0, claim: 0, fetched: [] };
  const scope = {
    addEventListener: (type, fn) => { listeners[type] = fn; },
    skipWaiting: () => { calls.skipWaiting += 1; return "skipped"; },
    clients: { claim: () => { calls.claim += 1; return "claimed"; } },
    fetch: (request) => { calls.fetched.push(request); return `response for ${request}`; },
  };
  return { scope, listeners, calls };
}

test("wire registers exactly the install, activate and fetch listeners", () => {
  const { scope, listeners } = fakeScope();
  wire(scope);
  assert.deepEqual(Object.keys(listeners).sort(), ["activate", "fetch", "install"]);
});

test("install takes over at once: skipWaiting is called", () => {
  const { scope, listeners, calls } = fakeScope();
  wire(scope);
  assert.equal(calls.skipWaiting, 0);
  listeners.install({});
  assert.equal(calls.skipWaiting, 1);
});

test("activate claims the open pages and waits on that claim", () => {
  const { scope, listeners, calls } = fakeScope();
  wire(scope);
  const waited = [];
  listeners.activate({ waitUntil: (p) => waited.push(p) });
  assert.equal(calls.claim, 1);
  assert.deepEqual(waited, ["claimed"]);
});

test("fetch goes straight to the network: the request is forwarded and nothing is cached", () => {
  const { scope, listeners, calls } = fakeScope();
  wire(scope);
  const responded = [];
  listeners.fetch({ request: "GET /api/state", respondWith: (r) => responded.push(r) });
  assert.deepEqual(calls.fetched, ["GET /api/state"]);
  assert.deepEqual(responded, ["response for GET /api/state"]);
  assert.equal(scope.caches, undefined);
});
