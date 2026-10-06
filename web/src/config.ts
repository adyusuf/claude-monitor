// The web tier's ONE configuration module (global #2). The app calls only relative /api paths on its own origin,
// so there is no host here; only limits and timings the pages share.
export const config = {
  apiBase: "/api",
  csrfHeader: "X-CSRF",
  pageSize: 25,
  eventPageSize: 50,
  /** The chat reads the newest transcript lines first, then this many more each time the reader scrolls up. */
  /** What the conversation reads: the transcript, and the hooks that say Claude wants the user or a subagent came and went. */
  chatKinds: "transcript,hook:Notification,hook:SubagentStart,hook:SubagentStop",
  chatPageSize: 40,
  /** A live refresh reads this many of the newest lines; if more arrived it reads back until it meets what it has. */
  chatRefreshSize: 20,
  chatCatchUpPages: 5,
  /** Within this many pixels of the end the chat follows new messages; within this many of the top it reads older ones. */
  chatStickPx: 80,
  chatLoadMorePx: 200,
  /** A tool's output longer than this shows cut, with "Show all". */
  chatOutputChars: 4000,
  passwordMin: 10,
  nameMax: 100,
  promptMax: 10_000,
  reasonMax: 500,
  /** The longest typed answer to a question (the API takes the same). */
  answerMax: 500,
  liveReconnectMs: 3_000,
  liveDebounceMs: 400,
  /** A session whose last event is older than this is "idle": a command waits until a turn ends or someone types in it. */
  idleSessionMinutes: 5,
  /** An applied command with no answer yet is "waiting for a reply" for this long, then "no reply recorded". */
  replyWaitMinutes: 30,
  /** How often the pages that show a countdown or an age redraw. */
  clockTickMs: 15_000,
  /** A web command is the same moment as the typed prompt it rode along with when they are this close (seconds). */
  commandPairSeconds: 10,
  /** A transcript line that repeats a web command's text is its echo when it is this close to the command's moment (seconds). */
  commandEchoSeconds: 120,
  activityFilterKey: "cm.activityFilter",
  /** Remote work (ADR-0004): a machine's metrics window and refresh, run history pages, the output page, the countdown tick. */
  metricsMinutes: 60,
  metricsRefreshMs: 60_000,
  runPageSize: 50,
  /** The nav badge of runs waiting for the user re-reads this often. */
  pendingPollMs: 30_000,
  outputPageSize: 50,
  countdownTickMs: 1_000,
  /** The Allow button of a shell or interpreter run waits this long after the card appears (ADR-0004, "The owner's approval"). */
  runAllowDelayMs: 3_000,
  grantDaysDefault: 7,
  grantDaysMax: 90,
  grantTimeoutMax: 3_600,
  languageKey: "cm.language",
  themeKey: "cm.theme",
} as const;
