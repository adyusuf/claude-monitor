import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "../api/endpoints";
import type { RunOutputChunk } from "../api/types";
import { Button, Notice } from "../components/ui";
import { VisibleText } from "../components/VisibleText";
import { config } from "../config";
import { useErrorText, useI18n } from "../i18n";
import { hasInvisible } from "../lib/visible";

/** One piece of output as plain text; one with control or format characters (an escape, a direction override) shows them as escapes. */
function Chunk({ chunk }: { chunk: RunOutputChunk }) {
  const { t } = useI18n();
  return (
    <>
      {chunk.gapBefore ? <span className="out-gap">{`⋯ ${t("remote.outputGap")}\n`}</span> : null}
      <span className={chunk.stream === "stderr" ? "out-err" : undefined}>
        {hasInvisible(chunk.body) ? <VisibleText text={chunk.body} /> : chunk.body}
      </span>
    </>
  );
}

/** The output of a run, read page by page and shown as plain text; stderr is tinted and a gap is marked. */
export function RunOutput({ runId, truncated, version }: { runId: string; truncated: boolean; version: number }) {
  const { t } = useI18n();
  const errorText = useErrorText();
  const [chunks, setChunks] = useState<RunOutputChunk[]>([]);
  const [done, setDone] = useState(false);
  const [loaded, setLoaded] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const next = useRef(-1);

  const more = useCallback(async () => {
    setBusy(true);
    try {
      const page = await api.runOutput(runId, next.current, config.outputPageSize);
      next.current = page.nextSeq;
      setChunks((c) => {
        const seen = new Set(c.map((x) => x.seq));
        return [...c, ...page.chunks.filter((x) => !seen.has(x.seq))];
      });
      setDone(page.done);
      setError(null);
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
      setLoaded(true);
    }
  }, [runId]);

  useEffect(() => {
    void more();
  }, [more, version]);

  return (
    <div className="run-output-box">
      {error ? <Notice kind="error">{errorText(error)}</Notice> : null}
      {loaded && chunks.length === 0 && !error ? <p className="muted small">{t("remote.noOutput")}</p> : (
        <pre className="run-output" aria-label={t("remote.output")}>{chunks.map((c) => <Chunk key={c.seq} chunk={c} />)}</pre>
      )}
      {truncated ? <p className="muted small">{t("remote.outputTruncated")}</p> : null}
      {!done && loaded ? <Button variant="ghost" busy={busy} disabled={busy} onClick={() => void more()}>{t("remote.outputMore")}</Button> : null}
    </div>
  );
}
