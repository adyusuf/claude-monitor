import { useState } from "react";
import { Card, CopyText } from "../components/ui";
import { useI18n } from "../i18n";

type Mode = "plugin" | "standalone" | "service";
type Exec = "off" | "argv" | "shell";

/** The `aipod install` command for the choices made; every flag is one the agent already has (see `aipod help`). */
export function installCommand(o: { mode: Mode; exec: Exec; server: string; runAccount: boolean; runAccountName: string; allowRoot: boolean }): string {
  const parts = ["aipod", "install"];
  if (o.mode === "standalone") parts.push("--standalone", "--server", o.server);
  if (o.mode === "service") parts.push("--service", "--server", o.server);
  if (o.exec !== "off" && !(o.mode === "service" && o.allowRoot)) parts.push("--exec", o.exec);
  if (o.mode === "service" && o.allowRoot) parts.push("--allow-root");
  if (o.mode === "service" && o.runAccount && !o.allowRoot) {
    parts.push("--run-account");
    const name = o.runAccountName.trim();
    if (/^[a-z_][a-z0-9_-]{0,31}$/.test(name)) parts.push(name);
  }
  return parts.join(" ");
}

/**
 * Install choices (08/11/2026): how the agent starts, which account it runs as, whether it may run as an administrator, and whether it runs
 * approved remote commands. The answer is the one command to type; the agent itself checks every choice again when it installs.
 */
export function InstallOptions({ server }: { server: string }) {
  const { t } = useI18n();
  const [mode, setMode] = useState<Mode>("plugin");
  const [exec, setExec] = useState<Exec>("off");
  const [runAccount, setRunAccount] = useState(false);
  const [runAccountName, setRunAccountName] = useState("");
  const [allowRoot, setAllowRoot] = useState(false);
  const command = installCommand({ mode, exec, server, runAccount, runAccountName, allowRoot });
  return (
    <Card>
      <div className="stack install-options">
        <label className="field">
          <span className="field-label">{t("installOptions.mode")}</span>
          <select className="input" value={mode} onChange={(e) => setMode(e.target.value as Mode)}>
            <option value="plugin">{t("installOptions.modes.plugin")}</option>
            <option value="standalone">{t("installOptions.modes.standalone")}</option>
            <option value="service">{t("installOptions.modes.service")}</option>
          </select>
          <span className="muted small">{t(`installOptions.modeHint.${mode}`)}</span>
        </label>
        <fieldset className="field">
          <legend className="field-label">{t("installOptions.runsAs")}</legend>
          {mode === "service" ? (
            <>
              <p className="small">{t("installOptions.serviceAccount")}</p>
              <label className="check"><input type="checkbox" checked={runAccount} disabled={allowRoot} onChange={(e) => setRunAccount(e.target.checked)} /> {t("installOptions.runAccount")}</label>
              {runAccount && !allowRoot ? (
                <label className="field">
                  <span className="field-label">{t("installOptions.runAccountName")}</span>
                  <input className="input" value={runAccountName} maxLength={32} placeholder={t("installOptions.runAccountDefault")} onChange={(e) => setRunAccountName(e.target.value)} />
                </label>
              ) : null}
            </>
          ) : <p className="small">{t("installOptions.ownAccount")}</p>}
        </fieldset>
        <fieldset className="field">
          <legend className="field-label">{t("installOptions.admin")}</legend>
          {mode === "service" ? (
            <label className="check"><input type="checkbox" checked={allowRoot} onChange={(e) => setAllowRoot(e.target.checked)} /> {t("installOptions.allowRoot")}</label>
          ) : null}
          <p className="muted small">{mode === "service" ? (allowRoot ? t("installOptions.allowRootOn") : t("installOptions.adminService")) : t("installOptions.adminUser")}</p>
        </fieldset>
        <label className="field">
          <span className="field-label">{t("installOptions.exec")}</span>
          <select className="input" value={exec} disabled={mode === "service" && allowRoot} onChange={(e) => setExec(e.target.value as Exec)}>
            <option value="off">{t("installOptions.execs.off")}</option>
            <option value="argv">{t("installOptions.execs.argv")}</option>
            <option value="shell">{t("installOptions.execs.shell")}</option>
          </select>
        </label>
        <div className="stack-tight">
          <p className="muted">{mode === "service" ? t("installOptions.runService") : t("installOptions.runUser")}</p>
          <CopyText text={command} />
        </div>
      </div>
    </Card>
  );
}
