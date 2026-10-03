// The web tier's ONE configuration module (global #2). The app calls only relative /api paths on its own origin,
// so there is no host here; only limits and timings the pages share.
export const config = {
  apiBase: "/api",
  csrfHeader: "X-CSRF",
  pageSize: 25,
  eventPageSize: 50,
  passwordMin: 10,
  nameMax: 100,
  promptMax: 10_000,
  reasonMax: 500,
  liveReconnectMs: 3_000,
  liveDebounceMs: 400,
  languageKey: "cm.language",
  themeKey: "cm.theme",
} as const;
