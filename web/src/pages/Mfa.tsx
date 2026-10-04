import { useState, type FormEvent } from "react";
import { useNavigate, useSearchParams } from "react-router";
import { api } from "../api/endpoints";
import { safeNext, useSession } from "../auth/session";
import { AuthFrame } from "../components/Layout";
import { Button, Card, CopyText, Field, Notice } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";

/** The second step of a sign-in: a code for the pending token the first step left. */
export function MfaStep({ token, next }: { token: string; next: string }) {
  const { t } = useI18n();
  const errorText = useErrorText();
  const { refresh } = useSession();
  const navigate = useNavigate();
  const [code, setCode] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await api.mfaSignIn(token, code);
      await refresh();
      navigate(safeNext(next), { replace: true });
    } catch (err) {
      setError(errorText(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <form className="stack" onSubmit={submit}>
      {error ? <Notice kind="error">{error}</Notice> : null}
      <Field label={t("auth.mfaCode")} value={code} inputMode="numeric" autoComplete="one-time-code" autoFocus
        onChange={(e) => setCode(e.target.value)} />
      <Button type="submit" busy={busy}>{t("auth.mfaVerify")}</Button>
    </form>
  );
}

/** /mfa?token=: the second step after a GitHub or Google sign-in. */
export function MfaPage() {
  const { t } = useI18n();
  const [params] = useSearchParams();
  return <AuthFrame title={t("auth.mfaTitle")}><MfaStep token={params.get("token") ?? ""} next="/" /></AuthFrame>;
}

/** Account page: turn two-step sign-in on (key, first code, recovery codes) or off (a code). */
export function MfaCard() {
  const { t } = useI18n();
  const errorText = useErrorText();
  const { me, refresh } = useSession();
  const [setup, setSetup] = useState<{ secret: string; uri: string } | null>(null);
  const [recovery, setRecovery] = useState<string[] | null>(null);
  const [code, setCode] = useState("");
  const [notice, setNotice] = useState<{ kind: "error" | "success"; text: string } | null>(null);

  const run = async (work: () => Promise<void>) => {
    setNotice(null);
    try {
      await work();
    } catch (err) {
      setNotice({ kind: "error", text: errorText(err) });
    }
  };

  return (
    <Card title={t("account.mfa")}>
      {notice ? <Notice kind={notice.kind}>{notice.text}</Notice> : null}
      {recovery ? (
        <div className="stack">
          <Notice kind="success">{t("account.mfaOn")}</Notice>
          <p className="muted">{t("account.mfaRecovery")}</p>
          <ul className="plain recovery">{recovery.map((r) => <li key={r}><code>{r}</code></li>)}</ul>
        </div>
      ) : me?.mfaEnabled ? (
        <form className="stack" onSubmit={(e) => {
          e.preventDefault();
          void run(async () => {
            await api.mfaDisable(code);
            setCode("");
            await refresh();
            setNotice({ kind: "success", text: t("account.mfaDisabled") });
          });
        }}>
          <p className="muted">{t("account.mfaOn")}</p>
          <Field label={t("auth.mfaCode")} value={code} autoComplete="one-time-code" onChange={(e) => setCode(e.target.value)} />
          <div><Button type="submit" variant="ghost">{t("account.mfaDisable")}</Button></div>
        </form>
      ) : setup ? (
        <form className="stack" onSubmit={(e) => {
          e.preventDefault();
          void run(async () => {
            const result = await api.mfaEnable(code);
            setRecovery(result.recoveryCodes);
            setCode("");
            await refresh();
          });
        }}>
          <p className="muted">{t("account.mfaScan")}</p>
          <CopyText text={setup.secret} />
          <a href={setup.uri}>{t("account.mfaOpen")}</a>
          <Field label={t("auth.mfaCode")} value={code} inputMode="numeric" autoComplete="one-time-code" onChange={(e) => setCode(e.target.value)} />
          <div><Button type="submit">{t("account.mfaEnable")}</Button></div>
        </form>
      ) : (
        <div className="stack">
          <p className="muted">{t("account.mfaOff")}</p>
          <div><Button variant="ghost" onClick={() => void run(async () => setSetup(await api.mfaSetup()))}>{t("account.mfaSetup")}</Button></div>
        </div>
      )}
    </Card>
  );
}
