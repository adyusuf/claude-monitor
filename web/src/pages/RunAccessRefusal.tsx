import { ACCESS_ERROR_PREFIX, ACCESS_ERRORS, ACCESS_REFUSED_BY } from "../api/typesAccess";
import type { WebRunView } from "../api/types";
import { useI18n } from "../i18n";

/** True when the run's error is an access refusal (ADR-0020 §4): the target's access layers refused it before it started. */
export const isAccessRefusal = (run: WebRunView): boolean => (run.error ?? "").startsWith(ACCESS_ERROR_PREFIX);

/**
 * Why the target refused a run under its access layers, in words: what was refused, and which layer refused it (the machine's own ceiling, the
 * project's policy with its version, or the default). A code this page does not know yet is shown as it came.
 */
export function RunAccessRefusal({ run }: { run: WebRunView }) {
  const { t, tx } = useI18n();
  const code = run.error ?? "";
  const known = (ACCESS_ERRORS as readonly string[]).includes(code);
  const by = run.refusedBy && (ACCESS_REFUSED_BY as readonly string[]).includes(run.refusedBy) ? run.refusedBy : null;
  let layer: string | null;
  switch (by) {
    case "policy":
      layer = run.accessVersion ? t("access.refusedBy.policyVersion", { version: run.accessVersion }) : t("access.refusedBy.policy");
      break;
    case "ceiling":
    case "default":
      layer = tx(`access.refusedBy.${by}`);
      break;
    default:
      layer = run.refusedBy ? run.refusedBy : null;
  }
  return (
    <div className="notice notice-error" role="alert">
      <p className="small">{known ? tx(`access.runError.${code}`) : <>{t("remote.runError")}: <code>{code}</code></>}</p>
      {layer ? <p className="small">{layer}</p> : null}
    </div>
  );
}
