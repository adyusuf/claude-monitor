import type { ClaudeInventory, InventorySetting, MachineProjectView } from "../api/types";
import { useI18n } from "../i18n";
import { dateTime } from "../lib/format";

/** The yes/no gates of a project as short lines, "merge.dev: yes". Names and values are text from the machine, shown as text. */
function Gates({ setting }: { setting: InventorySetting }) {
  const { t } = useI18n();
  const gates = Object.entries(setting.gates ?? {}).sort(([a], [b]) => a.localeCompare(b));
  if (gates.length === 0) return null;
  return (
    <ul className="chips small">
      {gates.map(([name, on]) => <li key={name} className={on ? "badge status-active" : "badge"}>{name}: {on ? t("machines.inventory.yes") : t("machines.inventory.no")}</li>)}
    </ul>
  );
}

/**
 * What Claude Code has on this machine, read-only (ADR-0011 §7a, step E): its sessions, installed plugins, skill names and, for projects that keep them, the
 * adyusuf working mode, model and gates. Only the owner and admins are sent it. Every text comes from the machine and is shown as text; nothing here can be changed from the web.
 */
export function MachineInventory({ inventory, projects }: { inventory: ClaudeInventory; projects: MachineProjectView[] }) {
  const { t } = useI18n();
  const nameOf = (key: string | null) => (key ? projects.find((p) => p.key === key)?.name ?? key : "–");
  const { sessions, plugins, skills, settings } = inventory;
  return (
    <div className="stack">
      <p className="muted small">{t("machines.inventory.hint", { at: dateTime(inventory.at) })}</p>

      <h3>{t("machines.inventory.sessions")}</h3>
      {sessions === null ? <p className="muted">{t("machines.inventory.unknown")}</p> : sessions.length === 0 ? <p className="muted">{t("machines.inventory.noSessions")}</p> : (
        <ul className="stack">
          {sessions.map((s, i) => (
            <li key={`${s.startedAt}-${i}`} className="row small">
              <strong>{s.name ?? t("machines.inventory.unnamed")}</strong>
              <span className="muted">{nameOf(s.projectKey)}</span>
              {s.kind ? <span className="badge">{s.kind}</span> : null}
              {s.status ? <span className="badge">{s.status}</span> : null}
              {s.startedAt ? <span className="muted">{dateTime(s.startedAt)}</span> : null}
            </li>
          ))}
        </ul>
      )}

      <h3>{t("machines.inventory.plugins")}</h3>
      {plugins === null ? <p className="muted">{t("machines.inventory.unknown")}</p> : plugins.length === 0 ? <p className="muted">{t("machines.inventory.noPlugins")}</p> : (
        <ul className="stack">
          {plugins.map((p) => (
            <li key={p.id} className="row small">
              <code>{p.id}</code>
              {p.version ? <span className="muted">{p.version}</span> : null}
              <span className={p.enabled ? "badge status-active" : "badge"}>{p.enabled ? t("machines.inventory.enabled") : t("machines.inventory.disabled")}</span>
              {p.scope ? <span className="badge">{p.scope}</span> : null}
            </li>
          ))}
        </ul>
      )}

      <h3>{t("machines.inventory.skills")}</h3>
      {skills === null ? <p className="muted">{t("machines.inventory.unknown")}</p> : skills.length === 0 ? <p className="muted">{t("machines.inventory.noSkills")}</p> : (
        <details>
          <summary>{t("machines.inventory.skillCount", { n: skills.length })}</summary>
          <ul className="chips small">{skills.map((name) => <li key={name} className="badge">{name}</li>)}</ul>
        </details>
      )}

      <h3>{t("machines.inventory.settings")}</h3>
      {settings === null || settings.length === 0 ? <p className="muted">{t("machines.inventory.noSettings")}</p> : (
        <ul className="stack">
          {settings.map((s) => (
            <li key={s.projectKey} className="stack">
              <div className="row small">
                <strong>{nameOf(s.projectKey)}</strong>
                {s.mode ? <span className="badge">{t("machines.inventory.mode", { mode: s.mode })}</span> : null}
                {s.modeModel ? <span className="badge">{s.modeModel}</span> : null}
              </div>
              <Gates setting={s} />
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
