import type { ApplicationView } from "../api/typesProjects";
import { Card } from "../components/ui";
import { useI18n } from "../i18n";
import { Chips, WebText } from "./ProjectBits";

/** What an application is: every field of its definition, as text; addresses are links only when they are web addresses. */
export function ApplicationDefinition({ app: a }: { app: ApplicationView }) {
  const { t } = useI18n();
  const none = <span className="muted">–</span>;
  return (
    <Card title={t("projects.definition")}>
      <dl className="facts">
        <dt>{t("projects.description")}</dt><dd className="pre">{a.description ?? none}</dd>
        <dt>{t("projects.kind")}</dt><dd>{t(`projects.kinds.${a.kind}`)}</dd>
        <dt>{t("projects.repoUrl")}</dt><dd>{a.repoUrl ? <WebText value={a.repoUrl} /> : none}</dd>
        <dt>{t("projects.environments")}</dt>
        <dd>
          {a.environments.length === 0 ? none : (
            <ul className="bullets">
              {a.environments.map((e) => <li key={e.name}>{e.name ? t(`projects.env.${e.name}`) : ""}{e.url ? <> · <WebText value={e.url} /></> : null}</li>)}
            </ul>
          )}
        </dd>
        <dt>{t("projects.techStack")}</dt><dd>{a.techStack.length === 0 ? none : <Chips label={t("projects.techStack")} items={a.techStack} />}</dd>
        <dt>{t("projects.versioning")}</dt><dd>{t(`projects.versions.${a.versioningScheme}`)}</dd>
        <dt>{t("projects.techOwner")}</dt><dd>{a.techOwnerName ?? none}</dd>
        <dt>{t("projects.targets")}</dt>
        <dd>
          {a.nonFunctionalTargets.length === 0 ? none : (
            <ul className="bullets">
              {a.nonFunctionalTargets.map((n, i) => <li key={i}>{n.kind ? <strong>{t(`projects.target.${n.kind}`)}</strong> : null} {n.target}</li>)}
            </ul>
          )}
        </dd>
      </dl>
    </Card>
  );
}
