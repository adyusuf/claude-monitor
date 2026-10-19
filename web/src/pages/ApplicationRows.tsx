import { useState } from "react";
import type { EnvironmentName, TargetKind } from "../api/typesProjects";
import { Button } from "../components/ui";
import { useI18n } from "../i18n";
import type { ApplicationDraft } from "../lib/applicationForm";
import { ENVIRONMENT_NAMES, projectLimits, TARGET_KINDS } from "../lib/projectOptions";
import { Group } from "./ProjectBits";

/** The environments an application runs in: a box per environment, and an optional address for each one that is ticked. */
export function EnvironmentRows({ rows, onChange, error }: { rows: ApplicationDraft["environments"]; onChange: (rows: ApplicationDraft["environments"]) => void; error?: string }) {
  const { t } = useI18n();
  const toggle = (name: EnvironmentName) => {
    if (rows.some((r) => r.name === name)) return onChange(rows.filter((r) => r.name !== name));
    const added = [...rows, { name, url: "" }];
    onChange(ENVIRONMENT_NAMES.flatMap((n) => added.filter((r) => r.name === n)));
  };
  return (
    <Group legend={t("projects.environments")} error={error}>
      {ENVIRONMENT_NAMES.map((name) => {
        const row = rows.find((r) => r.name === name);
        return (
          <div key={name} className="row-input env-row">
            <label className="check">
              <input type="checkbox" checked={Boolean(row)} onChange={() => toggle(name)} />
              <span>{t(`projects.env.${name}`)}</span>
            </label>
            {row ? (
              <input className="input" type="url" aria-label={`${t("projects.envUrl")} ${t(`projects.env.${name}`)}`} maxLength={projectLimits.urlMax} value={row.url}
                onChange={(e) => onChange(rows.map((r) => r.name === name ? { ...r, url: e.target.value } : r))} />
            ) : <span />}
          </div>
        );
      })}
    </Group>
  );
}

/** The technology stack as removable chips and a box to add one. */
export function TechStackEditor({ items, onChange, error }: { items: string[]; onChange: (items: string[]) => void; error?: string }) {
  const { t } = useI18n();
  const [text, setText] = useState("");
  const add = () => {
    const name = text.trim();
    if (name !== "" && !items.some((i) => i.toLowerCase() === name.toLowerCase()) && items.length < projectLimits.techStackMax) onChange([...items, name]);
    setText("");
  };
  return (
    <Group legend={t("projects.techStack")} error={error}>
      <ul className="chip-list">
        {items.map((item) => (
          <li key={item} className="badge">
            {item}
            <button type="button" className="chip-x" aria-label={`${t("projects.removeRow")} ${item}`} onClick={() => onChange(items.filter((i) => i !== item))}>×</button>
          </li>
        ))}
      </ul>
      <div className="row-input">
        <input className="input" aria-label={t("projects.techName")} maxLength={projectLimits.techNameMax} value={text} onChange={(e) => setText(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") {
              e.preventDefault();
              add();
            }
          }} />
        <Button variant="ghost" onClick={add}>{t("projects.addTech")}</Button>
      </div>
    </Group>
  );
}

/** Non-functional targets: a kind and a sentence each ("answers within 300 ms"). */
export function TargetRows({ rows, onChange, error }: { rows: ApplicationDraft["nonFunctionalTargets"]; onChange: (rows: ApplicationDraft["nonFunctionalTargets"]) => void; error?: string }) {
  const { t } = useI18n();
  const set = (i: number, patch: Partial<ApplicationDraft["nonFunctionalTargets"][number]>) => onChange(rows.map((r, j) => j === i ? { ...r, ...patch } : r));
  return (
    <Group legend={t("projects.targets")} error={error}>
      {rows.map((row, i) => (
        <div key={i} className="row-input">
          <select className="input target-kind" aria-label={`${t("projects.targetKind")} ${i + 1}`} value={row.kind} onChange={(e) => set(i, { kind: e.target.value as TargetKind })}>
            {TARGET_KINDS.map((k) => <option key={k} value={k}>{t(`projects.target.${k}`)}</option>)}
          </select>
          <input className="input" aria-label={`${t("projects.targetText")} ${i + 1}`} maxLength={projectLimits.lineMax} value={row.target} onChange={(e) => set(i, { target: e.target.value })} />
          <Button variant="subtle" aria-label={`${t("projects.removeRow")} ${t("projects.targets")} ${i + 1}`} onClick={() => onChange(rows.filter((_, j) => j !== i))}>×</Button>
        </div>
      ))}
      {rows.length < projectLimits.targetsMax ? <div><Button variant="ghost" onClick={() => onChange([...rows, { kind: "performance", target: "" }])}>{t("projects.addTarget")}</Button></div> : null}
    </Group>
  );
}
