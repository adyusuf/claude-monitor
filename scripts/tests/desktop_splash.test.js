"use strict";
// The desktop window's splash page (desktop/ui/splash.js, docs/live-board.md §2g).
// Run: node --test scripts/tests/  — measured by scripts/coverage.sh (#29).
const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");
const { show, FAILED_TITLE } = require(path.join(__dirname, "..", "..", "desktop", "ui", "splash.js"));

function fakeDocument() {
  const nodes = { title: { textContent: "Starting" }, detail: { textContent: "" } };
  return { nodes, getElementById: (id) => nodes[id] };
}

test("without an error in the fragment the page keeps saying it is starting", () => {
  const doc = fakeDocument();
  assert.equal(show("", doc), false);
  assert.equal(show("#", doc), false);
  assert.equal(doc.nodes.title.textContent, "Starting");
  assert.equal(doc.nodes.detail.textContent, "");
});

test("an error in the fragment is shown, decoded, as text", () => {
  const doc = fakeDocument();
  assert.equal(show("#Cannot%20run%20python3%3A%20%3Cb%3Ex", doc), true);
  assert.equal(doc.nodes.title.textContent, FAILED_TITLE);
  assert.equal(doc.nodes.detail.textContent, "Cannot run python3: <b>x");
});

test("a reason with a literal percent sign is shown as it is, not lost", () => {
  const doc = fakeDocument();
  assert.equal(show("#disk 100% full", doc), true);
  assert.equal(doc.nodes.detail.textContent, "disk 100% full");
});
