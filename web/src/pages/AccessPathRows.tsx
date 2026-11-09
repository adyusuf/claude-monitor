import type { AccessApp, AccessOsRules, OsKind } from "../api/typesAccess";
import { Icon } from "../components/icons";
import { Button } from "../components/ui";
import { useI18n } from "../i18n";
import { MAX_PATH_LENGTH, isInterpreter } from "../lib/accessPaths";
import { MAX_APPLICATIONS, MAX_FOLDERS, appProblem, folderProblem } from "../lib/accessPolicy";
import { Group } from "./ProjectBits";

const SHA256_LENGTH = 64;

/** A list of folders for one OS: one path per row, each judged as it is typed (the API judges again). */
function FolderRows({ os, legend, hint, add, rows, allowed, onChange }: {
  os: OsKind; legend: string; hint: string; add: string; rows: string[]; allowed: boolean; onChange: (rows: string[]) => void;
}) {
  const { t, tx } = useI18n();
  return (
    <Group legend={legend}>
      <p className="muted small">{hint}</p>
      {rows.map((row, i) => {
        const problem = row.trim() ? folderProblem(row, os, allowed) : null;
        return (
          <div key={i} className="stack-tight">
            <div className="row-input">
              <input className={problem ? "input input-error path-input" : "input path-input"} aria-label={`${legend} ${i + 1}`} placeholder={t(`access.placeholder.${os}`)}
                maxLength={MAX_PATH_LENGTH} value={row} aria-invalid={problem ? true : undefined} onChange={(e) => onChange(rows.map((r, j) => (j === i ? e.target.value : r)))} />
              <Button variant="subtle" aria-label={`${t("access.remove")} ${legend} ${i + 1}`} onClick={() => onChange(rows.filter((_, j) => j !== i))}><Icon name="close" /></Button>
            </div>
            {problem ? <span className="field-error" role="alert">{tx(`errors.${problem}`)}</span> : null}
          </div>
        );
      })}
      {rows.length < MAX_FOLDERS ? <div><Button variant="ghost" onClick={() => onChange([...rows, ""])}>{add}</Button></div> : null}
    </Group>
  );
}

/** The allowed applications for one OS: an absolute executable each, with the SHA-256 its file must have when one is given. */
function AppRows({ os, rows, onChange }: { os: OsKind; rows: AccessApp[]; onChange: (rows: AccessApp[]) => void }) {
  const { t, tx } = useI18n();
  const legend = t("access.applications");
  const set = (i: number, patch: Partial<AccessApp>) => onChange(rows.map((r, j) => (j === i ? { ...r, ...patch } : r)));
  return (
    <Group legend={legend}>
      <p className="muted small">{t("access.applicationsHint")}</p>
      {rows.map((row, i) => {
        const problem = row.path.trim() || row.sha256 ? appProblem(row, os) : null;
        return (
          <div key={i} className="stack-tight">
            <div className="row-input">
              <input className={problem ? "input input-error path-input" : "input path-input"} aria-label={`${legend} ${i + 1}`} placeholder={t(`access.placeholderApp.${os}`)}
                maxLength={MAX_PATH_LENGTH} value={row.path} aria-invalid={problem ? true : undefined} onChange={(e) => set(i, { path: e.target.value })} />
              <input className="input path-input" aria-label={`${t("access.sha256")} ${i + 1}`} placeholder={t("access.sha256")} maxLength={SHA256_LENGTH}
                value={row.sha256 ?? ""} onChange={(e) => set(i, { sha256: e.target.value.trim() ? e.target.value.trim() : null })} />
              <Button variant="subtle" aria-label={`${t("access.remove")} ${legend} ${i + 1}`} onClick={() => onChange(rows.filter((_, j) => j !== i))}><Icon name="close" /></Button>
            </div>
            {problem ? <span className="field-error" role="alert">{tx(`errors.${problem}`)}</span> : null}
            {!problem && isInterpreter(row.path, os) ? <span className="field-hint">{t("access.interpreterRow")}</span> : null}
          </div>
        );
      })}
      {rows.length < MAX_APPLICATIONS ? <div><Button variant="ghost" onClick={() => onChange([...rows, { path: "", sha256: null }])}>{t("access.addApplication")}</Button></div> : null}
    </Group>
  );
}

/** One OS's section of a policy: allowed folders, denied folders and applications. An empty list means no limit from this policy. */
export function AccessPathRows({ os, rules, onChange }: { os: OsKind; rules: AccessOsRules; onChange: (rules: AccessOsRules) => void }) {
  const { t } = useI18n();
  return (
    <div className="stack">
      <FolderRows os={os} legend={t("access.allowedFolders")} hint={t("access.allowedHint")} add={t("access.addAllowed")} rows={rules.allowedFolders} allowed
        onChange={(allowedFolders) => onChange({ ...rules, allowedFolders })} />
      <FolderRows os={os} legend={t("access.deniedFolders")} hint={t("access.deniedHint")} add={t("access.addDenied")} rows={rules.deniedFolders} allowed={false}
        onChange={(deniedFolders) => onChange({ ...rules, deniedFolders })} />
      <AppRows os={os} rows={rules.applications} onChange={(applications) => onChange({ ...rules, applications })} />
    </div>
  );
}
