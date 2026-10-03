import { useEffect, useRef, useState } from "react";
import { streamUrl } from "../api/endpoints";
import { config } from "../config";

export interface LiveMessage { event: string; sessionId?: string; id?: string; status?: string }

const EVENTS = ["session", "command", "permission"] as const;

/**
 * The workspace's live stream (server-sent events). Calls onMessage for every change; the page re-reads what it
 * shows, so a missed message costs nothing but a moment of staleness. Reconnects after a drop.
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
    const open = () => {
      source = new EventSource(streamUrl(workspaceId));
      source.addEventListener("ready", () => setConnected(true));
      for (const name of EVENTS) {
        source.addEventListener(name, (e) => {
          try {
            handler.current({ event: name, ...(JSON.parse((e as MessageEvent).data) as object) });
          } catch {
            handler.current({ event: name });
          }
        });
      }
      source.onerror = () => {
        setConnected(false);
        source?.close();
        if (!closed) retry = setTimeout(open, config.liveReconnectMs);
      };
    };
    open();
    return () => {
      closed = true;
      clearTimeout(retry);
      source?.close();
      setConnected(false);
    };
  }, [workspaceId]);

  return connected;
}

/** Runs fn at most once per wait, after the last call (for bursts of live messages). */
export function debounce(fn: () => void, wait = config.liveDebounceMs): () => void {
  let timer: ReturnType<typeof setTimeout> | undefined;
  return () => {
    clearTimeout(timer);
    timer = setTimeout(fn, wait);
  };
}
