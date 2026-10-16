import { useEffect, useRef } from "react";
import type { WebRunView } from "../api/types";
import { VisibleText } from "../components/VisibleText";
import { useI18n } from "../i18n";

type ScriptRun = Pick<WebRunView, "visible" | "scriptBody" | "scriptArgs" | "scriptInterpreter" | "interpreterPath">;

/** The lines of a script text; a final line break does not make an extra empty line. */
export const scriptLines = (body: string): string[] => {
  const lines = body.split("\n");
  if (lines.length > 1 && lines[lines.length - 1] === "") lines.pop();
  return lines;
};

/**
 * The whole script an owner is asked to allow (ADR-0008 §3c): every line numbered, never shortened, with anything that is not plain ASCII made
 * visible; the interpreter and where it lives; the arguments, one box each. The box scrolls, and `onEnd` fires when its end has been in view
 * (at once when the script fits), so the Allow button can wait for it. Only the requester and the target's owner see the text.
 */
export function ScriptListing({ run, onEnd }: { run: ScriptRun; onEnd?: () => void }) {
  const { t } = useI18n();
  const box = useRef<HTMLDivElement>(null);
  const body = run.scriptBody ?? "";

  const atEnd = () => {
    const el = box.current;
    if (el && el.scrollTop + el.clientHeight >= el.scrollHeight - 2) onEnd?.();
  };
  // A script that fits needs no scrolling.
  useEffect(atEnd);

  if (!run.visible) return <p className="muted small">{t("remote.commandHidden")}</p>;
  const lines = scriptLines(body);
  return (
    <div className="stack">
      <p className="small">
        <span className="muted">{t("remote.scriptInterpreter")}: </span>
        <code>{run.scriptInterpreter ?? "?"}</code>{run.interpreterPath ? <> <span className="muted">({run.interpreterPath})</span></> : null}
        {" "}<span className="muted">· {t("remote.scriptLines", { n: lines.length })}</span>
      </p>
      <div ref={box} className="script-scroll" role="region" tabIndex={0} aria-label={t("remote.scriptListing")} onScroll={atEnd}>
        {body === "" ? <p className="muted">{t("remote.scriptNoText")}</p> : (
          <ol className="script-lines">
            {lines.map((line, i) => (
              <li key={i}><span className="ln" aria-hidden="true">{i + 1}</span><code><VisibleText text={line} /></code></li>
            ))}
          </ol>
        )}
      </div>
      <div>
        <span className="muted small">{t("remote.scriptArgs")}</span>
        {(run.scriptArgs ?? []).length === 0 ? <p className="muted small">{t("remote.scriptNoArgs")}</p> : (
          <ol className="argv" aria-label={t("remote.scriptArgs")}>
            {(run.scriptArgs ?? []).map((arg, i) => (
              <li key={i} className="argv-box"><code>{arg === "" ? <em className="vis-flag">{t("remote.emptyArg")}</em> : <VisibleText text={arg} />}</code></li>
            ))}
          </ol>
        )}
      </div>
    </div>
  );
}
