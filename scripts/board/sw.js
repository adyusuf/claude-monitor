"use strict";
/* The live board's service worker (docs/live-board.md §2e). It exists so the browser
   can install the board as its own window; it caches NOTHING — the board is live data and a
   cached page would show a stale state. Every request goes to the local server. */
function wire(scope) {
  scope.addEventListener("install", () => scope.skipWaiting());
  scope.addEventListener("activate", (event) => event.waitUntil(scope.clients.claim()));
  scope.addEventListener("fetch", (event) => event.respondWith(scope.fetch(event.request)));
}

if (typeof module === "object" && module.exports) {
  module.exports = { wire };
} else {
  wire(self);
}
