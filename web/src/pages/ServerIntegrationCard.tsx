import { useState } from "react";
import { api } from "../api/endpoints";
import { ApiError } from "../api/client";
import {
  INTEGRATION_IDS, INTEGRATION_STATUSES, IntegrationStatus, SMTP_ID, type IntegrationSettingsView, type ServerIntegrationsView,
} from "../api/typesServerSettings";
import { Button, Card, Notice } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { changedValues, initialDraft, type Draft } from "../lib/serverSettings";
import { ServerSettingField } from "./ServerSettingField";
import { useCodeGate } from "./useCodeGate";

/** The codes this page words for itself: the app-wide texts of the same codes speak of picking from a list. */
const OWN_CODES = ["invalid_setting", "invalid_value"] as const;
const KNOWN_NOTES = ["integration_key_missing"] as const;

/** The name, status and note texts of an integration; whatever this web does not know is shown as it is. */
function useIntegrationTexts() {
  const { t, tx } = useI18n();
  return {
    name: (id: string) => {
      const known = INTEGRATION_IDS.find((k) => k === id);
      return known ? t(`serverSettings.names.${known}`) : id;
    },
    status: (status: string) => {
      const known = INTEGRATION_STATUSES.find((k) => k === status);
      return known ? t(`serverSettings.status.${known}`) : status;
    },
    note: (note: string) => {
      const known = KNOWN_NOTES.find((k) => k === note);
      return known ? t(`serverSettings.notes.${known}`) : note;
    },
    code: (code: string) => {
      const own = OWN_CODES.find((k) => k === code);
      return own ? t(`serverSettings.errors.${own}`) : tx(`errors.${code}`);
    },
  };
}

function badgeClass(status: string): string {
  switch (status) {
    case IntegrationStatus.ready:
      return "badge ok";
    case IntegrationStatus.incomplete:
      return "badge warn";
    case IntegrationStatus.off:
      return "badge";
    default:
      return "badge"; // a status this web does not know: neutral
  }
}

interface Props {
  integration: IntegrationSettingsView;
  canSave: boolean;
  onSaved: (view: ServerIntegrationsView) => void;
}

/** One integration: its status and notes, the address to register at the provider, its settings, and for e-mail a test mail. */
export function ServerIntegrationCard({ integration: i, canSave, onSaved }: Props) {
  const { t } = useI18n();
  const errorText = useErrorText();
  const texts = useIntegrationTexts();
  const gate = useCodeGate("reauth_required", "remote.codeHintReauth");
  const [draft, setDraft] = useState<Draft>(() => initialDraft(i));
  const [removed, setRemoved] = useState<ReadonlySet<string>>(new Set());
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});
  const [saved, setSaved] = useState(false);
  const [copied, setCopied] = useState<"idle" | "done" | "failed">("idle");
  const [mailBusy, setMailBusy] = useState(false);
  const [mail, setMail] = useState<{ kind: "success" | "error"; text: string } | null>(null);
  const name = texts.name(i.id);
  const changes = changedValues(i, draft, removed);
  const dirty = Object.keys(changes).length > 0;

  const edit = (field: string, value: string) => {
    setSaved(false);
    setDraft({ ...draft, [field]: value });
  };
  const markRemoved = (field: string, remove: boolean) => {
    setSaved(false);
    const next = new Set(removed);
    if (remove) next.add(field);
    else next.delete(field);
    setRemoved(next);
  };

  // "Copied" only after the browser says it copied; without the clipboard (or permission) the person is told, and pastes nothing stale.
  const copyCallback = async () => {
    try {
      if (!navigator.clipboard) throw new Error("no clipboard");
      await navigator.clipboard.writeText(i.callbackUrl ?? "");
      setCopied("done");
    } catch {
      setCopied("failed");
    }
  };

  const save = async () => {
    setSaved(false);
    setFieldErrors({});
    const result: { view?: ServerIntegrationsView } = {};
    const ok = await gate.attempt(async (code) => {
      try {
        result.view = await api.saveServerIntegration(i.id, { values: changes, code });
      } catch (e) {
        if (e instanceof ApiError && e.status === 400) setFieldErrors(e.fields);
        throw e; // the code gate shows it, or asks for the two-step code
      }
    });
    if (!ok || !result.view) return;
    const mine = result.view.integrations.find((x) => x.id === i.id) ?? i;
    setDraft(initialDraft(mine));
    setRemoved(new Set());
    setSaved(true);
    onSaved(result.view);
  };

  const testMail = async () => {
    setMailBusy(true);
    setMail(null);
    try {
      await api.testServerMail();
      setMail({ kind: "success", text: t("serverSettings.testMailSent") });
    } catch (e) {
      setMail({ kind: "error", text: errorText(e) });
    } finally {
      setMailBusy(false);
    }
  };

  // A field error is shown at its field; one the card has no field for, at the top. Without field errors, the failure as it is.
  const unplaced = Object.entries(fieldErrors).filter(([field]) => !i.fields.some((f) => f.name === field));
  const problem = Object.keys(fieldErrors).length > 0 ? unplaced.map(([, code]) => texts.code(code)).join(" ") : gate.error;

  return (
    <Card title={name} actions={<span className={badgeClass(i.status)}>{t("serverSettings.statusLabel", { status: texts.status(i.status) })}</span>}>
      <div className="stack">
        {i.notes.map((note) => <Notice key={note}>{texts.note(note)}</Notice>)}
        {i.callbackUrl ? (
          <div className="field">
            <span className="field-label">{t("serverSettings.callback")}</span>
            <div className="copy">
              <code>{i.callbackUrl}</code>
              <Button variant="subtle" aria-label={t("serverSettings.copyNamed", { name })} onClick={() => void copyCallback()}>
                {copied === "done" ? t("serverSettings.copied") : t("serverSettings.copy")}
              </Button>
            </div>
            {copied === "failed" ? <span className="field-error" role="alert">{t("serverSettings.copyFailed")}</span> : null}
            <span className="field-hint">{t("serverSettings.callbackHint")}</span>
          </div>
        ) : null}
        {i.fields.map((f) => (
          <ServerSettingField key={f.name} field={f} value={draft[f.name] ?? ""} onChange={(v) => edit(f.name, v)}
            removed={removed.has(f.name)} onRemove={(r) => markRemoved(f.name, r)} disabled={!canSave || gate.busy}
            error={fieldErrors[f.name] ? texts.code(fieldErrors[f.name]!) : undefined} />
        ))}
        {gate.field}
        {problem ? <Notice kind="error">{problem}</Notice> : null}
        {saved ? <Notice kind="success">{t("serverSettings.savedDone")}</Notice> : null}
        <div className="row-actions">
          {i.id === SMTP_ID ? (
            <Button variant="ghost" busy={mailBusy} onClick={() => void testMail()}>{t("serverSettings.testMail")}</Button>
          ) : null}
          <Button busy={gate.busy} disabled={!canSave || !dirty || !gate.ready || gate.busy} aria-label={t("serverSettings.saveNamed", { name })}
            onClick={() => void save()}>{t("serverSettings.save")}</Button>
        </div>
        {i.id === SMTP_ID ? <p className="muted small">{t("serverSettings.testMailHint")}</p> : null}
        {mail ? <Notice kind={mail.kind}>{mail.text}</Notice> : null}
      </div>
    </Card>
  );
}
