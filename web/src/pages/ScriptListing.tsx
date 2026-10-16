import { useCallback, useEffect, useRef, useState } from "react";
import type { WebRunView } from "../api/types";
import { VisibleText } from "../components/VisibleText";
import { useI18n } from "../i18n";

type ScriptRun = Pick<WebRunView, "visible" | "scriptBody" | "scriptArgs" | "scriptInterpreter" | "interpreterPath"> & Partial<Pick<WebRunView, "scriptFiles" | "scriptStdin">>;

/** The lines of a script text; a final line break does not make an extra empty line. */
export const scriptLines = (body: string): string[] => {
  const lines = body.split("\n");
  if (lines.length > 1 && lines[lines.length - 1] === "") lines.pop();
  return lines;
};

/** One text the owner must read: every line numbered, never shortened, anything that is not plain ASCII made visible. `onEnd` fires when its end has been in view (at once when it fits). */
function ScrollBox({ text, label, onEnd }: { text: string; label: string; onEnd: () => void }) {
  const { t } = useI18n();
  const box = useRef<HTMLDivElement>(null);
  const atEnd = () => {
    const el = box.current;
    if (el && el.scrollTop + el.clientHeight >= el.scrollHeight - 2) onEnd();
  };
  useEffect(atEnd); // a text that fits needs no scrolling
  const lines = scriptLines(text);
  return (
    <div ref={box} className="script-scroll" role="region" tabIndex={0} aria-label={label} onScroll={atEnd}>
      {text === "" ? <p className="muted">{t("remote.scriptNoText")}</p> : (
        <ol className="script-lines">
          {lines.map((line, i) => (
            <li key={i}><span className="ln" aria-hidden="true">{i + 1}</span><code><VisibleText text={line} /></code></li>
          ))}
        </ol>
      )}
    </div>
  );
}

/**
 * The whole script an owner is asked to allow (ADR-0008 §3c, ADR-0010 §2): the script, then every file next to it and the stdin text, each with every line
 * numbered and never shortened; the interpreter and where it lives; the arguments, one box each. Each box scrolls, and `onEnd` fires once EVERY box has had
 * its end in view, so the Allow button waits for all of them. Only the requester and the target's owner see the text.
 */
export function ScriptListing({ run, onEnd }: { run: ScriptRun; onEnd?: () => void }) {
  const { t } = useI18n();
  const [done, setDone] = useState<ReadonlySet<string>>(new Set());
  const files = run.scriptFiles ?? [];
  const stdin = run.scriptStdin ?? "";
  const ids = ["script", ...files.map((f) => `file:${f.path}`), ...(stdin !== "" ? ["stdin"] : [])];
  const seen = useCallback((id: string) => setDone((previous) => (previous.has(id) ? previous : new Set(previous).add(id))), []);
  const everything = ids.every((id) => done.has(id));
  useEffect(() => {
    if (everything) onEnd?.();
  }, [everything, onEnd]);

  if (!run.visible) return <p className="muted small">{t("remote.commandHidden")}</p>;
  const body = run.scriptBody ?? "";
  const lines = scriptLines(body);
  return (
    <div className="stack">
      <p className="small">
        <span className="muted">{t("remote.scriptInterpreter")}: </span>
        <code>{run.scriptInterpreter ?? "?"}</code>{run.interpreterPath ? <> <span className="muted">({run.interpreterPath})</span></> : null}
        {" "}<span className="muted">· {t("remote.scriptLines", { n: lines.length })}</span>
      </p>
      <ScrollBox text={body} label={t("remote.scriptListing")} onEnd={() => seen("script")} />
      {files.length > 0 || stdin !== "" ? <p className="muted small">{t("remote.scriptExtras", { n: files.length })}</p> : null}
      {files.map((f) => (
        <div key={f.path}>
          <p className="small"><code><VisibleText text={f.path} /></code> <span className="muted">· {t("remote.scriptChars", { n: f.text.length })}</span></p>
          <ScrollBox text={f.text} label={t("remote.scriptFile", { path: f.path })} onEnd={() => seen(`file:${f.path}`)} />
        </div>
      ))}
      {stdin !== "" ? (
        <div>
          <p className="small"><span className="muted">{t("remote.scriptStdin")} · {t("remote.scriptChars", { n: stdin.length })}</span></p>
          <ScrollBox text={stdin} label={t("remote.scriptStdin")} onEnd={() => seen("stdin")} />
        </div>
      ) : null}
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
