import { useEffect, useRef, useState } from "react";
import { streamUrl } from "../api/endpoints";
import { config } from "../config";

export interface LiveMessage { event: string; sessionId?: string; id?: string; status?: string }

/** The events the page itself makes up when the stream was away or the tab came back: whatever the page shows may have changed. */
export const isCatchUp = (m: LiveMessage): boolean => m.event === "reconnected" || m.event === "resume";

const EVENTS = ["session", "command", "permission", "run", "alert", "grant", "job", "schedule", "approval_rule", "machine", "action"] as const;

/**
 * The workspace's live stream (server-sent events). Calls onMessage for every change; the page re-reads what it
 * shows, so a missed message costs nothing but a moment of staleness. Reconnects after a drop, waiting longer after each failure in a row (up to config.liveReconnectMaxMs).
 *
 * A stream can also go quiet without the browser noticing (a laptop that slept, a network that changed, a proxy that holds the
 * connection open and delivers nothing): the server pings every 20 s, so a stream that has said nothing for config.liveStaleMs is
 * closed and opened again. Whatever happened while it was away is read again: after a reconnect, and when the tab is shown or the
 * network returns, onMessage gets an event with no session (`reconnected`, `resume`), which every page treats as "re-read everything".
 */
export function useLive(workspaceId: string | undefined, onMessage: (m: LiveMessage) => void): boolean {
  const [connected, setConnected] = useState(false);
  const handler = useRef(onMessage);
  useEffect(() => {
    handler.current = onMessage;
  }, [onMessage]);

  useEffect(() => {
    if (!workspaceId || typeof EventSource === "undefined") return;
    let source: EventSource | null = null;
    let retry: ReturnType<typeof setTimeout> | undefined;
    let closed = false;
    let heard = Date.now();
    let everReady = false;
    let failures = 0;
    const open = () => {
      heard = Date.now();
      const stream = new EventSource(streamUrl(workspaceId));
      source = stream;
      stream.addEventListener("ready", () => {
        heard = Date.now();
        failures = 0;
        setConnected(true);
        if (everReady) handler.current({ event: "reconnected" });
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
          handler.current({ event: name, ...payload });
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
      handler.current({ event: "resume" });
    };
    document.addEventListener("visibilitychange", resume);
    window.addEventListener("online", resume);
    open();
    return () => {
      closed = true;
      clearTimeout(retry);
      clearInterval(watch);
      document.removeEventListener("visibilitychange", resume);
      window.removeEventListener("online", resume);
      source?.close();
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
