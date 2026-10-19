import { useEffect, useState } from "react";
import { Link } from "react-router";
import { api } from "../api/endpoints";
import type { IssueLinkView, IssueSummary, LinkKind } from "../api/typesIssues";
import { Button, Card, Notice } from "../components/ui";
import { config } from "../config";
import { useErrorText, useI18n } from "../i18n";
import { issueLimits, LINK_KINDS } from "../lib/issueOptions";
import { StatusBadge } from "./IssueBits";

function LinkList({ ws, projectId, title, rows, canWrite, onRemove }: {
  ws: string; projectId: string; title: string; rows: IssueLinkView[]; canWrite: boolean; onRemove: (linkId: string) => void;
}) {
  const { t } = useI18n();
  return (
    <section aria-label={title}>
      <h3 className="subhead">{title}</h3>
      {rows.length === 0 ? <p className="muted">{t("issues.noLinks")}</p> : (
        <ul className="plain">
          {rows.map((l) => (
            <li key={l.linkId} className="spread">
              <span><Link to={`/w/${ws}/projects/${projectId}/issues/${l.issueId}`}>#{l.number} {l.title}</Link> <StatusBadge status={l.status} /></span>
              {canWrite ? <Button variant="ghost" aria-label={`${t("issues.removeLink")} #${l.number}`} onClick={() => onRemove(l.linkId)}>{t("issues.removeLink")}</Button> : null}
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

/** The issues this one blocks and the ones that block it. An issue is added from a search of the project's issues; the API refuses a loop, a repeat and a link to itself. */
export function IssueLinks({ ws, projectId, issueId, blocks, blockedBy, canWrite, onChanged }: {
  ws: string; projectId: string; issueId: string; blocks: IssueLinkView[]; blockedBy: IssueLinkView[]; canWrite: boolean; onChanged: () => Promise<void>;
}) {
  const { t } = useI18n();
  const errorText = useErrorText();
  const [search, setSearch] = useState("");
  const [found, setFound] = useState<IssueSummary[]>([]);
  const [other, setOther] = useState("");
  const [kind, setKind] = useState<LinkKind>("blocks");
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (search.trim() === "") return;
    let live = true;
    const timer = setTimeout(() => {
      api.issues(projectId, { q: search.trim(), limit: issueLimits.searchLimit }).then(
        (page) => live && setFound(page.items.filter((x) => x.id !== issueId)),
        (e: unknown) => live && setError(errorText(e)),
      );
    }, config.liveDebounceMs);
    return () => {
      live = false;
      clearTimeout(timer);
    };
    // errorText is a fresh function each render; searching again for it would loop
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [search, projectId, issueId]);

  const act = async (work: () => Promise<unknown>, done?: () => void) => {
    try {
      setError(null);
      await work();
      done?.();
      await onChanged();
    } catch (e) {
      setError(errorText(e));
    }
  };

  const add = () => act(() => api.addIssueLink(issueId, other, kind), () => {
    setSearch("");
    setFound([]);
    setOther("");
  });
  const shown = search.trim() === "" ? [] : found;

  return (
    <Card title={t("issues.links")}>
      {error ? <Notice kind="error">{error}</Notice> : null}
      <LinkList ws={ws} projectId={projectId} title={t("issues.blocks")} rows={blocks} canWrite={canWrite} onRemove={(id) => void act(() => api.removeIssueLink(id))} />
      <LinkList ws={ws} projectId={projectId} title={t("issues.blockedBy")} rows={blockedBy} canWrite={canWrite} onRemove={(id) => void act(() => api.removeIssueLink(id))} />
      {canWrite ? (
        <form className="row-input add-row" onSubmit={(e) => {
          e.preventDefault();
          if (other) void add();
        }}>
          <select className="input kind-pick" aria-label={t("issues.linkKind")} value={kind} onChange={(e) => setKind(e.target.value as LinkKind)}>
            {LINK_KINDS.map((k) => <option key={k} value={k}>{t(`issues.linkKinds.${k}`)}</option>)}
          </select>
          <input className="input" type="search" aria-label={t("issues.searchToLink")} placeholder={t("issues.searchToLink")} value={search}
            onChange={(e) => setSearch(e.target.value)} />
          <select className="input" aria-label={t("issues.linkTarget")} value={other} onChange={(e) => setOther(e.target.value)}>
            <option value="">{shown.length === 0 && search.trim() ? t("issues.noMatch") : "–"}</option>
            {shown.map((x) => <option key={x.id} value={x.id}>{`#${x.number} ${x.title}`}</option>)}
          </select>
          <Button type="submit" disabled={!other}>{t("issues.addLink")}</Button>
        </form>
      ) : null}
    </Card>
  );
}
