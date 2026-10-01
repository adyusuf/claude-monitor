"use strict";
// The window shows this page only while the board server is being started, or when it cannot be.
// The Rust side puts the reason in the URL fragment (desktop/src/board.rs: error_page).
const FAILED_TITLE = "Claude Monitor could not start";

function decode(text) {
  try {
    return decodeURIComponent(text);
  } catch (_) {
    return text; // a literal "%" in the reason (the URL fragment does not escape it)
  }
}

function show(hash, doc) {
  const error = decode(hash.replace(/^#/, ""));
  if (!error) return false;
  doc.getElementById("title").textContent = FAILED_TITLE;
  doc.getElementById("detail").textContent = error;
  return true;
}

if (typeof module === "object" && module.exports) {
  module.exports = { show, FAILED_TITLE };
} else {
  show(location.hash, document);
}
