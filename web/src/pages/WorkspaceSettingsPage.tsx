import { useEffect, useState } from "react";
import { useNavigate, useParams } from "react-router";
import { api } from "../api/endpoints";
import type { AgentUpdateMode, AuditRow, WorkspaceInfo } from "../api/types";
import { useSession } from "../auth/session";
import { Button, Card, Field, Notice, Spinner } from "../components/ui";
import { SettingsSection } from "../components/SettingsSection";
import { Tabs } from "../components/Tabs";
import { config } from "../config";
import { useErrorText, useI18n } from "../i18n";
import { AccessEnforcementCard } from "./AccessEnforcementCard";
import { RemoteSettingsCard } from "./RemoteSettingsCard";
import { WorkspaceAudit } from "./WorkspaceAudit";

/** Name, capture, updates, remote control, access and the audit log, one tab each (admins). */
export function WorkspaceSettingsPage() {
  const { ws = "" } = useParams();
  const { t } = useI18n();
  const errorText = useErrorText();
  const { refresh } = useSession();
  const [info, setInfo] = useState<WorkspaceInfo | null>(null);
  const [form, setForm] = useState<{ name: string; maskSecrets: boolean; retentionDays: number; eventMaxBytes: number; agentUpdate: AgentUpdateMode; claudeUpdate: boolean }>(
    { name: "", maskSecrets: true, retentionDays: 90, eventMaxBytes: 262144, agentUpdate: "off", claudeUpdate: false });
  const [audit, setAudit] = useState<AuditRow[]>([]);
  const [notice, setNotice] = useState<{ kind: "error" | "success"; text: string } | null>(null);

  useEffect(() => {
    api.workspace(ws).then((w) => {
      setInfo(w);
      setForm({ name: w.name, ...w.settings, agentUpdate: w.settings.agentUpdate ?? "off", claudeUpdate: w.settings.claudeUpdate ?? false });
    }).catch((e) => setNotice({ kind: "error", text: errorText(e) }));
    api.audit(ws).then((p) => setAudit(p.items)).catch(() => setAudit([]));
    // errorText changes identity every render; the workspace id is what matters
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ws]);

  const save = async () => {
    try {
      if (form.name !== info?.name) await api.renameWorkspace(ws, form.name);
      await api.saveSettings(ws, { maskSecrets: form.maskSecrets, retentionDays: form.retentionDays, eventMaxBytes: form.eventMaxBytes, agentUpdate: form.agentUpdate, claudeUpdate: form.claudeUpdate });
      await refresh();
      setNotice({ kind: "success", text: t("workspace.saved") });
    } catch (e) {
      setNotice({ kind: "error", text: errorText(e) });
    }
  };

  if (!info) return notice ? <div className="page"><Notice kind="error">{notice.text}</Notice></div> : <Spinner />;
  const canEdit = info.role === "owner" || info.role === "admin";
  // One form holds name, capture and updates, so each of those tabs ends with the same Save.
  const saveButton = <div><Button onClick={() => void save()}>{t("workspace.save")}</Button></div>;
  return (
    <div className="page">
      <header className="page-head"><h1>{t("workspace.settingsTitle")}</h1></header>
      {notice ? <Notice kind={notice.kind}>{notice.text}</Notice> : null}
      <Tabs label={t("views.workspace.tabs")} tabs={[
        {
          id: "general", label: t("workspace.sectionGeneral"),
          content: (
            <SettingsSection title={t("workspace.sectionGeneral")} hint={t("workspace.sectionGeneralHint")}>
              <Card>
                <Field label={t("workspace.name")} value={form.name} maxLength={config.nameMax} onChange={(e) => setForm({ ...form, name: e.target.value })} />
              </Card>
              {saveButton}
            </SettingsSection>
          ),
        },
        {
          id: "capture", label: t("workspace.sectionCapture"),
          content: (
            <SettingsSection title={t("workspace.sectionCapture")} hint={t("workspace.sectionCaptureHint")}>
              <Card>
                <div className="stack">
                  <label className="check">
                    <input type="checkbox" checked={form.maskSecrets} onChange={(e) => setForm({ ...form, maskSecrets: e.target.checked })} />
                    <span>{t("workspace.maskSecrets")}<span className="field-hint">{t("workspace.maskHint")}</span></span>
                  </label>
                  <Field label={t("workspace.retention")} type="number" min={1} max={3650} value={form.retentionDays} hint={t("workspace.retentionHint")}
                    onChange={(e) => setForm({ ...form, retentionDays: Number(e.target.value) })} />
                  <Field label={t("workspace.eventMax")} type="number" min={1024} max={4194304} value={form.eventMaxBytes}
                    onChange={(e) => setForm({ ...form, eventMaxBytes: Number(e.target.value) })} />
                </div>
              </Card>
              {saveButton}
            </SettingsSection>
          ),
        },
        {
          id: "updates", label: t("workspace.sectionUpdates"),
          content: (
            <SettingsSection title={t("workspace.sectionUpdates")} hint={t("workspace.sectionUpdatesHint")}>
              <Card>
                <div className="stack">
                  <div className="field">
                    <label className="field-label" htmlFor="agent-update">{t("workspace.agentUpdate")}</label>
                    <select id="agent-update" className="input" value={form.agentUpdate} aria-describedby="agent-update-note"
                      onChange={(e) => setForm({ ...form, agentUpdate: e.target.value as AgentUpdateMode })}>
                      <option value="off">{t("workspace.agentUpdateOff")}</option>
                      <option value="check">{t("workspace.agentUpdateCheck")}</option>
                      <option value="on">{t("workspace.agentUpdateOn")}</option>
                    </select>
                    <span id="agent-update-note" className="field-hint">{t("workspace.agentUpdateHint")}</span>
                  </div>
                  <label className="check">
                    <input type="checkbox" checked={form.claudeUpdate} onChange={(e) => setForm({ ...form, claudeUpdate: e.target.checked })} />
                    <span>{t("workspace.claudeUpdate")}<span className="field-hint">{t("workspace.claudeUpdateHint")}</span></span>
                  </label>
                </div>
              </Card>
              {saveButton}
            </SettingsSection>
          ),
        },
        {
          id: "remote", label: t("remote.settingsTitle"),
          content: (
            <SettingsSection title={t("remote.settingsTitle")} hint={t("remote.settingsHint")}>
              <RemoteSettingsCard ws={ws} canEdit={canEdit} titled={false} />
            </SettingsSection>
          ),
        },
        {
          id: "access", label: t("access.enforceTitle"), hidden: info.settings.enforceAccessPolicies === undefined,
          content: (
            <SettingsSection title={t("access.enforceTitle")} hint={t("access.enforceSection")}>
              <AccessEnforcementCard ws={ws} enforce={info.settings.enforceAccessPolicies ?? false} canEdit={canEdit}
                onChanged={() => void api.workspace(ws).then(setInfo).catch((e: unknown) => setNotice({ kind: "error", text: errorText(e) }))} />
            </SettingsSection>
          ),
        },
        {
          id: "audit", label: t("workspace.audit"),
          content: (
            <SettingsSection title={t("workspace.audit")} hint={t("workspace.auditHint")}>
              <Card><WorkspaceAudit rows={audit} /></Card>
            </SettingsSection>
          ),
        },
      ]} />
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
            navigate(`/w/${created.id}/projects`);
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
