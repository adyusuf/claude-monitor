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
  /** How often the pages that show a countdown or an age redraw. */
  clockTickMs: 15_000,
  /** A web command is the same moment as the typed prompt it rode along with when they are this close (seconds). */
  commandPairSeconds: 10,
  /** A transcript line that repeats a web command's text is its echo when it is this close to the command's moment (seconds). */
  commandEchoSeconds: 120,
  activityFilterKey: "cm.activityFilter",
  languageKey: "cm.language",
  themeKey: "cm.theme",
} as const;
