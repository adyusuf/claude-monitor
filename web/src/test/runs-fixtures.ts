import type { WebRunView } from "../api/types";
import { run } from "./remote-fixtures";

/** A moment `minutesAgo` before noon on 05/10/2026, local time, so a date shown in the page is the same on every machine. */
export const at = (minutesAgo: number) => new Date(new Date(2026, 9, 5, 12, 0, 0).getTime() - minutesAgo * 60_000).toISOString();

/** A finished run nobody has to decide, with its own command (`/bin/echo <id>`) so its row can be found. */
export const done = (id: string, minutesAgo: number, over: Partial<WebRunView> = {}): WebRunView =>
  run({ id, status: "succeeded", canDecide: false, argv: ["/bin/echo", id], createdAt: at(minutesAgo), expiresAt: at(minutesAgo - 5), ...over });
