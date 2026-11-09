import type { MachineView, ToolInfo } from "../api/types";
import { useI18n } from "../i18n";
import { ENVIRONMENT_ORDER, environmentsOf, FUNCTION_ORDER, functionsOf, normalize } from "../lib/roles";
import { Icon } from "../components/icons";

/** True for a production or database machine (ADR-0008 §2): the badge turns red and scripts will need a code. */
export const isStrict = (m: MachineView): boolean => m.effectiveEnvironment === "production" || m.effectiveFunction === "database";

/** The values of a set with the effective one added when a claim made it stricter than anything set, loosest first. */
function withEffective<T extends string>(values: T[], effective: T | null | undefined, order: readonly T[]): T[] {
  return effective && !values.includes(effective) ? normalize([...values, effective], order) : values;
}

/**
 * "production/database", "test/not set", "dev+test/web", or "not set"; the server computed the effective role (the claim only tightened it),
 * and a role with several values names each of them.
 */
export function roleCode(m: MachineView, notSet: string): string {
  if (!m.effectiveEnvironment && !m.effectiveFunction) return notSet;
  const envs = m.effectiveEnvironment ? withEffective(environmentsOf(m), m.effectiveEnvironment, ENVIRONMENT_ORDER) : [];
  const fns = m.effectiveFunction ? withEffective(functionsOf(m), m.effectiveFunction, FUNCTION_ORDER) : [];
  return `${envs.length > 0 ? envs.join("+") : notSet}/${fns.length > 0 ? fns.join("+") : notSet}`;
}

/** The effective role, red when strict, with the machine's own claim in the title. */
export function RoleBadge({ view: m }: { view: MachineView }) {
  const { t } = useI18n();
  const code = roleCode(m, t("machines.role.notSet"));
  const claim = m.claimedEnvironment || m.claimedFunction
    ? t("machines.role.claimed", { role: `${m.claimedEnvironment ?? t("machines.role.notSet")}/${m.claimedFunction ?? t("machines.role.notSet")}` }) : "";
  if (isStrict(m)) return <span className="badge status-failed" title={claim}>{code}</span>;
  if (!m.effectiveEnvironment && !m.effectiveFunction) return <span className="muted small" title={claim}>{code}</span>;
  return <span className="badge" title={claim}>{code}</span>;
}

/** Whether Claude Code is there: "unknown" for an agent that reported nothing (older), "not installed", or the kind, version and idle sessions. */
export function ClaudeBadge({ view: m }: { view: MachineView }) {
  const { t } = useI18n();
  if (m.claudeKind === undefined || m.claudeKind === null) return <span className="muted small">{t("machines.claude.unknown")}</span>;
  if (m.claudeKind === "none") return <span className="muted small">{t("machines.claude.none")}</span>;
  const idle = m.claudeSessionsIdle !== undefined && m.claudeSessionsIdle !== null ? ` · ${t("machines.claude.idle", { n: m.claudeSessionsIdle })}` : "";
  return <span className="badge" title={kindText(m.claudeKind, t)}>{t("machines.claude.title")} {m.claudeVersion ?? ""}{idle}</span>;
}

function kindText(kind: string, t: (key: "machines.claude.kind.npm" | "machines.claude.kind.native" | "machines.claude.kind.unsupported") => string): string {
  switch (kind) {
    case "npm":
      return t("machines.claude.kind.npm");
    case "native":
      return t("machines.claude.kind.native");
    case "unsupported":
      return t("machines.claude.kind.unsupported");
    default:
      return kind;
  }
}

/** How many tags a row of the machines table shows before it says how many more there are. */
export const TAG_CHIPS_SHOWN = 5;

/** The admin's tags as small chips, the first few and a count of the rest; nothing when there are none (an older API omits them). */
export function TagChips({ view: m }: { view: MachineView }) {
  const { t } = useI18n();
  const entries = Object.entries(m.tags ?? {}).sort(([a], [b]) => (a < b ? -1 : 1));
  if (entries.length === 0) return null;
  const rest = entries.length - TAG_CHIPS_SHOWN;
  return <>{entries.slice(0, TAG_CHIPS_SHOWN).map(([k, v]) => <span key={k} className="badge">{k}={v}</span>)}{rest > 0
    ? <span className="badge" title={entries.slice(TAG_CHIPS_SHOWN).map(([k, v]) => `${k}=${v}`).join(" ")}>{t("machines.tags.more", { n: rest })}</span> : null} </>;
}

/** The tool inventory as "name version" chips; an untrusted tool is marked, and an agent that reported nothing says so. */
export function ToolList({ view: m }: { view: MachineView }) {
  const { t } = useI18n();
  if (m.tools === undefined || m.tools === null) return <p className="muted">{t("machines.tools.unknown")}</p>;
  if (m.tools.length === 0) return <p className="muted">{t("machines.tools.none")}</p>;
  const interpreters = new Set((m.interpreters ?? []).map((i) => i.name));
  return (
    <div className="stack">
      <ul className="chips" aria-label={t("machines.tools.title")}>
        {m.tools.map((tool: ToolInfo) => (
          <li key={tool.name} className={tool.trusted ? "badge" : "badge warn"} title={tool.trusted ? tool.path : `${tool.path} — ${t("machines.tools.untrusted")}`}>
            {tool.name}{tool.version ? ` ${tool.version}` : ""}{interpreters.has(tool.name) ? <> <Icon name="check" /></> : null}
          </li>
        ))}
      </ul>
      <p className="muted small">{t("machines.tools.hint")}</p>
    </div>
  );
}
