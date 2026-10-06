import { useEffect, useState } from "react";
import { useNavigate, useParams } from "react-router";
import { api } from "../api/endpoints";
import type { AuditRow, WorkspaceInfo } from "../api/types";
import { useSession } from "../auth/session";
import { Button, Card, Field, Notice, Spinner } from "../components/ui";
import { config } from "../config";
import { useErrorText, useI18n } from "../i18n";
import { dateTime } from "../lib/format";
import { RemoteSettingsCard } from "./RemoteSettingsCard";

/** Name, capture settings and the audit log (admins). */
export function WorkspaceSettingsPage() {
  const { ws = "" } = useParams();
  const { t } = useI18n();
  const errorText = useErrorText();
  const { refresh } = useSession();
  const [info, setInfo] = useState<WorkspaceInfo | null>(null);
  const [form, setForm] = useState({ name: "", maskSecrets: true, retentionDays: 90, eventMaxBytes: 262144 });
  const [audit, setAudit] = useState<AuditRow[]>([]);
  const [notice, setNotice] = useState<{ kind: "error" | "success"; text: string } | null>(null);

  useEffect(() => {
    api.workspace(ws).then((w) => {
      setInfo(w);
      setForm({ name: w.name, ...w.settings });
    }).catch((e) => setNotice({ kind: "error", text: errorText(e) }));
    api.audit(ws).then((p) => setAudit(p.items)).catch(() => setAudit([]));
    // errorText changes identity every render; the workspace id is what matters
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ws]);

  const save = async () => {
    try {
      if (form.name !== info?.name) await api.renameWorkspace(ws, form.name);
      await api.saveSettings(ws, { maskSecrets: form.maskSecrets, retentionDays: form.retentionDays, eventMaxBytes: form.eventMaxBytes });
      await refresh();
      setNotice({ kind: "success", text: t("workspace.saved") });
    } catch (e) {
      setNotice({ kind: "error", text: errorText(e) });
    }
  };

  if (!info) return notice ? <div className="page"><Notice kind="error">{notice.text}</Notice></div> : <Spinner />;
  return (
    <div className="page">
      <header className="page-head"><h1>{t("workspace.settingsTitle")}</h1></header>
      {notice ? <Notice kind={notice.kind}>{notice.text}</Notice> : null}
      <Card>
        <div className="stack">
          <Field label={t("workspace.name")} value={form.name} maxLength={config.nameMax} onChange={(e) => setForm({ ...form, name: e.target.value })} />
          <label className="check">
            <input type="checkbox" checked={form.maskSecrets} onChange={(e) => setForm({ ...form, maskSecrets: e.target.checked })} />
            <span>{t("workspace.maskSecrets")}<span className="field-hint">{t("workspace.maskHint")}</span></span>
          </label>
          <Field label={t("workspace.retention")} type="number" min={1} max={3650} value={form.retentionDays} hint={t("workspace.retentionHint")}
            onChange={(e) => setForm({ ...form, retentionDays: Number(e.target.value) })} />
          <Field label={t("workspace.eventMax")} type="number" min={1024} max={4194304} value={form.eventMaxBytes}
            onChange={(e) => setForm({ ...form, eventMaxBytes: Number(e.target.value) })} />
          <div><Button onClick={() => void save()}>{t("workspace.save")}</Button></div>
        </div>
      </Card>
      <RemoteSettingsCard ws={ws} canEdit={info.role === "owner" || info.role === "admin"} />
      <Card title={t("workspace.audit")}>
        {audit.length === 0 ? <p className="muted">{t("workspace.noAudit")}</p> : (
          <ul className="plain audit">
            {audit.map((a) => <li key={a.id}><span className="muted small">{dateTime(a.at)}</span> <code>{a.action}</code></li>)}
          </ul>
        )}
      </Card>
    </div>
  );
}

/** /workspaces/new */
export function NewWorkspacePage() {
  const { t } = useI18n();
  const errorText = useErrorText();
  const { refresh } = useSession();
  const navigate = useNavigate();
  const [name, setName] = useState("");
  const [error, setError] = useState<string | null>(null);
  return (
    <div className="page narrow">
      <h1>{t("workspace.createTitle")}</h1>
      {error ? <Notice kind="error">{error}</Notice> : null}
      <Card>
        <form className="stack" onSubmit={async (e) => {
          e.preventDefault();
          if (!name.trim()) return setError(t("errors.invalid_name"));
          try {
            const created = await api.createWorkspace(name);
            await refresh();
            navigate(`/w/${created.id}/sessions`);
          } catch (err) {
            setError(errorText(err));
          }
        }}>
          <Field label={t("workspace.name")} value={name} maxLength={config.nameMax} onChange={(e) => setName(e.target.value)} />
          <Button type="submit">{t("workspace.create")}</Button>
        </form>
      </Card>
    </div>
  );
}
