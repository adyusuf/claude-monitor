import type { ReactNode } from "react";

// The Markdown Claude (and people) write: headings, paragraphs with their line breaks, **bold**, *italic*, ~~strike~~, `code`, fenced
// code with its language, links, bullet / numbered / task lists that nest, block quotes, tables with alignment and rules.
// It builds React elements and never sets HTML, so captured text cannot inject markup; a link is made only for http(s) and mailto.

const INLINE_MAX = 4000; // a longer run is shown as it is: emphasis is found by scanning, and a wall of text is not worth the cost
const DEPTH_MAX = 6; // quotes and lists nested deeper than this are shown as text

const TOKEN = new RegExp([
  String.raw`(?<escape>\\[\\\`*_{}\[\]()#+\-.!~>|])`,
  String.raw`(?<code2>\`\`(?<c2>[^\`]+(?:\`[^\`]+)*)\`\`)`,
  String.raw`(?<code1>\`(?<c1>[^\`\n]+)\`)`,
  String.raw`(?<link>\[(?<ltext>[^\]\n]+)\]\((?<lurl>[^)\s]+)(?:\s+"[^"\n]*")?\))`,
  String.raw`(?<auto><(?<aurl>https?:\/\/[^>\s]+)>)`,
  String.raw`(?<bare>https?:\/\/[^\s<>\]"')]+)`,
  String.raw`(?<bold>\*\*(?<b1>[^\n]+?)\*\*|__(?<b2>[^\n]+?)__)`,
  String.raw`(?<strike>~~(?<s1>[^\n]+?)~~)`,
  String.raw`(?<ital>\*(?<i1>[^*\s](?:[^*\n]*[^*\s])?)\*|(?<![A-Za-z0-9_])_(?<i2>[^_\s](?:[^_\n]*[^_\s])?)_(?![A-Za-z0-9_]))`,
].join("|"), "g");

/** An address worth a link: http(s) and mailto only (never javascript:, data: or a relative path). */
export function safeHref(raw: string): string | null {
  try {
    const url = new URL(raw);
    return url.protocol === "http:" || url.protocol === "https:" || url.protocol === "mailto:" ? url.href : null;
  } catch {
    return null;
  }
}

function inline(text: string, key: string, depth = 0): ReactNode[] {
  if (text.length > INLINE_MAX || depth > 3) return [text];
  const out: ReactNode[] = [];
  let last = 0;
  let n = 0;
  for (const m of text.matchAll(TOKEN)) {
    const g = m.groups ?? {};
    if (m.index > last) out.push(text.slice(last, m.index));
    const k = `${key}.${n++}`;
    if (g.escape) out.push(g.escape.slice(1));
    else if (g.code2 || g.code1) out.push(<code key={k}>{g.c2 ?? g.c1}</code>);
    else if (g.link) {
      const href = safeHref(g.lurl!);
      out.push(href ? <a key={k} href={href} target="_blank" rel="noopener noreferrer nofollow">{inline(g.ltext!, k, depth + 1)}</a> : m[0]);
    } else if (g.auto || g.bare) {
      const shown = (g.aurl ?? g.bare)!;
      const url = g.bare ? shown.replace(/[.,;:!?]+$/, "") : shown; // sentence punctuation after a bare address is not part of it
      const href = safeHref(url);
      out.push(href ? <a key={k} href={href} target="_blank" rel="noopener noreferrer nofollow">{url}</a> : shown);
      if (g.bare && url.length < shown.length) out.push(shown.slice(url.length));
    } else if (g.bold) out.push(<strong key={k}>{inline((g.b1 ?? g.b2)!, k, depth + 1)}</strong>);
    else if (g.strike) out.push(<del key={k}>{inline(g.s1!, k, depth + 1)}</del>);
    else if (g.ital) out.push(<em key={k}>{inline((g.i1 ?? g.i2)!, k, depth + 1)}</em>);
    last = m.index + m[0].length;
  }
  if (last < text.length) out.push(text.slice(last));
  return out;
}

/** A paragraph's lines with their breaks kept: a line the writer broke stays broken. */
function lines(text: string[], key: string): ReactNode[] {
  return text.flatMap((line, i): ReactNode[] => [...(i === 0 ? [] : [<br key={`${key}.br${i}`} />]), ...inline(line.replace(/\s+$/, ""), `${key}.${i}`)]);
}

const FENCE = /^\s*(```+|~~~+)\s*([\w+#.-]*)\s*$/;
const HEADING = /^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$/;
const RULE = /^\s{0,3}([-*_])(?:\s*\1){2,}\s*$/;
const LIST = /^(\s*)([-*+]|\d{1,9}[.)])\s+(.*)$/;
const QUOTE = /^\s{0,3}>\s?(.*)$/;
const TABLE_SEPARATOR = /^\s*\|?\s*:?-+:?\s*(?:\|\s*:?-+:?\s*)*\|?\s*$/;
const TASK = /^\[([ xX])\]\s+(.*)$/;

const cells = (row: string): string[] => row.trim().replace(/^\|/, "").replace(/\|$/, "").split(/(?<!\\)\|/).map((c) => c.trim().replace(/\\\|/g, "|"));
const isTable = (line: string, next: string | undefined): boolean => line.includes("|") && next !== undefined && TABLE_SEPARATOR.test(next) && next.includes("-");
const startsBlock = (line: string, next: string | undefined): boolean =>
  FENCE.test(line) || HEADING.test(line) || RULE.test(line) || LIST.test(line) || QUOTE.test(line) || isTable(line, next);
const indentOf = (line: string): number => line.length - line.trimStart().length;

function list(src: string[], start: number, key: string, depth: number): { node: ReactNode; next: number } {
  const first = LIST.exec(src[start]!)!;
  const ordered = /\d/.test(first[2]!);
  const base = first[1]!.length;
  const items: ReactNode[] = [];
  let i = start;
  while (i < src.length) {
    const marker = LIST.exec(src[i]!);
    if (!marker || marker[1]!.length > base + 1 || /\d/.test(marker[2]!) !== ordered) break;
    const body: string[] = [marker[3]!];
    i++;
    // What belongs to the item: lines indented deeper than its marker (nested lists, wrapped text), single blank lines between them included.
    while (i < src.length) {
      const next = src[i]!;
      const blank = !next.trim();
      if (!blank && indentOf(next) <= base + 1) break;
      if (blank && !(src[i + 1]?.trim() && indentOf(src[i + 1]!) > base + 1)) break;
      body.push(blank ? "" : next.slice(Math.min(indentOf(next), base + 2)));
      i++;
    }

    const task = TASK.exec(body[0]!);
    if (task) body[0] = task[2]!;
    const at = `${key}.${items.length}`;
    items.push(
      <li key={items.length} className={task ? "md-task" : undefined}>
        {task ? <input type="checkbox" disabled checked={task[1] !== " "} aria-label={task[2]} /> : null}
        {depth >= DEPTH_MAX ? lines(body, at) : blocks(body, at, depth + 1, true)}
      </li>,
    );
  }

  const number = Number.parseInt(first[2]!, 10);
  return { node: ordered ? <ol key={key} start={number !== 1 ? number : undefined}>{items}</ol> : <ul key={key}>{items}</ul>, next: i };
}

function table(src: string[], start: number, key: string): { node: ReactNode; next: number } {
  const head = cells(src[start]!);
  const align = cells(src[start + 1]!).map((c) => (c.startsWith(":") && c.endsWith(":") ? "center" : c.endsWith(":") ? "right" : c.startsWith(":") ? "left" : undefined));
  let i = start + 2;
  const rows: string[][] = [];
  while (i < src.length && src[i]!.trim() && src[i]!.includes("|")) rows.push(cells(src[i++]!));
  return {
    next: i,
    node: (
      <div key={key} className="md-table"><table>
        <thead><tr>{head.map((c, j) => <th key={j} style={{ textAlign: align[j] }}>{inline(c, `${key}.h${j}`)}</th>)}</tr></thead>
        <tbody>{rows.map((r, y) => <tr key={y}>{head.map((_, j) => <td key={j} style={{ textAlign: align[j] }}>{inline(r[j] ?? "", `${key}.${y}.${j}`)}</td>)}</tr>)}</tbody>
      </table></div>
    ),
  };
}

function blocks(src: string[], key: string, depth: number, tight: boolean): ReactNode[] {
  const out: ReactNode[] = [];
  let i = 0;
  while (i < src.length) {
    const line = src[i]!;
    const k = `${key}.${i}`;
    const fence = FENCE.exec(line);
    if (fence) {
      const close = new RegExp(`^\\s*${fence[1]![0] === "`" ? "`" : "~"}{${fence[1]!.length},}\\s*$`);
      const code: string[] = [];
      i++;
      while (i < src.length && !close.test(src[i]!)) code.push(src[i++]!);
      i++; // the closing fence (or the end of a block that was never closed)
      out.push(<pre key={k} className="md-code" data-lang={fence[2] || undefined}><code>{code.join("\n")}</code></pre>);
    } else if (HEADING.test(line)) {
      const [, hashes, body] = HEADING.exec(line)!;
      out.push(<div key={k} role="heading" aria-level={Math.min(6, hashes!.length + 2)} className={`md-heading md-h${hashes!.length}`}><strong>{inline(body!, k)}</strong></div>);
      i++;
    } else if (RULE.test(line)) {
      out.push(<hr key={k} />);
      i++;
    } else if (QUOTE.test(line)) {
      const inner: string[] = [];
      while (i < src.length && QUOTE.test(src[i]!)) inner.push(QUOTE.exec(src[i++]!)![1]!);
      out.push(<blockquote key={k}>{depth >= DEPTH_MAX ? lines(inner, k) : blocks(inner, k, depth + 1, false)}</blockquote>);
    } else if (isTable(line, src[i + 1])) {
      const t = table(src, i, k);
      out.push(t.node);
      i = t.next;
    } else if (LIST.test(line)) {
      const l = list(src, i, k, depth);
      out.push(l.node);
      i = l.next;
    } else if (!line.trim()) {
      i++;
    } else {
      const para: string[] = [];
      while (i < src.length && src[i]!.trim() && (para.length === 0 || !startsBlock(src[i]!, src[i + 1]))) para.push(src[i++]!);
      out.push(tight ? <span key={k}>{lines(para, k)}</span> : <p key={k}>{lines(para, k)}</p>);
    }
  }

  return out;
}

export function Markdown({ text }: { text: string }) {
  return <div className="md">{blocks(text.replace(/\r\n?/g, "\n").split("\n"), "b", 0, false)}</div>;
}
