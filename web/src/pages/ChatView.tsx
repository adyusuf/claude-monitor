import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import type { ReactNode } from "react";
import { api } from "../api/endpoints";
import type { EventRow } from "../api/types";
import { Button, Card, Spinner } from "../components/ui";
import { config } from "../config";
import { useI18n } from "../i18n";
import { buildChat } from "../lib/transcript";
import { ChatLine } from "./ChatItems";

/** Events newest first, merged by id; older ones the page holds stay. */
const merge = (held: EventRow[], fresh: EventRow[]): EventRow[] => {
  const byId = new Map(held.map((e) => [e.id, e]));
  for (const e of fresh) byId.set(e.id, e);
  return [...byId.values()].sort((a, b) => b.id - a.id);
};

/**
 * The session as Claude's own screen shows it: oldest at the top, the newest at the bottom, a new message pushing the
 * rest up. Only the newest page is read at first; scrolling up reads the older ones, so a long session costs what is
 * looked at. A page scrolled up stays where it is when something new arrives.
 */
export function ChatCard({ sessionId, version, footer }: { sessionId: string; version: number; footer?: ReactNode }) {
  const { t } = useI18n();
  const [held, setHeld] = useState<EventRow[]>([]); // newest first
  const [next, setNext] = useState<string | null>(null);
  const [loaded, setLoaded] = useState(false);
  const [loadingOlder, setLoadingOlder] = useState(false);
  const [unseen, setUnseen] = useState(false);
  const box = useRef<HTMLDivElement>(null);
  const atBottom = useRef(true);
  const anchor = useRef<number | null>(null); // the scroll height before older events were put on top
  const newest = useRef<number | null>(null);
  const first = held[0]?.id ?? null;

  const refresh = useCallback(async (initial: boolean) => {
    const known = initial ? null : newest.current;
    const size = initial ? config.chatPageSize : config.chatRefreshSize;
    let page = await api.events(sessionId, { kind: config.chatKinds, limit: size });
    let fetched = page.items;
    // More arrived than one page holds: read back until it meets what the page already has.
    for (let i = 0; known !== null && page.next && fetched.length > 0 && fetched[fetched.length - 1]!.id > known && i < config.chatCatchUpPages; i++) {
      page = await api.events(sessionId, { kind: config.chatKinds, before: page.next, limit: size });
      fetched = [...fetched, ...page.items];
    }
    const gap = known !== null && page.next !== null && fetched.length > 0 && fetched[fetched.length - 1]!.id > known;
    if (initial || gap) {
      setHeld(fetched);
      setNext(page.next);
    } else {
      setHeld((h) => merge(h, fetched));
    }
    setLoaded(true);
  }, [sessionId]);

  useEffect(() => {
    newest.current = null;
    atBottom.current = true;
    setHeld([]);
    setNext(null);
    setUnseen(false);
    setLoaded(false);
    refresh(true).catch(() => setLoaded(true));
  }, [refresh]);

  const seen = useRef(version);
  useEffect(() => {
    if (seen.current === version) return;
    seen.current = version;
    refresh(false).catch(() => undefined);
  }, [version, refresh]);

  const older = useCallback(async () => {
    if (!next || loadingOlder) return;
    setLoadingOlder(true);
    try {
      const page = await api.events(sessionId, { kind: config.chatKinds, before: next, limit: config.chatPageSize });
      anchor.current = box.current?.scrollHeight ?? null;
      setHeld((h) => merge(h, page.items));
      setNext(page.next);
    } finally {
      setLoadingOlder(false);
    }
  }, [next, loadingOlder, sessionId]);

  // After the list changes: keep the reader's place when older events went on top, follow the end when the reader is
  // there, and otherwise say that something new came.
  useLayoutEffect(() => {
    const el = box.current;
    if (!el) return;
    if (anchor.current !== null) {
      el.scrollTop += el.scrollHeight - anchor.current;
      anchor.current = null;
    } else if (first !== null && first !== newest.current) {
      if (atBottom.current) el.scrollTop = el.scrollHeight;
      else if (newest.current !== null) setUnseen(true);
    }
    newest.current = first;
  }, [held, first]);

  // A short list that does not fill the box has nothing to scroll: read the next older page until it does.
  useEffect(() => {
    const el = box.current;
    if (loaded && next && !loadingOlder && el && el.clientHeight > 0 && el.scrollHeight <= el.clientHeight) void older();
  }, [loaded, next, loadingOlder, held, older]);

  const onScroll = () => {
    const el = box.current;
    if (!el) return;
    atBottom.current = el.scrollHeight - el.scrollTop - el.clientHeight < config.chatStickPx;
    if (atBottom.current) setUnseen(false);
    if (el.scrollTop < config.chatLoadMorePx) void older();
  };

  const toEnd = () => {
    const el = box.current;
    if (el) el.scrollTop = el.scrollHeight;
    setUnseen(false);
  };

  const items = useMemo(() => buildChat([...held].reverse()), [held]);

  return (
    <Card title={t("session.events")} className="chat-card">
      {!loaded ? <Spinner /> : (
        <div className="chat-scroll" ref={box} onScroll={onScroll} tabIndex={0} aria-label={t("chat.label")}>
          {next ? <div className="more"><Button variant="ghost" busy={loadingOlder} onClick={() => void older()}>{t("session.olderEvents")}</Button></div> : null}
          {items.length === 0 ? <p className="muted">{t("session.noEvents")}</p> : <ol className="chat">{items.map((item) => <ChatLine key={item.key} item={item} />)}</ol>}
        </div>
      )}
      {footer}
      {unseen ? <button type="button" className="chat-new" onClick={toEnd}>{t("chat.newBelow")} ↓</button> : null}
    </Card>
  );
}
