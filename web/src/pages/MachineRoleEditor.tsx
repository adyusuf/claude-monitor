import { useState } from "react";
import { api } from "../api/endpoints";
import type { MachineEnvironment, MachineFunction, MachineView } from "../api/types";
import { Icon } from "../components/icons";
import { Button, Notice } from "../components/ui";
import { useI18n } from "../i18n";
import { ENVIRONMENT_ORDER, environmentsOf, FUNCTION_ORDER, functionsOf, normalize, strictest } from "../lib/roles";
import { isStrict, RoleBadge } from "./MachineBadges";
import { useCodeGate } from "./useCodeGate";

const STRICT: readonly string[] = ["production", "database"];

/**
 * One set of the role as chips: each value is a chip with an edit (it turns into a list to pick another value) and a remove; a list below adds a
 * value not in the set yet. Changes stay here until the role is saved.
 */
function ValueChips<T extends string>({ label, addLabel, values, order, name, onChange }: {
  label: string; addLabel: string; values: T[]; order: readonly T[]; name: (v: T) => string; onChange: (next: T[]) => void;
}) {
  const { t } = useI18n();
  const [editing, setEditing] = useState<T | null>(null);
  const free = order.filter((v) => !values.includes(v));
  return (
    <div className="stack role-set">
      <span className="field-label">{label}</span>
      {values.length > 0 ? (
        <ul className="chips" aria-label={label}>
          {values.map((v) => (
            <li key={v}>
              {editing === v ? (
                <select className="input role-edit" aria-label={t("machines.role.editValue", { value: v })} autoFocus value={v}
                  onBlur={() => setEditing(null)}
                  onChange={(e) => {
                    onChange(normalize(values.map((x) => (x === v ? (e.target.value as T) : x)), order));
                    setEditing(null);
                  }}>
                  {[v, ...free].map((o) => <option key={o} value={o}>{o} · {name(o)}</option>)}
                </select>
              ) : (
                <span className={STRICT.includes(v) ? "tag-chip role-chip-strict" : "tag-chip"} title={name(v)}>
                  {v}
                  <button type="button" className="chip-x" aria-label={t("machines.role.editValue", { value: v })} title={t("machines.role.editValue", { value: v })}
                    disabled={free.length === 0} onClick={() => setEditing(v)}><Icon name="edit" /></button>
                  <button type="button" className="chip-x" aria-label={t("machines.role.removeValue", { value: v })} title={t("machines.role.removeValue", { value: v })}
                    onClick={() => onChange(values.filter((x) => x !== v))}><Icon name="close" /></button>
                </span>
              )}
            </li>
          ))}
        </ul>
      ) : <span className="muted small">{t("machines.role.notSet")}</span>}
      {free.length > 0 ? (
        <select className="input" aria-label={addLabel} value="" onChange={(e) => {
          if (e.target.value) onChange(normalize([...values, e.target.value as T], order));
        }}>
          <option value="">{addLabel}</option>
          {free.map((v) => <option key={v} value={v}>{v} · {name(v)}</option>)}
        </select>
      ) : null}
    </div>
  );
}

/**
 * The role of a machine (ADR-0008 §3a, a set since 08/11/2026): a workspace admin gives it one or more environments and functions, each a chip
 * that is edited or removed from the chip; anyone else reads them. Dropping production or database answers 403 "reauth_required", which the
 * code gate turns into the code field. The machine's own claim is shown beside it and never edited here: it only tightens.
 */
export function MachineRoleEditor({ agentId, view: m, canEdit, onChanged }: { agentId: string; view: MachineView; canEdit: boolean; onChanged: () => void }) {
  const { t } = useI18n();
  const [envs, setEnvs] = useState<MachineEnvironment[]>(() => environmentsOf(m));
  const [fns, setFns] = useState<MachineFunction[]>(() => functionsOf(m));
  const [saved, setSaved] = useState(false);
  // A re-read machine (a live message, or the reload after a save) resets the chips to what is stored; the saved notice stays.
  const stored = `${environmentsOf(m).join(",")}/${functionsOf(m).join(",")}`;
  const [seen, setSeen] = useState(stored);
  if (seen !== stored) {
    setSeen(stored);
    setEnvs(environmentsOf(m));
    setFns(functionsOf(m));
  }
  const gate = useCodeGate("reauth_required", "machines.role.lowerHint");
  const claim = m.claimedEnvironment || m.claimedFunction
    ? t("machines.role.claimed", { role: `${m.claimedEnvironment ?? t("machines.role.notSet")}/${m.claimedFunction ?? t("machines.role.notSet")}` }) : null;
  const changed = `${envs.join(",")}/${fns.join(",")}` !== stored;

  const save = async () => {
    setSaved(false);
    const input = { environment: strictest(envs, ENVIRONMENT_ORDER), function: strictest(fns, FUNCTION_ORDER), environments: envs, functions: fns };
    const ok = await gate.attempt((code) => api.setAgentRole(agentId, { ...input, code }).then(() => undefined));
    if (ok) {
      setSaved(true);
      onChanged();
    }
  };

  return (
    <div className="stack">
      <p><RoleBadge view={m} />{isStrict(m) ? <> <span className="muted small">{t("machines.role.strict")}</span></> : null}</p>
      {claim ? <p className="muted small">{claim} {t("machines.role.claimTightens")}</p> : null}
      {canEdit ? (
        <form className="stack" onSubmit={(e) => {
          e.preventDefault();
          void save();
        }}>
          <div className="row">
            <ValueChips label={t("machines.role.environments")} addLabel={t("machines.role.addEnvironment")} values={envs} order={ENVIRONMENT_ORDER}
              name={(v) => t(`machines.role.env.${v}`)} onChange={setEnvs} />
            <ValueChips label={t("machines.role.functions")} addLabel={t("machines.role.addFunction")} values={fns} order={FUNCTION_ORDER}
              name={(v) => t(`machines.role.fn.${v}`)} onChange={setFns} />
          </div>
          <p className="muted small">{t("machines.role.setHint")}</p>
          {gate.field}
          {gate.error ? <Notice kind="error">{gate.error}</Notice> : null}
          {saved ? <Notice kind="success">{t("machines.role.saved")}</Notice> : null}
          <div className="row-actions"><Button type="submit" busy={gate.busy} disabled={!changed && !gate.field}>{t("machines.role.save")}</Button></div>
        </form>
      ) : <p className="muted small">{t("machines.role.adminOnly")}</p>}
    </div>
  );
}
