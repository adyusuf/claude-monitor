import { useEffect, useRef, useState } from "react";
import { streamUrl } from "../api/endpoints";
import { config } from "../config";

export interface LiveMessage { event: string; sessionId?: string; id?: string; status?: string }

/** The events the page itself makes up when the stream was away or the tab came back: whatever the page shows may have changed. */
export const isCatchUp = (m: LiveMessage): boolean => m.event === "reconnected" || m.event === "resume";

const EVENTS = ["session", "command", "permission", "run", "alert", "grant", "job", "schedule", "ask", "approval_rule", "machine", "action"] as const;

/** One reader of a workspace's stream: it hears every message, and whether the stream is connected now. */
interface Subscriber { onMessage: (m: LiveMessage) => void; onConnected: (connected: boolean) => void }

/** The one stream of a workspace that every reader on the page shares (F-098): who listens, whether it is connected, and how to end it. */
interface Shared { subscribers: Set<Subscriber>; connected: boolean; close: () => void }

const shared = new Map<string, Shared>();

/**
 * Hands a message to every reader. A reader whose handler throws does not keep the message from the others; the error is thrown on once all
 * have had it (several errors together as one AggregateError), so a page bug is never hidden.
 */
function broadcast(conn: Shared, m: LiveMessage) {
  const errors: unknown[] = [];
  for (const s of [...conn.subscribers]) {
    try {
      s.onMessage(m);
    } catch (e) {
      errors.push(e);
    }
  }
  if (errors.length === 1) throw errors[0];
  if (errors.length > 1) throw new AggregateError(errors, "live stream handlers failed");
}

/**
 * Opens the workspace's stream (server-sent events) and keeps it open until close(). Reconnects after a drop, waiting longer after each failure in
 * a row (up to config.liveReconnectMaxMs).
 *
 * A stream can also go quiet without the browser noticing (a laptop that slept, a network that changed, a proxy that holds the
 * connection open and delivers nothing): the server pings every 20 s, so a stream that has said nothing for config.liveStaleMs is
 * closed and opened again. Whatever happened while it was away is read again: after a reconnect, and when the tab is shown or the
 * network returns, the readers get an event with no session (`reconnected`, `resume`), which every page treats as "re-read everything".
 */
function connect(workspaceId: string): Shared {
  let source: EventSource | null = null;
  let retry: ReturnType<typeof setTimeout> | undefined;
  let closed = false;
  let heard = Date.now();
  let everReady = false;
  let failures = 0;
  const conn: Shared = { subscribers: new Set(), connected: false, close: () => undefined };
  const setConnected = (connected: boolean) => {
    conn.connected = connected;
    for (const s of [...conn.subscribers]) s.onConnected(connected);
  };
  const open = () => {
    heard = Date.now();
    const stream = new EventSource(streamUrl(workspaceId));
    source = stream;
    stream.addEventListener("ready", () => {
      heard = Date.now();
      failures = 0;
      setConnected(true);
      if (everReady) broadcast(conn, { event: "reconnected" });
      everReady = true;
    });
    stream.addEventListener("ping", () => {
      heard = Date.now();
    });
    for (const name of EVENTS) {
      stream.addEventListener(name, (e) => {
        heard = Date.now();
        let payload: object = {};
        try {
          payload = JSON.parse((e as MessageEvent).data) as object;
        } catch {
          // a payload that is not JSON still tells the page that something of this kind changed
        }
        broadcast(conn, { event: name, ...payload });
      });
    }
    stream.onerror = () => {
      setConnected(false);
      stream.close();
      if (closed) return;
      retry = setTimeout(open, Math.min(config.liveReconnectMs * 2 ** failures, config.liveReconnectMaxMs));
      failures++;
    };
  };
  const reopen = () => {
    source?.close();
    clearTimeout(retry);
    setConnected(false);
    open();
  };
  const stale = () => Date.now() - heard > config.liveStaleMs;
  const watch = setInterval(() => {
    if (!closed && stale()) reopen();
  }, config.liveWatchMs);
  const resume = () => {
    if (closed || document.visibilityState === "hidden") return;
    if (stale()) reopen();
    broadcast(conn, { event: "resume" });
  };
  document.addEventListener("visibilitychange", resume);
  window.addEventListener("online", resume);
  conn.close = () => {
    closed = true;
    clearTimeout(retry);
    clearInterval(watch);
    document.removeEventListener("visibilitychange", resume);
    window.removeEventListener("online", resume);
    source?.close();
    conn.connected = false;
  };
  open();
  return conn;
}

/**
 * Joins the workspace's shared stream: the first reader opens it, the others join the one already open, and the last one to leave closes it.
 * A reader that joins an already connected stream hears so at once. Returns the leave.
 */
export function subscribeLive(workspaceId: string, subscriber: Subscriber): () => void {
  let conn = shared.get(workspaceId);
  if (!conn) {
    conn = connect(workspaceId);
    shared.set(workspaceId, conn);
  }
  const mine = conn;
  mine.subscribers.add(subscriber);
  if (mine.connected) subscriber.onConnected(true);
  return () => {
    if (!mine.subscribers.delete(subscriber) || mine.subscribers.size > 0) return;
    mine.close();
    if (shared.get(workspaceId) === mine) shared.delete(workspaceId);
  };
}

/**
 * The workspace's live stream. Calls onMessage for every change; the page re-reads what it shows, so a missed message costs nothing but a
 * moment of staleness. Every reader on the page (the frame's badges and the page itself) shares one EventSource per workspace
 * ({@link subscribeLive}); returns whether it is connected now.
 */
export function useLive(workspaceId: string | undefined, onMessage: (m: LiveMessage) => void): boolean {
  const [connected, setConnected] = useState(false);
  const handler = useRef(onMessage);
  useEffect(() => {
    handler.current = onMessage;
  }, [onMessage]);

  useEffect(() => {
    if (!workspaceId || typeof EventSource === "undefined") return;
    const leave = subscribeLive(workspaceId, { onMessage: (m) => handler.current(m), onConnected: setConnected });
    return () => {
      leave();
      setConnected(false);
    };
  }, [workspaceId]);

  return connected;
}

/** Runs fn at most once per wait, after the last call (for bursts of live messages). */
export function debounce(fn: () => void, wait: number = config.liveDebounceMs): () => void {
  let timer: ReturnType<typeof setTimeout> | undefined;
  return () => {
    clearTimeout(timer);
    timer = setTimeout(fn, wait);
  };
}
