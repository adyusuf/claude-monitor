import { useState } from "react";
import { Card, CopyText } from "../components/ui";
import { useI18n } from "../i18n";

type Mode = "plugin" | "standalone" | "service";
type Exec = "off" | "argv" | "shell";

/** The agent's own rule for a run account's name (RunAccount.IsValidName): a plain name of at most 32 characters, not a system one. */
const RUN_ACCOUNT_NAME = /^[A-Za-z_][A-Za-z0-9_-]{0,31}$/;
const RESERVED = ["root", "wheel", "admin", "sudo", "daemon", "nobody", "system", "aipod", "_aipod"];
export const runAccountNameOk = (name: string): boolean => RUN_ACCOUNT_NAME.test(name) && !RESERVED.includes(name.toLowerCase());

/** Where the installer puts the agent, per system (scripts/install/aipod-install.sh and .ps1; an AP_AGENT_HOME override moves it): sudo needs this full path. */
const AGENT_PATH = {
  linux: "~/.aipod/bin/aipod",
  macos: "\"$HOME/Library/Application Support/aipod/bin/aipod\"",
  windows: "& \"$env:USERPROFILE\\.aipod\\bin\\aipod.exe\"",
} as const;

export type InstallOs = keyof typeof AGENT_PATH;
export interface InstallLine { os?: InstallOs; text: string }
export type InstallProblem = "runAccountInvalid" | "serviceHttps" | "standaloneHttps";
export type InstallAnswer = { lines: InstallLine[]; noWindows: boolean } | { problem: InstallProblem };

const LOOPBACK = new Set(["localhost", "127.0.0.1", "[::1]"]);

/**
 * The agent's own rule for the server address (ServiceCommands, StandaloneCommands): a service takes only https, without spaces, control
 * characters, quotes, % or |; a daemon at sign-in also takes http on this machine (localhost). Null when the address is accepted.
 */
export function serverProblem(mode: Mode, server: string): InstallProblem | null {
  let url: URL;
  try {
    url = new URL(server);
  } catch {
    return mode === "service" ? "serviceHttps" : mode === "standalone" ? "standaloneHttps" : null;
  }
  const unsafe = [...server].some((c) => /[\s"%|]/.test(c) || c.charCodeAt(0) < 0x20 || c.charCodeAt(0) === 0x7f);
  if (mode === "service") return url.protocol === "https:" && !unsafe ? null : "serviceHttps";
  if (mode === "standalone") return url.protocol === "https:" || (url.protocol === "http:" && LOOPBACK.has(url.hostname)) ? null : "standaloneHttps";
  return null;
}

/**
 * The commands for the choices made, in order; every flag is one the agent already has (see `aipod help`). The level of remote runs is always
 * written, "off" too, so a reinstall never keeps what an earlier one set. With Claude Code, `aipod install` alone registers the plugin and
 * `aipod install --exec` alone only sets the level, so that mode gives both. A service is installed by an administrator with the agent's full
 * path (sudo does not search the user's own folders), one line per system; a run account exists on Linux and macOS only, so with one there is
 * no Windows line. A problem instead when the agent would refuse the choices: a run account's name, or a server address it does not take.
 */
export function installCommands(o: { mode: Mode; exec: Exec; server: string; runAccount: boolean; runAccountName: string }): InstallAnswer {
  const server = o.server.replace(/\/+$/, "");
  if (o.mode === "plugin") return { lines: [{ text: "aipod install" }, { text: `aipod install --exec ${o.exec}` }], noWindows: false };
  const problem = serverProblem(o.mode, server);
  if (problem) return { problem };
  if (o.mode === "standalone") return { lines: [{ text: `aipod install --standalone --server ${server} --exec ${o.exec}` }], noWindows: false };
  const name = o.runAccountName.trim();
  if (o.runAccount && name !== "" && !runAccountNameOk(name)) return { problem: "runAccountInvalid" };
  const run = o.runAccount ? (name ? ` --run-account ${name}` : " --run-account") : "";
  const args = `install --service --server ${server} --exec ${o.exec}${run}`;
  const lines: InstallLine[] = [{ os: "linux", text: `sudo ${AGENT_PATH.linux} ${args}` }, { os: "macos", text: `sudo ${AGENT_PATH.macos} ${args}` }];
  if (!o.runAccount) lines.push({ os: "windows", text: `${AGENT_PATH.windows} ${args}` });
  return { lines, noWindows: o.runAccount };
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
  const answer = installCommands({ mode, exec, server, runAccount, runAccountName });
  const problem = "problem" in answer ? answer.problem : null;
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
                    aria-invalid={problem === "runAccountInvalid"} onChange={(e) => setRunAccountName(e.target.value)} />
                  {problem === "runAccountInvalid" ? <span className="field-error">{t("installOptions.runAccountInvalid")}</span> : null}
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
        {"lines" in answer ? (
          <div className="stack-tight">
            <p className="muted">{mode === "service" ? t("installOptions.runService") : t("installOptions.runUser")}</p>
            {answer.lines.map((c) => (
              <div key={c.text} className="stack-tight">
                {c.os ? <span className="muted small">{t(`installOptions.os.${c.os}`)}</span> : null}
                <CopyText text={c.text} />
              </div>
            ))}
            {answer.noWindows ? <p className="muted small">{t("installOptions.noWindows")}</p> : null}
          </div>
        ) : problem === "serviceHttps" || problem === "standaloneHttps" ? <p className="field-error">{t(`installOptions.${problem}`)}</p> : null}
      </div>
    </Card>
  );
}
