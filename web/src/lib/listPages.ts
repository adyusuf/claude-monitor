// "Load more" for the lists the API cuts and says so (the X-Truncated header): members and invitations (F-060), Jira spaces and people (F-077).
import type { CursorPage } from "../api/types";

/** The rows shown with those of a later page added after them; a row already shown (one that moved between two reads) is not added again. */
export function appendNew<T>(shown: T[], more: T[], key: (row: T) => string): T[] {
  const seen = new Set(shown.map(key));
  return [...shown, ...more.filter((row) => !seen.has(key(row)))];
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
