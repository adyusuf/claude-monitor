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
  /** A session whose last event is older than this is "idle": a command waits until a turn ends or someone types in it. */
  idleSessionMinutes: 5,
  /** An applied command with no answer yet is "waiting for a reply" for this long, then "no reply recorded". */
  replyWaitMinutes: 30,
  /** How often the pages that show a countdown or an age redraw. */
  clockTickMs: 15_000,
  languageKey: "cm.language",
  themeKey: "cm.theme",
} as const;
