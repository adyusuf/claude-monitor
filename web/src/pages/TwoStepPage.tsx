import { useSession } from "../auth/session";
import { Card, Notice } from "../components/ui";
import { useI18n } from "../i18n";
import { MfaCard } from "./Mfa";

const STEPS = ["twoStep.step1", "twoStep.step2", "twoStep.step3", "twoStep.step4", "twoStep.step5"] as const;

/** /two-step: what the 6-digit code is and how to get an authenticator app giving it, with the setup itself (QR code, first code, recovery codes) below. */
export function TwoStepPage() {
  const { t } = useI18n();
  const { me } = useSession();
  return (
    <div className="page">
      <header className="page-head"><h1>{t("twoStep.title")}</h1></header>
      <Notice kind={me?.mfaEnabled ? "success" : undefined}>{me?.mfaEnabled ? t("twoStep.on") : t("twoStep.off")}</Notice>
      <Card title={t("twoStep.stepsTitle")}>
        <p>{t("twoStep.intro")}</p>
        <ol className="stack">{STEPS.map((key) => <li key={key}>{t(key)}</li>)}</ol>
        <p className="muted">{t("twoStep.after")}</p>
      </Card>
      <MfaCard guide={false} />
    </div>
  );
}
