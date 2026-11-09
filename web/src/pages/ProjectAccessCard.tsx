import { useCallback, useEffect, useState } from "react";
import { ApiError } from "../api/client";
import { api } from "../api/endpoints";
import { OS_KINDS, type AccessPolicyView } from "../api/typesAccess";
import { Button, Card, Notice, Spinner } from "../components/ui";
import { VisibleText } from "../components/VisibleText";
import { useErrorText, useI18n } from "../i18n";
import { sectionOf } from "../lib/accessPolicy";
import { AccessLists } from "./AccessLists";
import { AccessPolicyForm } from "./AccessPolicyForm";

/** What a saved policy is: its version, whether it is signed and by whom, or that none is saved (the default) or it was taken back. */
function PolicyState({ view }: { view: AccessPolicyView }) {
  const { t } = useI18n();
  if (view.isDefault) return <p className="muted">{t("access.none")}</p>;
  return (
    <p className="row small">
      <span className="badge">{t("access.version", { version: view.version })}</span>
      {view.isSigned
        ? <span className="badge ok">{view.signerName ? t("access.signedBy", { name: view.signerName }) : t("access.signed")}</span>
        : <span className="badge warn">{t("access.unsigned")}</span>}
      {view.document.cleared ? <span className="muted">{t("access.cleared")}</span> : null}
    </p>
  );
}

/** Repositories the machines report spelled differently from the project's (case or Unicode form): the policy still covers them. */
function NearKeys({ keys }: { keys: string[] }) {
  const { t } = useI18n();
  return (
    <div className="notice notice-warn" role="status">
      <p className="small">{t("access.nearKeys")}</p>
      <ul className="plain stack-tight">{keys.map((k) => <li key={k}><code><VisibleText text={k} /></code></li>)}</ul>
    </div>
  );
}

/** The policy, read-only: the repositories it covers, the project folder, shell, and per OS its folders and applications. */
function PolicyFacts({ view }: { view: AccessPolicyView }) {
  const { t } = useI18n();
  const d = view.document;
  return (
    <div className="stack">
      <dl className="facts">
        <dt>{t("access.keys")}</dt>
        <dd>{d.projectKeys.length === 0 ? <span className="muted">{t("access.noKeys")}</span>
          : <ul className="plain stack-tight">{d.projectKeys.map((k) => <li key={k}><code>{k}</code></li>)}</ul>}</dd>
        <dt>{t("access.projectFolderShort")}</dt>
        <dd>{d.includeProjectFolder ? t("access.allowed") : t("access.notAllowed")}</dd>
        <dt>{t("access.shellShort")}</dt>
        <dd>{d.shell ? <span className="badge status-failed">{t("access.shellOn")}</span> : t("access.shellOff")}</dd>
      </dl>
      {OS_KINDS.map((os) => {
        const rules = sectionOf(d, os);
        return (
          <section key={os} className="stack-tight" aria-label={t(`access.os.${os}`)}>
            <h3 className="subhead">{t(`access.os.${os}`)}</h3>
            <AccessLists allowed={rules.allowedFolders} denied={rules.deniedFolders} applications={rules.applications} projectFolder={d.includeProjectFolder} />
          </section>
        );
      })}
    </div>
  );
}

/**
 * The project's access policy (ADR-0020 §4) on the project page: what remote runs in its repositories may reach on each machine. Every member reads
 * it; the workspace's owners and admins edit it (the API says who may). An API without access policies answers 404 and the card is not shown.
 */
export function ProjectAccessCard({ ws, projectId }: { ws: string; projectId: string }) {
  const { t } = useI18n();
  const errorText = useErrorText();
  const [view, setView] = useState<AccessPolicyView | null>(null);
  const [unavailable, setUnavailable] = useState(false);
  const [editing, setEditing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<{ kind: "info" | "success"; text: string } | null>(null);

  const load = useCallback(async () => {
    try {
      setView(await api.accessPolicy(projectId));
      setError(null);
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) setUnavailable(true);
      else setError(errorText(e));
    }
    // errorText is a fresh function each render; loading again for it would loop
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId]);

  useEffect(() => {
    void load();
  }, [load]);

  if (unavailable) return null;
  const done = (kind: "info" | "success", text: string) => {
    setEditing(false);
    setNotice({ kind, text });
    void load();
  };

  return (
    <Card title={t("access.title")}
      actions={view?.canEdit && !editing ? <Button variant="ghost" onClick={() => { setNotice(null); setEditing(true); }}>{t("access.edit")}</Button> : undefined}>
      <div className="stack">
        <p className="muted small">{t("access.intro")}</p>
        {error ? <Notice kind="error">{error}</Notice> : null}
        {notice ? <Notice kind={notice.kind}>{notice.text}</Notice> : null}
        {view === null ? (error ? null : <Spinner />) : (
          <>
            <PolicyState view={view} />
            {view.nearKeys && view.nearKeys.length > 0 ? <NearKeys keys={view.nearKeys} /> : null}
            {view.isDefault || view.document.cleared ? <p className="muted small">{t("access.defaultHint")}</p> : null}
            {editing ? (
              <AccessPolicyForm key={view.version} view={view} ws={ws} onCancel={() => setEditing(false)}
                onSaved={(signed) => done("success", signed ? t("access.savedSigned") : t("access.saved"))} onConflict={() => done("info", t("access.reloaded"))} />
            ) : view.isDefault || view.document.cleared ? null : <PolicyFacts view={view} />}
          </>
        )}
      </div>
    </Card>
  );
}
