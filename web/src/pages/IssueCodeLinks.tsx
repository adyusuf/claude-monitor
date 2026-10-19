import { useCallback, useEffect, useState } from "react";
import { api } from "../api/endpoints";
import type { CodeLinkKind, CodeLinkView } from "../api/typesIssues";
import { Button, Card, Notice, Spinner } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { codeLinkUrlOk } from "../lib/issueForm";
import { CODE_LINK_KINDS, issueLimits } from "../lib/issueOptions";
import { WebText } from "./ProjectBits";

/** The pull requests and commits of an issue. An address is a link only when it is http or https; anything else stays plain text. */
export function IssueCodeLinks({ issueId, canWrite }: { issueId: string; canWrite: boolean }) {
  const { t } = useI18n();
  const errorText = useErrorText();
  const [rows, setRows] = useState<CodeLinkView[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [kind, setKind] = useState<CodeLinkKind>("pr");
  const [url, setUrl] = useState("");
  const [title, setTitle] = useState("");

  const load = useCallback(async () => {
    try {
      setRows(await api.codeLinks(issueId));
    } catch (e) {
      setError(errorText(e));
    }
    // errorText is a fresh function each render; loading again for it would loop
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [issueId]);

  useEffect(() => {
    void load();
  }, [load]);

  const act = async (work: () => Promise<unknown>, done?: () => void) => {
    try {
      setError(null);
      await work();
      done?.();
      await load();
    } catch (e) {
      setError(errorText(e));
    }
  };

  const add = () => {
    if (!codeLinkUrlOk(url.trim())) return setError(t("errors.invalid_url"));
    void act(() => api.addCodeLink(issueId, { kind, url: url.trim(), title: title.trim() === "" ? null : title.trim() }), () => {
      setUrl("");
      setTitle("");
    });
  };

  return (
    <Card title={t("issues.codeLinks")}>
      {error ? <Notice kind="error">{error}</Notice> : null}
      {rows === null ? <Spinner /> : rows.length === 0 ? <p className="muted">{t("issues.noCodeLinks")}</p> : (
        <ul className="plain">
          {rows.map((l) => (
            <li key={l.id} className="spread">
              <span>
                <span className="badge">{t(`issues.codeKinds.${l.kind}`)}</span>{" "}
                {l.source === "auto" ? <><span className="badge ok">{t("issues.automatic")}</span>{" "}</> : null}
                {l.title ? <strong>{l.title} </strong> : null}
                <WebText value={l.url} />
              </span>
              {canWrite ? <Button variant="ghost" aria-label={`${t("issues.removeLink")}: ${l.title ?? l.url}`} onClick={() => void act(() => api.deleteCodeLink(l.id))}>{t("issues.removeLink")}</Button> : null}
            </li>
          ))}
        </ul>
      )}
      {canWrite ? (
        <form className="row-input add-row" noValidate onSubmit={(e) => {
          e.preventDefault();
          add();
        }}>
          <select className="input kind-pick" aria-label={t("issues.codeKind")} value={kind} onChange={(e) => setKind(e.target.value as CodeLinkKind)}>
            {CODE_LINK_KINDS.map((k) => <option key={k} value={k}>{t(`issues.codeKinds.${k}`)}</option>)}
          </select>
          <input className="input" type="url" aria-label={t("issues.codeUrl")} placeholder={t("issues.codeUrl")} maxLength={issueLimits.urlMax} value={url}
            onChange={(e) => setUrl(e.target.value)} />
          <input className="input" aria-label={t("issues.codeTitle")} placeholder={t("issues.codeTitle")} maxLength={issueLimits.linkTitleMax} value={title}
            onChange={(e) => setTitle(e.target.value)} />
          <Button type="submit">{t("issues.addCodeLink")}</Button>
        </form>
      ) : null}
    </Card>
  );
}
