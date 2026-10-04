import { useState, type FormEvent } from "react";
import { api, exportUrl } from "../api/endpoints";
import { useSession } from "../auth/session";
import { Button, Card, Field, Notice } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";

/** The word the API wants before it deletes an account (PrivacyEndpoints.ConfirmWord). */
export const CONFIRM_WORD = "DELETE";

/** The user's own data: download it all, or delete the account (GDPR/KVKK). */
export function AccountDataCards() {
  const { t } = useI18n();
  const errorText = useErrorText();
  const { me } = useSession();
  const [password, setPassword] = useState("");
  const [confirm, setConfirm] = useState("");
  const [code, setCode] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [deleted, setDeleted] = useState(false);

  const remove = async (e: FormEvent) => {
    e.preventDefault();
    if (confirm !== CONFIRM_WORD) return setError(t("errors.confirm_required"));
    setBusy(true);
    setError(null);
    try {
      await api.deleteAccount(me?.hasPassword ? password : undefined, confirm, me?.mfaEnabled ? code : undefined);
      setDeleted(true);
    } catch (err) {
      setError(errorText(err));
    } finally {
      setBusy(false);
    }
  };

  if (deleted) {
    return (
      <Card>
        <Notice kind="success">{t("account.deleted")}</Notice>
        <p><a href="/login">{t("auth.backToSignIn")}</a></p>
      </Card>
    );
  }

  return (
    <>
      <Card title={t("account.data")}>
        <p className="muted">{t("account.exportHint")}</p>
        <a className="btn btn-ghost" href={exportUrl} download>{t("account.export")}</a>
      </Card>
      <Card title={t("account.deleteTitle")} className="card-danger">
        <p className="muted">{t("account.deleteHint")}</p>
        {error ? <Notice kind="error">{error}</Notice> : null}
        <form className="stack" onSubmit={remove}>
          {me?.hasPassword ? (
            <Field label={t("auth.password")} type="password" autoComplete="current-password" value={password}
              onChange={(e) => setPassword(e.target.value)} />
          ) : null}
          {me?.mfaEnabled ? (
            <Field label={t("auth.mfaCode")} value={code} autoComplete="one-time-code" onChange={(e) => setCode(e.target.value)} />
          ) : null}
          <Field label={t("account.deleteConfirm", { word: CONFIRM_WORD })} value={confirm} autoComplete="off"
            onChange={(e) => setConfirm(e.target.value)} />
          <div><Button type="submit" variant="danger" busy={busy}>{t("account.deleteButton")}</Button></div>
        </form>
      </Card>
    </>
  );
}
