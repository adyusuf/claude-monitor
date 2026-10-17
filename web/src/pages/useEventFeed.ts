import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "../api/endpoints";
import type { EventRow } from "../api/types";
import { config } from "../config";

/** Events newest first, merged by id; older ones the list holds stay. */
export const mergeEvents = (held: EventRow[], fresh: EventRow[]): EventRow[] => {
  const byId = new Map(held.map((e) => [e.id, e]));
  for (const e of fresh) byId.set(e.id, e);
  return [...byId.values()].sort((a, b) => b.id - a.id);
};

/**
 * A session's events, newest first, read a page at a time. The newest page is read at first; `older` reads the page before what is held.
 * A refresh (the page was told something changed: `version` moved) reads the newest lines and merges them into what is held, so older
 * pages the reader asked for stay; only when more arrived than a few pages hold, and a gap would be left, does it start again from the newest.
 */
export function useEventFeed(sessionId: string, version: number, opts: { kind?: string; pageSize: number; refreshSize: number }) {
  const { kind, pageSize, refreshSize } = opts;
  const [held, setHeld] = useState<EventRow[]>([]); // newest first
  const [next, setNext] = useState<string | null>(null);
  const [loaded, setLoaded] = useState(false);
  const [loadingOlder, setLoadingOlder] = useState(false);
  const newest = useRef<number | null>(null);

  useEffect(() => {
    newest.current = held[0]?.id ?? null;
  }, [held]);

  const refresh = useCallback(async (initial: boolean) => {
    const known = initial ? null : newest.current;
    const size = initial ? pageSize : refreshSize;
    let page = await api.events(sessionId, { kind, limit: size });
    let fetched = page.items;
    // More arrived than one page holds: read back until it meets what the list already has.
    for (let i = 0; known !== null && page.next && fetched.length > 0 && fetched[fetched.length - 1]!.id > known && i < config.chatCatchUpPages; i++) {
      page = await api.events(sessionId, { kind, before: page.next, limit: size });
      fetched = [...fetched, ...page.items];
    }
    const gap = known !== null && page.next !== null && fetched.length > 0 && fetched[fetched.length - 1]!.id > known;
    if (initial || gap) {
      setHeld(fetched);
      setNext(page.next);
    } else {
      setHeld((h) => mergeEvents(h, fetched));
    }
    setLoaded(true);
  }, [sessionId, kind, pageSize, refreshSize]);

  useEffect(() => {
    newest.current = null;
    setHeld([]);
    setNext(null);
    setLoaded(false);
    refresh(true).catch(() => setLoaded(true));
  }, [refresh]);

  const seen = useRef(version);
  useEffect(() => {
    if (seen.current === version) return;
    seen.current = version;
    refresh(false).catch(() => undefined);
  }, [version, refresh]);

  /** Reads the page before what is held; `beforeAdd` runs just before it is put on top (the chat notes its scroll height there). */
  const older = useCallback(async (beforeAdd?: () => void) => {
    if (!next || loadingOlder) return;
    setLoadingOlder(true);
    try {
      const page = await api.events(sessionId, { kind, before: next, limit: pageSize });
      beforeAdd?.();
      setHeld((h) => mergeEvents(h, page.items));
      setNext(page.next);
    } finally {
      setLoadingOlder(false);
    }
  }, [next, loadingOlder, sessionId, kind, pageSize]);

  return { held, next, loaded, loadingOlder, older };
}
