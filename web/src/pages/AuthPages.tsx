import { useEffect, useRef, useState, type FormEvent } from "react";
import { Link, useNavigate, useSearchParams } from "react-router";
import { ApiError } from "../api/client";
import { api, providerUrl } from "../api/endpoints";
import { safeNext, useSession } from "../auth/session";
import { AuthFrame } from "../components/Layout";
import { Button, Field, Notice } from "../components/ui";
import { config } from "../config";
import { useErrorText, useI18n } from "../i18n";

const emailShape = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

function Providers() {
  const { t } = useI18n();
  const [available, setAvailable] = useState<string[]>([]);
  useEffect(() => {
    api.providers().then((p) => setAvailable(p.available)).catch(() => setAvailable([]));
  }, []);
  if (available.length === 0) return null;
  return (
    <div className="providers">
      <div className="divider"><span>{t("auth.or")}</span></div>
      {available.includes("github") ? <a className="btn btn-ghost wide" href={providerUrl("github", "signin")}>{t("auth.withGithub")}</a> : null}
      {available.includes("google") ? <a className="btn btn-ghost wide" href={providerUrl("google", "signin")}>{t("auth.withGoogle")}</a> : null}
    </div>
  );
}

export function LoginPage() {
  const { t, tx } = useI18n();
  const errorText = useErrorText();
  const { refresh } = useSession();
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(params.get("error") ? tx(`errors.${params.get("error")}`) : null);
  const [busy, setBusy] = useState(false);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await api.login(email, password);
      await refresh();
      navigate(safeNext(params.get("next")), { replace: true });
    } catch (err) {
      setError(errorText(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <AuthFrame title={t("auth.signIn")}>
      {error ? <Notice kind="error">{error}</Notice> : null}
      <form onSubmit={submit} className="stack">
        <Field label={t("auth.email")} type="email" autoComplete="email" required value={email} onChange={(e) => setEmail(e.target.value)} />
        <Field label={t("auth.password")} type="password" autoComplete="current-password" required value={password} onChange={(e) => setPassword(e.target.value)} />
        <Button type="submit" busy={busy}>{t("auth.signIn")}</Button>
      </form>
      <Providers />
      <p className="auth-links"><Link to="/forgot">{t("auth.forgot")}</Link></p>
      <p className="auth-links">{t("auth.noAccount")} <Link to="/register">{t("auth.signUp")}</Link></p>
    </AuthFrame>
  );
}

export function RegisterPage() {
  const { t } = useI18n();
  const errorText = useErrorText();
  const [form, setForm] = useState({ email: "", password: "", name: "" });
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [done, setDone] = useState(false);
  const [busy, setBusy] = useState(false);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    const local: Record<string, string> = {};
    if (!emailShape.test(form.email)) local.email = t("errors.invalid_email");
    if (form.password.length < config.passwordMin) local.password = t("errors.weak_password");
    if (!form.name.trim() || form.name.length > config.nameMax) local.displayName = t("errors.invalid_name");
    setErrors(local);
    if (Object.keys(local).length > 0) return;
    setBusy(true);
    try {
      await api.register(form.email, form.password, form.name);
      setDone(true);
    } catch (err) {
      setErrors(err instanceof ApiError && Object.keys(err.fields).length > 0
        ? Object.fromEntries(Object.entries(err.fields).map(([k, v]) => [k, t(`errors.${v}` as never)]))
        : { form: errorText(err) });
    } finally {
      setBusy(false);
    }
  };

  if (done) {
    return <AuthFrame title={t("auth.signUp")}><Notice kind="success">{t("auth.checkMail")}</Notice><p className="auth-links"><Link to="/login">{t("auth.backToSignIn")}</Link></p></AuthFrame>;
  }
  return (
    <AuthFrame title={t("auth.signUp")}>
      {errors.form ? <Notice kind="error">{errors.form}</Notice> : null}
      <form onSubmit={submit} className="stack" noValidate>
        <Field label={t("auth.name")} autoComplete="name" value={form.name} error={errors.displayName} onChange={(e) => setForm({ ...form, name: e.target.value })} />
        <Field label={t("auth.email")} type="email" autoComplete="email" value={form.email} error={errors.email} onChange={(e) => setForm({ ...form, email: e.target.value })} />
        <Field label={t("auth.password")} type="password" autoComplete="new-password" value={form.password} error={errors.password}
          hint={t("auth.passwordHint", { n: config.passwordMin })} onChange={(e) => setForm({ ...form, password: e.target.value })} />
        <Button type="submit" busy={busy}>{t("auth.signUp")}</Button>
      </form>
      <Providers />
      <p className="auth-links">{t("auth.haveAccount")} <Link to="/login">{t("auth.signIn")}</Link></p>
    </AuthFrame>
  );
}

/** Pages that act once on a ?token= link (verify, accept). */
export function useTokenOnce(action: (token: string) => Promise<unknown>) {
  const [params] = useSearchParams();
  const [state, setState] = useState<"working" | "done" | "failed">("working");
  const [error, setError] = useState<unknown>(null);
  const once = useRef(false);
  useEffect(() => {
    if (once.current) return;
    once.current = true;
    action(params.get("token") ?? "").then(() => setState("done")).catch((e) => {
      setError(e);
      setState("failed");
    });
  }, [action, params]);
  return { state, error };
}

const verify = (token: string) => api.verifyEmail(token);

export function VerifyEmailPage() {
  const { t } = useI18n();
  const errorText = useErrorText();
  const { state, error } = useTokenOnce(verify);
  return (
    <AuthFrame title={t("auth.signIn")}>
      {state === "working" ? <Notice>{t("auth.verifying")}</Notice> : state === "done"
        ? <Notice kind="success">{t("auth.verified")}</Notice> : <Notice kind="error">{errorText(error)}</Notice>}
      <p className="auth-links"><Link to="/login">{t("auth.backToSignIn")}</Link></p>
    </AuthFrame>
  );
}

export function ForgotPage() {
  const { t } = useI18n();
  const [email, setEmail] = useState("");
  const [sent, setSent] = useState(false);
  const [busy, setBusy] = useState(false);
  return (
    <AuthFrame title={t("auth.forgotTitle")}>
      {sent ? <Notice kind="success">{t("auth.forgotSent")}</Notice> : (
        <form className="stack" onSubmit={async (e) => {
          e.preventDefault();
          setBusy(true);
          await api.forgot(email).catch(() => undefined);
          setBusy(false);
          setSent(true);
        }}>
          <Field label={t("auth.email")} type="email" required value={email} onChange={(e) => setEmail(e.target.value)} />
          <Button type="submit" busy={busy}>{t("auth.sendLink")}</Button>
        </form>
      )}
      <p className="auth-links"><Link to="/login">{t("auth.backToSignIn")}</Link></p>
    </AuthFrame>
  );
}

export function ResetPasswordPage() {
  const { t } = useI18n();
  const errorText = useErrorText();
  const [params] = useSearchParams();
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState(false);
  return (
    <AuthFrame title={t("auth.resetTitle")}>
      {done ? <Notice kind="success">{t("auth.resetDone")}</Notice> : (
        <form className="stack" onSubmit={async (e) => {
          e.preventDefault();
          if (password.length < config.passwordMin) return setError(t("errors.weak_password"));
          try {
            await api.reset(params.get("token") ?? "", password);
            setDone(true);
          } catch (err) {
            setError(errorText(err));
          }
        }}>
          {error ? <Notice kind="error">{error}</Notice> : null}
          <Field label={t("auth.newPassword")} type="password" autoComplete="new-password" value={password}
            hint={t("auth.passwordHint", { n: config.passwordMin })} onChange={(e) => setPassword(e.target.value)} />
          <Button type="submit">{t("common.save")}</Button>
        </form>
      )}
      <p className="auth-links"><Link to="/login">{t("auth.backToSignIn")}</Link></p>
    </AuthFrame>
  );
}
