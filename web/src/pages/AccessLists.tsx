import type { AccessApp } from "../api/typesAccess";
import { VisibleText } from "../components/VisibleText";
import { useI18n } from "../i18n";

/** One list of paths, each shown with every character it has; an empty list says what it means (no limit, or nothing); extra follows the list. */
function PathList({ label, paths, empty, extra }: { label: string; paths: string[]; empty: string; extra?: string }) {
  return (
    <>
      <dt>{label}</dt>
      <dd>
        {paths.length === 0 ? <span className="muted">{empty}</span> : (
          <ul className="plain stack-tight" aria-label={label}>
            {paths.map((p) => <li key={p}><code><VisibleText text={p} /></code></li>)}
          </ul>
        )}
        {paths.length > 0 && extra ? <span className="muted small">{extra}</span> : null}
      </dd>
    </>
  );
}

/**
 * The folders and applications of one access layer, read-only: allowed, denied, and the applications with their SHA-256 when one is pinned. With
 * projectFolder (a policy that includes the project's own folder) the allowed folders always limit: the listed ones plus the project's folder, and
 * with none listed only the project's folder (AccessPolicyMath.Policy). Without it an empty allowed list is no folder limit.
 */
export function AccessLists({ allowed, denied, applications, projectFolder = false }: {
  allowed: string[]; denied: string[]; applications: AccessApp[]; projectFolder?: boolean;
}) {
  const { t } = useI18n();
  const apps = t("access.applications");
  return (
    <dl className="facts">
      <PathList label={t("access.allowedFolders")} paths={allowed} empty={projectFolder ? t("access.onlyProjectFolder") : t("access.noFolderLimit")}
        extra={projectFolder ? t("access.plusProjectFolder") : undefined} />
      <PathList label={t("access.deniedFolders")} paths={denied} empty={t("access.noneDenied")} />
      <dt>{apps}</dt>
      <dd>
        {applications.length === 0 ? <span className="muted">{t("access.noAppLimit")}</span> : (
          <ul className="plain stack-tight" aria-label={apps}>
            {applications.map((a) => (
              <li key={`${a.path}\n${a.sha256 ?? ""}`}>
                <code><VisibleText text={a.path} /></code>
                {a.sha256 ? <span className="muted small"> {t("access.sha256")} <code className="access-hash">{a.sha256}</code></span> : null}
              </li>
            ))}
          </ul>
        )}
      </dd>
    </dl>
  );
}
