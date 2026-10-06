// Makes text safe to judge by eye (ADR-0005, "The owner's approval"): a command someone is asked to allow must show
// every character it has. Anything that is not plain printable ASCII is marked; what has no glyph (controls, format
// characters such as the right-to-left override, separators, combining marks, private and unassigned code points) is
// shown ONLY as its \u escape, so nothing can hide.

export interface VisibleSegment {
  /** What is drawn. */
  text: string;
  /** True for anything the reader must look at twice: an escape, or a glyph that is not ASCII. */
  flagged: boolean;
  /** True for a line break the text really holds (the page breaks the line after the marker). */
  newline?: boolean;
}

const NO_GLYPH = /[\p{C}\p{Z}\p{M}]/u;
const NEWLINE = 0x0a;
const SPACE = 0x20;
const TILDE = 0x7e;

const LAYOUT_CONTROLS = new Set([0x09, 0x0a, 0x0d]);
const NAMED: Record<number, string> = { 0x09: "\\t", 0x0a: "\\n", 0x0d: "\\r", 0x00: "\\0" };

/** "‮" for a BMP code point, "\u{1F600}" above it. */
export function escapeOf(code: number): string {
  const named = NAMED[code];
  if (named) return named;
  const hex = code.toString(16).toUpperCase();
  return code > 0xffff ? `\\u{${hex}}` : `\\u${hex.padStart(4, "0")}`;
}

/** The text split into runs: plain ASCII as is, everything else flagged. */
export function visibleSegments(text: string): VisibleSegment[] {
  const out: VisibleSegment[] = [];
  let plain = "";
  const flush = () => {
    if (plain) out.push({ text: plain, flagged: false });
    plain = "";
  };
  for (const ch of text) {
    const code = ch.codePointAt(0) ?? 0;
    if (code >= SPACE && code <= TILDE) {
      plain += ch;
      continue;
    }
    flush();
    if (code === NEWLINE) out.push({ text: escapeOf(code), flagged: true, newline: true });
    else if (NO_GLYPH.test(ch) || code < SPACE || code === 0x7f) out.push({ text: escapeOf(code), flagged: true });
    else out.push({ text: `${ch} ${escapeOf(code)}`, flagged: true });
  }
  flush();
  return out;
}

/** True when the text holds a control or format character, a separator or a combining mark (line breaks, tabs and CR are fine): anything with no glyph. */
export function hasInvisible(text: string): boolean {
  for (const ch of text) {
    const code = ch.codePointAt(0) ?? 0;
    if (LAYOUT_CONTROLS.has(code) || (code >= SPACE && code <= TILDE)) continue;
    if (NO_GLYPH.test(ch) || code < SPACE || code === 0x7f) return true;
  }
  return false;
}
