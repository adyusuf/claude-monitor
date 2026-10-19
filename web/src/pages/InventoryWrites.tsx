import { useState } from "react";
import type { InventoryPlugin, InventorySetting } from "../api/types";
import { Button } from "../components/ui";
import { useI18n } from "../i18n";

/** What the owner can ask the agent to do with Claude's setup (ADR-0011 §7a). The names and values are the API's closed lists. */
export type Ask = (kind: "plugin_enable" | "plugin_disable" | "plugin_update" | "plugin_install" | "project_setting", extra: { target: string; value?: string; projectKey?: string }) => Promise<void>;

export const MODELS = ["keep", "sonnet", "haiku", "fable"] as const;
export const MODES = ["A", "B", "C", "D", "E"] as const;
const BRANCHES = ["dev", "test", "prod"] as const;
export const GATES: readonly string[] = [
  ...["merge", "push"].flatMap((verb) => BRANCHES.map((b) => `${verb}.${b}`)),
  ...BRANCHES.flatMap((b) => ["unit", "e2e"].map((kind) => `run_before_push.${b}.${kind}`)),
];

/** Enable or disable and update for one installed plugin. */
export function PluginButtons({ plugin, ask, busy }: { plugin: InventoryPlugin; ask: Ask; busy: boolean }) {
  const { t } = useI18n();
  return (
    <span className="row-actions">
      <Button variant="subtle" busy={busy} onClick={() => void ask(plugin.enabled ? "plugin_disable" : "plugin_enable", { target: plugin.id })}>
        {plugin.enabled ? t("machines.inventory.disable") : t("machines.inventory.enable")}
      </Button>
      <Button variant="subtle" busy={busy} onClick={() => void ask("plugin_update", { target: plugin.id })}>{t("machines.inventory.update")}</Button>
    </span>
  );
}

/** Install by id (name@marketplace): the machine installs only what its own marketplaces offer. */
export function InstallPlugin({ ask, busy }: { ask: Ask; busy: boolean }) {
  const { t } = useI18n();
  const [id, setId] = useState("");
  return (
    <div className="row">
      <label className="stack">
        <span className="small">{t("machines.inventory.installLabel")}</span>
        <input value={id} maxLength={200} placeholder="name@marketplace" onChange={(e) => setId(e.target.value.trim())} />
      </label>
      <Button variant="subtle" busy={busy} disabled={!/^[A-Za-z0-9][A-Za-z0-9._-]*@[A-Za-z0-9][A-Za-z0-9._-]*$/.test(id)} onClick={() => void ask("plugin_install", { target: id })}>
        {t("machines.inventory.install")}
      </Button>
    </div>
  );
}

/** One change to a project's adyusuf files: a name from the closed list and a value that fits it. */
export function SettingForm({ setting, ask, busy }: { setting: InventorySetting; ask: Ask; busy: boolean }) {
  const { t } = useI18n();
  const [name, setName] = useState("mode");
  const [value, setValue] = useState("");
  const options: readonly string[] = name === "mode" ? MODES : name === "mode_model" ? MODELS : ["true", "false"];
  const chosen = options.includes(value) ? value : options[0]!;
  return (
    <div className="row small">
      <label className="stack">
        <span>{t("machines.inventory.setting")}</span>
        <select value={name} onChange={(e) => { setName(e.target.value); setValue(""); }}>
          {["mode", "mode_model", ...GATES].map((n) => <option key={n} value={n}>{n}</option>)}
        </select>
      </label>
      <label className="stack">
        <span>{t("machines.inventory.value")}</span>
        <select value={chosen} onChange={(e) => setValue(e.target.value)}>
          {options.map((o) => <option key={o} value={o}>{o === "true" ? t("machines.inventory.yes") : o === "false" ? t("machines.inventory.no") : o}</option>)}
        </select>
      </label>
      <Button variant="subtle" busy={busy} onClick={() => void ask("project_setting", { projectKey: setting.projectKey, target: name, value: chosen })}>{t("machines.inventory.apply")}</Button>
    </div>
  );
}
