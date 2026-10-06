import { useEffect, useState } from "react";
import { ApiError } from "../api/client";
import { api } from "../api/endpoints";
import type { RemoteSettings } from "../api/types";
import { Button, Card, Field, Notice } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";

const PCT_MIN = 1;
const PCT_MAX = 100;
const SUSTAIN_MIN = 60;
const SUSTAIN_MAX = 86_400;

const inRange = (n: number, min: number, max: number) => Number.isInteger(n) && n >= min && n <= max;

/** The workspace's remote-work switch and alert thresholds (ADR-0005). Any member reads; only an admin changes them. */
export function RemoteSettingsCard({ ws, canEdit }: { ws: string; canEdit: boolean }) {
  const { t } = useI18n();
  const errorText = useErrorText();
  const [form, setForm] = useState<RemoteSettings | null>(null);
  const [saved, setSaved] = useState<RemoteSettings | null>(null);
  const [unavailable, setUnavailable] = useState(false);
  const [notice, setNotice] = useState<{ kind: "error" | "success"; text: string } | null>(null);

  useEffect(() => {
    api.remoteSettings(ws).then((s) => {
      setForm(s);
      setSaved(s);
    }).catch((e: unknown) => {
      // An API without remote work answers 404: the section is simply not there.
      if (e instanceof ApiError && e.status === 404) setUnavailable(true);
      else setNotice({ kind: "error", text: errorText(e) });
    });
    // errorText changes identity every render; the workspace id is what matters
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ws]);

  if (unavailable) return null;
  if (!form) return notice ? <Card title={t("remote.settingsTitle")}><Notice kind="error">{notice.text}</Notice></Card> : null;

  const set = (patch: Partial<RemoteSettings>) => setForm({ ...form, ...patch });
  const num = (key: "alertCpuPct" | "alertMemoryPct" | "alertDiskPct", label: string) => (
    <Field label={label} type="number" min={PCT_MIN} max={PCT_MAX} value={form[key]} disabled={!canEdit}
      onChange={(e) => set({ [key]: Number(e.target.value) })} />
  );

  const save = async () => {
    const pctOk = [form.alertCpuPct, form.alertMemoryPct, form.alertDiskPct].every((n) => inRange(n, PCT_MIN, PCT_MAX));
    if (!pctOk || !inRange(form.alertSustainSeconds, SUSTAIN_MIN, SUSTAIN_MAX)) return setNotice({ kind: "error", text: t("errors.out_of_range") });
    if (saved?.remoteRunsEnabled && !form.remoteRunsEnabled && !window.confirm(t("remote.disableConfirm"))) return;
    try {
      const stored = await api.saveRemoteSettings(ws, form);
      setForm(stored);
      setSaved(stored);
      setNotice({ kind: "success", text: t("workspace.saved") });
    } catch (e) {
      setNotice({ kind: "error", text: errorText(e) });
    }
  };

  return (
    <Card title={t("remote.settingsTitle")}>
      <div className="stack">
        {notice ? <Notice kind={notice.kind}>{notice.text}</Notice> : null}
        {canEdit ? null : <p className="muted small">{t("remote.settingsReadOnly")}</p>}
        <label className="check">
          <input type="checkbox" role="switch" checked={form.remoteRunsEnabled} disabled={!canEdit}
            onChange={(e) => set({ remoteRunsEnabled: e.target.checked })} />
          <span>{t("remote.enabled")}<span className="field-hint">{t("remote.enabledHint")}</span></span>
        </label>
        <div className="settings-numbers">
          {num("alertCpuPct", t("remote.alertCpu"))}
          {num("alertMemoryPct", t("remote.alertMemory"))}
          {num("alertDiskPct", t("remote.alertDisk"))}
          <Field label={t("remote.alertSustain")} type="number" min={SUSTAIN_MIN} max={SUSTAIN_MAX} value={form.alertSustainSeconds} disabled={!canEdit}
            hint={t("remote.alertSustainHint")} onChange={(e) => set({ alertSustainSeconds: Number(e.target.value) })} />
        </div>
        {canEdit ? <div><Button onClick={() => void save()}>{t("workspace.save")}</Button></div> : null}
      </div>
    </Card>
  );
}
