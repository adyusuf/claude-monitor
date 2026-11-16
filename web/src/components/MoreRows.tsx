import { useI18n } from "../i18n";
import { Button } from "./ui";

/** Under a list the API cut (X-Truncated): what is shown, and the button that reads the next rows. */
export function MoreRows({ note, busy, onMore }: { note: string; busy: boolean; onMore: () => void }) {
  const { t } = useI18n();
  return (
    <div className="row-input">
      <p className="muted grow">{note}</p>
      <Button variant="ghost" busy={busy} onClick={onMore}>{t("common.loadMore")}</Button>
    </div>
  );
}
