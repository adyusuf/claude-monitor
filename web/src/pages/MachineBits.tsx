import type { ExecLevel, MetricSample } from "../api/types";
import { useI18n, type Key } from "../i18n";
import { share } from "../lib/format";

function execKey(level: string): Key {
  switch (level) {
    case "off":
      return "remote.exec.off";
    case "argv":
      return "remote.exec.argv";
    case "shell":
      return "remote.exec.shell";
    default:
      return "remote.exec.unknown";
  }
}

/** What the machine lets remote runs do, as its own owner set it on the machine ("off" when unknown). */
export function ExecBadge({ level }: { level: ExecLevel | string }) {
  const { t } = useI18n();
  return <span className={`badge exec-${level === "argv" || level === "shell" ? level : "off"}`}>{t(execKey(level))}</span>;
}

export function ServiceBadge({ serviceMode }: { serviceMode: boolean }) {
  const { t } = useI18n();
  return serviceMode ? <span className="badge">{t("remote.service")}</span> : null;
}

export function OnlineDot({ online }: { online: boolean }) {
  const { t } = useI18n();
  const label = online ? t("remote.online") : t("remote.offline");
  return <span className={online ? "online-dot online-on" : "online-dot"} role="img" aria-label={label} title={label} />;
}

export const memoryPct = (m: MetricSample | null): number | null => (m ? share(m.memUsedBytes, m.memTotalBytes) : null);

/** The fullest disk's share, 0-100; null when nothing was measured. */
export const maxDiskPct = (m: MetricSample | null): number | null =>
  m && m.disks.length > 0 ? Math.max(...m.disks.map((d) => share(d.usedBytes, d.totalBytes))) : null;
