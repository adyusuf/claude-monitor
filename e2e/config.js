"use strict";
// The e2e suite's only configuration (global #2): the port, the address and the language the page is put in.
// Nothing else in e2e/ reads the environment or spells an address.
const PORT = Number(process.env.E2E_PORT || 18765);   // DEV fallback, defined here and nowhere else
module.exports = {
  PORT,
  BASE_URL: `http://127.0.0.1:${PORT}`,
  PYTHON: process.env.E2E_PYTHON || "python3",
  LANG_KEY: "board.lang",   // the page's own localStorage key (scripts/board/board_ui.js)
  LANG: "en",
  PAGE_SIZE: 10,            // scripts/board/board_ui_lists.js
};
