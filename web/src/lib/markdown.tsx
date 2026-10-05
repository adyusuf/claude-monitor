import type { ReactNode } from "react";

// Just enough Markdown for what Claude writes: fenced code, headings, lists, paragraphs, `code` and **bold**.
// It builds React elements and never sets HTML, so captured text cannot inject markup.

function inline(text: string, keyBase: string): ReactNode[] {
  return text.split(/(`[^`]+`|\*\*[^*]+\*\*)/g).map((part, i) => {
    const key = `${keyBase}.${i}`;
    if (part.length > 2 && part.startsWith("`") && part.endsWith("`")) return <code key={key}>{part.slice(1, -1)}</code>;
    if (part.length > 4 && part.startsWith("**") && part.endsWith("**")) return <strong key={key}>{part.slice(2, -2)}</strong>;
    return part;
  });
}

const BULLET = /^\s*[-*]\s+(.*)$/;
const NUMBERED = /^\s*\d+[.)]\s+(.*)$/;
const HEADING = /^(#{1,6})\s+(.*)$/;

export function Markdown({ text }: { text: string }) {
  const lines = text.replace(/\r\n/g, "\n").split("\n");
  const out: ReactNode[] = [];
  let i = 0;
  while (i < lines.length) {
    const line = lines[i]!;
    const key = `b${i}`;
    if (line.trimStart().startsWith("```")) {
      const code: string[] = [];
      i++;
      while (i < lines.length && !lines[i]!.trimStart().startsWith("```")) code.push(lines[i++]!);
      i++; // the closing fence (or the end of an unfinished block)
      out.push(<pre key={key} className="md-code"><code>{code.join("\n")}</code></pre>);
    } else if (HEADING.test(line)) {
      out.push(<p key={key} className="md-heading"><strong>{inline(HEADING.exec(line)![2]!, key)}</strong></p>);
      i++;
    } else if (BULLET.test(line) || NUMBERED.test(line)) {
      const ordered = NUMBERED.test(line);
      const rule = ordered ? NUMBERED : BULLET;
      const items: ReactNode[] = [];
      while (i < lines.length && rule.test(lines[i]!)) items.push(<li key={`${key}.${i}`}>{inline(rule.exec(lines[i++]!)![1]!, `${key}.${i}`)}</li>);
      out.push(ordered ? <ol key={key}>{items}</ol> : <ul key={key}>{items}</ul>);
    } else if (!line.trim()) {
      i++;
    } else {
      const para: string[] = [];
      while (i < lines.length && lines[i]!.trim() && !lines[i]!.trimStart().startsWith("```") && !HEADING.test(lines[i]!) && !BULLET.test(lines[i]!) && !NUMBERED.test(lines[i]!)) para.push(lines[i++]!);
      out.push(<p key={key}>{inline(para.join("\n"), key)}</p>);
    }
  }

  return <div className="md">{out}</div>;
}
