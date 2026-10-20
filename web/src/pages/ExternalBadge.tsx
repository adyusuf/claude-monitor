import { useI18n } from "../i18n";

/** The key of the issue in the tool it mirrors, as a link out. Only an https address is linked; anything else shows the key as text. */
export function ExternalBadge({ externalKey, externalUrl }: { externalKey: string | null; externalUrl: string | null }) {
  const { t } = useI18n();
  if (!externalKey) return null;
  const safe = externalUrl !== null && externalUrl.startsWith("https://");
  return safe
    ? <a className="badge" href={externalUrl} target="_blank" rel="noopener noreferrer" title={t("integrations.openInJira")}>{externalKey} ↗</a>
    : <span className="badge" title={t("integrations.fromJira")}>{externalKey}</span>;
}
