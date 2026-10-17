/**
 * Which folds (accordions) the reader has opened, as the set of the keys of the lines inside them. A fold that groups lines
 * (a run of tool calls) is open when ANY of its lines is in the set, so it stays open when lines join it, when older ones are
 * read on top of it, and when its own key changes; closing it removes the lines it holds at that moment.
 */
export type OpenKeys = ReadonlySet<string>;

export const isOpen = (open: OpenKeys, keys: readonly string[]): boolean => keys.some((k) => open.has(k));

/** The set after the fold made of `keys` was opened or closed; the same set when nothing changes. */
export function withOpen(open: OpenKeys, keys: readonly string[], on: boolean): OpenKeys {
  if (on ? keys.every((k) => open.has(k)) : !isOpen(open, keys)) return open;
  const next = new Set(open);
  for (const k of keys) {
    if (on) next.add(k);
    else next.delete(k);
  }
  return next;
}
