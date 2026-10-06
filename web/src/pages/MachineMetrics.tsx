import type { MetricSample } from "../api/types";
import { useI18n } from "../i18n";
import { bytes, percent, share, time } from "../lib/format";

const WIDTH = 100;
const HEIGHT = 32;

/** A line of percentages over the window, drawn as inline SVG (no chart library): 0 at the bottom, 100 at the top. */
function Spark({ label, values, from, to }: { label: string; values: number[]; from: string; to: string }) {
  const last = values.length > 0 ? values[values.length - 1]! : null;
  const step = values.length > 1 ? WIDTH / (values.length - 1) : 0;
  const points = values.map((v, i) => `${(i * step).toFixed(2)},${(HEIGHT - (Math.min(100, Math.max(0, v)) / 100) * HEIGHT).toFixed(2)}`).join(" ");
  return (
    <figure className="spark">
      <figcaption><strong>{label}</strong> <span className="spark-now">{percent(last)}</span></figcaption>
      <svg viewBox={`0 0 ${WIDTH} ${HEIGHT}`} preserveAspectRatio="none" role="img" aria-label={`${label} ${percent(last)}`}>
        <line x1="0" y1={HEIGHT / 2} x2={WIDTH} y2={HEIGHT / 2} className="spark-grid" />
        {values.length === 1 ? <circle cx={WIDTH / 2} cy={HEIGHT - ((last ?? 0) / 100) * HEIGHT} r="1.5" className="spark-line" />
          : values.length > 1 ? <polyline points={points} className="spark-line" vectorEffect="non-scaling-stroke" /> : null}
      </svg>
      <div className="spark-axis muted small"><span>{from}</span><span>{to}</span></div>
    </figure>
  );
}

/** The window's CPU and memory lines, and each disk's share in the latest sample. */
export function MachineMetrics({ samples }: { samples: MetricSample[] }) {
  const { t } = useI18n();
  if (samples.length === 0) return <p className="muted">{t("remote.noMetrics")}</p>;
  const first = samples[0]!;
  const latest = samples[samples.length - 1]!;
  const from = time(first.sampledAt);
  const to = time(latest.sampledAt);
  return (
    <div className="metrics">
      <div className="metrics-lines">
        <Spark label={t("remote.cpu")} values={samples.map((s) => s.cpuPct)} from={from} to={to} />
        <Spark label={t("remote.memory")} values={samples.map((s) => share(s.memUsedBytes, s.memTotalBytes))} from={from} to={to} />
      </div>
      <h3 className="sub">{t("remote.disks")}</h3>
      {latest.disks.length === 0 ? <p className="muted small">{t("remote.noDisks")}</p> : (
        <ul className="plain disks">
          {latest.disks.map((d) => {
            const pct = share(d.usedBytes, d.totalBytes);
            return (
              <li key={d.mount}>
                <div className="spread"><code>{d.mount}</code><span className="muted small">{bytes(d.usedBytes)} / {bytes(d.totalBytes)} · {percent(pct)}</span></div>
                <div className="bar" role="img" aria-label={`${d.mount} ${percent(pct)}`}><div className="bar-fill" style={{ width: `${Math.min(100, pct)}%` }} /></div>
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}
