import { useErrorText, useI18n } from "../i18n";
import { RUN_KINDS } from "../lib/issueRuns";

/** The text of an error of a run link: a repeat says it is the run that is already there, the rest are the usual error texts. */
export function useRunErrorText() {
  const { t } = useI18n();
  const errorText = useErrorText();
  return (error: unknown) => (error as { code?: string } | null)?.code === "duplicate_link" ? t("issues.runDuplicate") : errorText(error);
}

/** The name of a run's kind (test, build, deploy, other); a kind this version does not know is shown as it came; none is a dash. */
export function useRunKindText() {
  const { tx } = useI18n();
  return (kind: string | null): string => !kind ? "–" : RUN_KINDS.includes(kind) ? tx(`issues.runKinds.${kind}`) : kind;
}

/** "40 passed, 2 failed, 1 skipped"; nothing at all when the run's tool was not recognised (no counts). */
export function RunTests({ passed, failed, skipped }: { passed: number | null; failed: number | null; skipped: number | null }) {
  const { t } = useI18n();
  if (passed === null && failed === null && skipped === null) return null;
  return <span className={(failed ?? 0) > 0 ? "badge bad" : "badge ok"}>{t("issues.runTests", { passed: passed ?? 0, failed: failed ?? 0, skipped: skipped ?? 0 })}</span>;
}
