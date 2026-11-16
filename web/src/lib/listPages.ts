// "Load more" for the lists the API cuts and says so (the X-Truncated header): members and invitations (F-060), Jira spaces and people (F-077).
import type { CursorPage } from "../api/types";

/** The rows shown with those of a later page added after them; a row already shown (one that moved between two reads) is not added again. */
export function appendNew<T>(shown: T[], more: T[], key: (row: T) => string): T[] {
  const seen = new Set(shown.map(key));
  return [...shown, ...more.filter((row) => !seen.has(key(row)))];
}

/** A list read and whether the API cut it (the X-Truncated header). */
export interface ListRead<T> { items: T[]; truncated: boolean }

/**
 * A skip-paged list read again as far as it was shown: the first page, then the next ones while the API says it cut the list and fewer
 * than `shown` rows are read, so a refresh does not take away the rows "Load more" added. A page with no new row ends the reading.
 */
export async function readUpTo<T>(read: (skip?: number) => Promise<ListRead<T>>, shown: number, key: (row: T) => string): Promise<ListRead<T>> {
  let list = await read();
  while (list.truncated && list.items.length < shown) {
    const page = await read(list.items.length);
    const items = appendNew(list.items, page.items, key);
    if (items.length === list.items.length) return { items, truncated: page.truncated };
    list = { items, truncated: page.truncated };
  }
  return list;
}

/** Where a cursor-paged list stands: whether rows are left, and the cursor to read them from (null: from the start). */
export interface CursorState { truncated: boolean; cursor: string | null }

/**
 * The rows after those shown of a list whose first read was the plain capped array: that read carries no cursor, so the pages are read
 * from the start until one brings a row not shown yet, and the cursor reached is kept, so the next call reads only one page.
 */
export async function nextByCursor<T>(read: (cursor: string | null) => Promise<CursorPage<T>>, state: CursorState, shown: T[],
  key: (row: T) => string): Promise<{ rows: T[]; state: CursorState }> {
  let cursor = state.cursor;
  for (;;) {
    const page = await read(cursor);
    const rows = appendNew(shown, page.items, key);
    // A page with new rows, the last page, or a cursor that does not move on (it would read the same page forever) ends the walk.
    if (rows.length > shown.length || page.nextCursor === null || page.nextCursor === cursor) {
      return { rows, state: { truncated: page.nextCursor !== null && page.nextCursor !== cursor, cursor: page.nextCursor } };
    }
    cursor = page.nextCursor;
  }
}

/**
 * A plain capped list read again as far as it was shown: after the plain `first` read, the cursor pages are walked while the API says
 * rows are left and fewer than `shown` are read, so a change and its reload do not take away the rows "Load more" added.
 */
export async function readByCursorUpTo<T>(first: ListRead<T>, read: (cursor: string | null) => Promise<CursorPage<T>>, shown: number,
  key: (row: T) => string): Promise<{ rows: T[]; state: CursorState }> {
  let rows = first.items;
  let state: CursorState = { truncated: first.truncated, cursor: null };
  // nextByCursor ends with new rows or with nothing left (truncated false), so this loop always ends.
  while (state.truncated && rows.length < shown) {
    ({ rows, state } = await nextByCursor(read, state, rows, key));
  }
  return { rows, state };
}
