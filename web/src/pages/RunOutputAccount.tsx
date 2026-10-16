import type { WebRunView } from "../api/types";
import { useI18n } from "../i18n";
import { bytes } from "../lib/format";

/**
 * The verdict on a script run's output (ADR-0008 §3d): complete with its size, or exactly what is missing: the bytes cut on the target, the
 * bytes from one chunk on the workspace's limit refused, a stop at the read limit. Nothing for a run of another mode (it has no verdict).
 */
export function RunOutputAccount({ run }: { run: Pick<WebRunView, "outputComplete" | "outputReadBytes" | "outputBytes" | "outputDroppedBytes" | "outputRefusedBytes" | "outputMissingFromSeq" | "outputCap" | "outputTruncated" | "error"> }) {
  const { t } = useI18n();
  if (run.outputComplete === null || run.outputComplete === undefined) return null;
  if (run.outputComplete) return <p className="small run-output-account">{t("remote.outputComplete", { size: bytes(run.outputReadBytes ?? run.outputBytes) })}</p>;
  const parts: string[] = [];
  if ((run.outputDroppedBytes ?? 0) > 0) parts.push(t("remote.outputCutTarget", { size: bytes(run.outputDroppedBytes ?? 0) }));
  if ((run.outputRefusedBytes ?? 0) > 0) {
    parts.push(t("remote.outputRefusedCap", { size: bytes(run.outputRefusedBytes ?? 0), seq: run.outputMissingFromSeq ?? 0, cap: bytes(run.outputCap ?? 0) }));
  }
  if (run.error === "output_limit" || parts.length === 0) parts.push(t("remote.outputReadLimit"));
  return <p className="small run-output-account notice notice-warn" role="status">{t("remote.outputIncompleteTitle")} {parts.join("; ")}.</p>;
}
