import type { WebRunView } from "../api/types";
import { VisibleText } from "../components/VisibleText";
import { useI18n } from "../i18n";
import { ScriptListing } from "./ScriptListing";

/**
 * What a run would execute, as plain text and never shortened. An argv run shows EVERY element in its own box; a shell
 * run shows its text. Characters that are not plain ASCII are made visible (see lib/visible). Only the requester and the
 * target's owner may see the command; for anyone else the API sends none.
 */
export function RunCommand({ run, onScriptEnd }: { run: Pick<WebRunView, "visible" | "mode" | "argv" | "shellCommand"> & Partial<Pick<WebRunView, "scriptBody" | "scriptArgs" | "scriptInterpreter" | "interpreterPath" | "scriptFiles" | "scriptStdin">>; onScriptEnd?: () => void }) {
  const { t } = useI18n();
  if (!run.visible) return <p className="muted small">{t("remote.commandHidden")}</p>;
  if (run.mode === "script") return <ScriptListing run={{ visible: run.visible, scriptBody: run.scriptBody ?? null, scriptArgs: run.scriptArgs ?? null, scriptInterpreter: run.scriptInterpreter ?? null, interpreterPath: run.interpreterPath ?? null, scriptFiles: run.scriptFiles ?? null, scriptStdin: run.scriptStdin ?? null }} onEnd={onScriptEnd} />;
  if (run.mode === "shell") return <pre className="shell-text" aria-label={t("remote.shellText")}><VisibleText text={run.shellCommand ?? ""} /></pre>;
  return (
    <ol className="argv" aria-label={t("remote.argvList")}>
      {(run.argv ?? []).map((element, i) => (
        <li key={i} className="argv-box">
          <code>{element === "" ? <em className="vis-flag">{t("remote.emptyArg")}</em> : <VisibleText text={element} />}</code>
        </li>
      ))}
    </ol>
  );
}
