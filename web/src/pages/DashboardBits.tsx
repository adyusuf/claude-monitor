import type { ToolInfo } from "../api/types";

/**
 * A short mark for a piece of software the machine reported (git, node, python3, docker, …). These are lettered marks in the app's own style,
 * not the projects' logos (ADR-0018: SVG and text only, nothing fetched); the full name and version are the title and the accessible name.
 */
const MARKS: Record<string, string> = {
  git: "Git", node: "Node", npm: "npm", python3: "Py", dotnet: ".NET", docker: "Dkr", psql: "PG", java: "Java", claude: "CC",
  bash: "Bash", sh: "sh", pwsh: "PS", powershell: "PS",
};

export function SoftwareMark({ tool }: { tool: ToolInfo }) {
  const label = tool.version ? `${tool.name} ${tool.version}` : tool.name;
  return <span className="soft-mark" role="img" aria-label={label} title={label}>{MARKS[tool.name] ?? tool.name.slice(0, 4)}</span>;
}

/** One resource as a bar and its value in text: the bar is never the only carrier (a missing value says so in words). */
export function Meter({ label, pct, none }: { label: string; pct: number | null; none: string }) {
  const value = pct === null ? null : Math.max(0, Math.min(100, Math.round(pct)));
  return (
    <div className="meter-row">
      <span className="meter-label">{label}</span>
      <span className="meter" aria-hidden><span className={value !== null && value >= 90 ? "meter-fill meter-high" : "meter-fill"} style={{ width: `${value ?? 0}%` }} /></span>
      <span className="meter-value">{value === null ? none : `${value}%`}</span>
    </div>
  );
}

/**
 * A horizontal bar list: one row per state with its name, a bar of one hue proportional to the largest count, and the count as text. One series,
 * so no legend and no colour coding: the label carries the identity, the text the value, and the title shows both on hover.
 */
export function BarList({ rows, empty }: { rows: { key: string; label: string; count: number }[]; empty: string }) {
  const shown = rows.filter((r) => r.count > 0);
  if (shown.length === 0) return <p className="muted">{empty}</p>;
  const max = Math.max(...shown.map((r) => r.count));
  return (
    <ul className="bar-list">
      {shown.map((r) => (
        <li key={r.key} className="bar-row" title={`${r.label}: ${r.count}`}>
          <span className="bar-label">{r.label}</span>
          <span className="bar-track" aria-hidden><span className="bar-fill" style={{ width: `${(r.count / max) * 100}%` }} /></span>
          <span className="bar-value">{r.count}</span>
        </li>
      ))}
    </ul>
  );
}
