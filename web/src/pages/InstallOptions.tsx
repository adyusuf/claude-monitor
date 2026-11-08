import { useState } from "react";
import { Card, CopyText } from "../components/ui";
import { useI18n } from "../i18n";

type Mode = "plugin" | "standalone" | "service";
type Exec = "off" | "argv" | "shell";

/** The agent's own rule for a run account's name (RunAccount.IsValidName): a plain name of at most 32 characters, not a system one. */
const RUN_ACCOUNT_NAME = /^[A-Za-z_][A-Za-z0-9_-]{0,31}$/;
const RESERVED = ["root", "wheel", "admin", "sudo", "daemon", "nobody", "system"];
export const runAccountNameOk = (name: string): boolean => RUN_ACCOUNT_NAME.test(name) && !RESERVED.includes(name.toLowerCase());

/**
 * The commands for the choices made, in order; every flag is one the agent already has (see `aipod help`). The level of remote runs is always
 * written, "off" too, so a reinstall never keeps what an earlier one set. With Claude Code, `aipod install` alone registers the plugin and
 * `aipod install --exec` alone only sets the level, so that mode gives both. A service is installed by an administrator through sudo, with
 * the agent's full path (sudo does not search the user's own folders). Null when a run account's name is not one the agent accepts.
 */
export function installCommands(o: { mode: Mode; exec: Exec; server: string; runAccount: boolean; runAccountName: string }): string[] | null {
  if (o.mode === "plugin") return ["aipod install", `aipod install --exec ${o.exec}`];
  if (o.mode === "standalone") return [`aipod install --standalone --server ${o.server} --exec ${o.exec}`];
  const name = o.runAccountName.trim();
  if (o.runAccount && name !== "" && !runAccountNameOk(name)) return null;
  const run = o.runAccount ? (name ? ` --run-account ${name}` : " --run-account") : "";
  return [`sudo ~/.aipod/bin/aipod install --service --server ${o.server} --exec ${o.exec}${run}`];
}

/**
 * Install choices (ADR-0020 part 1): how the agent starts, which account it runs as, and whether it runs approved remote commands. It never
 * runs as an administrator; the page says so instead of offering it. The answer is the command to type; the agent checks every choice again.
 */
export function InstallOptions({ server }: { server: string }) {
  const { t } = useI18n();
  const [mode, setMode] = useState<Mode>("plugin");
  const [exec, setExec] = useState<Exec>("off");
  const [runAccount, setRunAccount] = useState(false);
  const [runAccountName, setRunAccountName] = useState("");
  const commands = installCommands({ mode, exec, server, runAccount, runAccountName });
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
              <label className="check"><input type="checkbox" checked={runAccount} onChange={(e) => setRunAccount(e.target.checked)} /> {t("installOptions.runAccount")}</label>
              {runAccount ? (
                <label className="field">
                  <span className="field-label">{t("installOptions.runAccountName")}</span>
                  <input className="input" value={runAccountName} maxLength={32} placeholder={t("installOptions.runAccountDefault")}
                    aria-invalid={commands === null} onChange={(e) => setRunAccountName(e.target.value)} />
                  {commands === null ? <span className="field-error">{t("installOptions.runAccountInvalid")}</span> : null}
                </label>
              ) : null}
            </>
          ) : <p className="small">{t("installOptions.ownAccount")}</p>}
        </fieldset>
        <fieldset className="field">
          <legend className="field-label">{t("installOptions.admin")}</legend>
          <p className="muted small">{mode === "service" ? t("installOptions.adminService") : t("installOptions.adminUser")}</p>
        </fieldset>
        <label className="field">
          <span className="field-label">{t("installOptions.exec")}</span>
          <select className="input" value={exec} onChange={(e) => setExec(e.target.value as Exec)}>
            <option value="off">{t("installOptions.execs.off")}</option>
            <option value="argv">{t("installOptions.execs.argv")}</option>
            <option value="shell">{t("installOptions.execs.shell")}</option>
          </select>
        </label>
        {commands ? (
          <div className="stack-tight">
            <p className="muted">{mode === "service" ? t("installOptions.runService") : t("installOptions.runUser")}</p>
            {commands.map((c) => <CopyText key={c} text={c} />)}
          </div>
        ) : null}
      </div>
    </Card>
  );
}
