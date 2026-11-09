import { useEffect, useState } from "react";
import { Link } from "react-router";
import { ApiError } from "../api/client";
import { api } from "../api/endpoints";
import type { AccessPolicySummary } from "../api/typesAccess";
import { Icon } from "../components/icons";
import { useErrorText, useI18n } from "../i18n";

type Found = { kind: "policy"; policy: AccessPolicySummary } | { kind: "none" } | { kind: "error"; text: string };

/**
 * One line on the session page (ADR-0020 §4): the access policy that covers the session's repository, with its version, whether it is signed and
 * whether it allows shell runs, or that none does. Nothing for a session that names no repository (or an API that does not say which).
 */
export function SessionAccessLine({ ws, projectKey }: { ws: string; projectKey: string | null | undefined }) {
  const { t } = useI18n();
  const errorText = useErrorText();
  const [found, setFound] = useState<Found | null>(null);

  useEffect(() => {
    if (!projectKey) return;
    let live = true;
    api.accessPolicyByKey(ws, projectKey).then((policy) => { if (live) setFound({ kind: "policy", policy }); }).catch((e: unknown) => {
      if (!live) return;
      setFound(e instanceof ApiError && e.status === 404 ? { kind: "none" } : { kind: "error", text: errorText(e) });
    });
    return () => { live = false; };
    // errorText changes identity every render; the workspace and the repository are what matter
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ws, projectKey]);

  if (!projectKey || found === null) return null;
  if (found.kind === "error") return <span className="muted"><Icon name="lock" /> {found.text}</span>;
  if (found.kind === "none") return <span className="muted"><Icon name="lock" /> {t("access.sessionNone")}</span>;
  const { policy } = found;
  return (
    <span>
      <Icon name="lock" /> <Link to={`/w/${ws}/projects/${policy.projectId}`}>{t("access.sessionPolicy", { project: policy.projectName, version: policy.version })}</Link>
      {" · "}{policy.isSigned ? t("access.signed") : t("access.unsigned")}
      {" · "}{policy.shell ? t("access.shellOn") : t("access.shellOff")}
    </span>
  );
}
