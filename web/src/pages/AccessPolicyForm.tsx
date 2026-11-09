import { useEffect, useState } from "react";
import { OS_KINDS, type AccessOsRules, type AccessPolicyDocument, type AccessPolicyView, type OsKind } from "../api/typesAccess";
import { Button, Notice } from "../components/ui";
import { useI18n } from "../i18n";
import { approvalKeys } from "../lib/approvalKey";
import { isInterpreter } from "../lib/accessPaths";
import { clearedDocument, sectionOf, withSection } from "../lib/accessPolicy";
import { AccessPathRows } from "./AccessPathRows";
import { Group } from "./ProjectBits";
import { useAccessPolicySave } from "./useAccessPolicySave";

// Blank rows are what the person has not filled in yet; the rest is sent as typed, without the spaces at its ends.
const filled = (rules: AccessOsRules): AccessOsRules => ({
  allowedFolders: rules.allowedFolders.map((p) => p.trim()).filter(Boolean),
  deniedFolders: rules.deniedFolders.map((p) => p.trim()).filter(Boolean),
  applications: rules.applications.map((a) => ({ path: a.path.trim(), sha256: a.sha256?.trim() || null })).filter((a) => a.path || a.sha256),
});
const withoutBlanks = (d: AccessPolicyDocument): AccessPolicyDocument => ({ ...d, linux: filled(d.linux), macOs: filled(d.macOs), windows: filled(d.windows) });

/** Where an edit starts: the saved document, or after a policy was taken back the same keys with the project folder allowed again. */
const startOf = (view: AccessPolicyView): AccessPolicyDocument =>
  view.document.cleared ? { ...view.document, includeProjectFolder: true, cleared: false } : view.document;

/**
 * Editing a project's access policy (ADR-0020 §4): the repositories it covers (from the project's applications), whether each machine's project
 * folder is allowed, per OS the allowed and denied folders and the applications, and whether shell runs are allowed. Saving signs the policy with
 * this browser's approval key when it holds one; the API asks for the code.
 */
export function AccessPolicyForm({ view, ws, onSaved, onConflict, onCancel }: {
  view: AccessPolicyView; ws: string; onSaved: (signed: boolean) => void; onConflict: () => void; onCancel: () => void;
}) {
  const { t } = useI18n();
  const [draft, setDraft] = useState<AccessPolicyDocument>(() => startOf(view));
  const [os, setOs] = useState<OsKind>("linux");
  const [hasKey, setHasKey] = useState<boolean | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const saver = useAccessPolicySave(view, ws, onSaved, onConflict);
  const { gate } = saver;

  useEffect(() => {
    let live = true;
    approvalKeys.current().then((key) => { if (live) setHasKey(key !== null); }).catch(() => { if (live) setProblem(t("access.keyUnreadable")); });
    return () => { live = false; };
    // t changes with the language only; the key is read once per form
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const keys = [...new Set([...view.candidateKeys, ...draft.projectKeys])].sort();
  const toggleKey = (key: string, on: boolean) =>
    setDraft({ ...draft, projectKeys: on ? [...draft.projectKeys, key] : draft.projectKeys.filter((k) => k !== key) });
  const interpreter = OS_KINDS.some((o) => sectionOf(draft, o).applications.some((a) => isInterpreter(a.path.trim(), o)));
  const unsignedSkipped = view.machinesRequiringSignature > 0 && hasKey === false;

  const save = () => void saver.submit(withoutBlanks(draft));
  const takeBack = () => {
    if (window.confirm(t("access.clearConfirm"))) void saver.submit(clearedDocument(view.document.projectKeys));
  };

  return (
    <div className="stack">
      <Group legend={t("access.keys")}>
        <p className="muted small">{t("access.keysHint")}</p>
        {keys.length === 0 ? <p className="muted">{t("access.noCandidates")}</p> : (
          <div className="stack-tight">
            {keys.map((key) => (
              <label key={key} className="check">
                <input type="checkbox" checked={draft.projectKeys.includes(key)} onChange={(e) => toggleKey(key, e.target.checked)} />
                <code>{key}</code>
              </label>
            ))}
          </div>
        )}
      </Group>
      <label className="check">
        <input type="checkbox" checked={draft.includeProjectFolder} onChange={(e) => setDraft({ ...draft, includeProjectFolder: e.target.checked })} />
        <span>{t("access.projectFolder")}<span className="field-hint">{t("access.projectFolderHint")}</span></span>
      </label>
      <div className="seg" role="tablist" aria-label={t("access.osTabs")}>
        {OS_KINDS.map((o) => (
          <button key={o} type="button" role="tab" aria-selected={os === o} className={os === o ? "seg-on" : ""} onClick={() => setOs(o)}>{t(`access.os.${o}`)}</button>
        ))}
      </div>
      <div role="tabpanel" aria-label={t(`access.os.${os}`)}>
        <AccessPathRows key={os} os={os} rules={sectionOf(draft, os)} projectFolder={draft.includeProjectFolder} onChange={(rules) => setDraft(withSection(draft, os, rules))} />
      </div>
      {interpreter ? <div className="notice notice-error" role="alert">{t("access.interpreterWarning")}</div> : null}
      <label className="check">
        <input type="checkbox" role="switch" checked={draft.shell} onChange={(e) => setDraft({ ...draft, shell: e.target.checked })} />
        <span>{t("access.shell")}<span className="field-hint">{t("access.shellHint")}</span></span>
      </label>
      {draft.shell ? <div className="notice notice-error" role="alert">{t("access.shellWarning")}</div> : null}
      {hasKey ? <p className="muted small">{t("access.willSign")}</p> : null}
      {unsignedSkipped ? <div className="notice notice-error" role="alert">{t("access.unsignedSkipped", { n: view.machinesRequiringSignature })}</div> : null}
      {gate.field}
      {problem ? <Notice kind="error">{problem}</Notice> : null}
      {saver.problem ? <Notice kind="error">{saver.problem}</Notice> : null}
      {gate.error ? <Notice kind="error">{gate.error}</Notice> : null}
      <div className="row-actions">
        {view.version > 0 && !view.document.cleared ? <Button variant="danger" busy={gate.busy} disabled={!gate.ready} onClick={takeBack}>{t("access.clear")}</Button> : null}
        <Button variant="ghost" onClick={onCancel}>{t("common.cancel")}</Button>
        <Button busy={gate.busy} disabled={!gate.ready || draft.projectKeys.length === 0} onClick={save}>{t("access.save")}</Button>
      </div>
    </div>
  );
}
