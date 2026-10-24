import { useId } from "react";
import { FieldSource, Setting, SETTING_NAMES, SwitchValue, type SettingFieldView } from "../api/typesServerSettings";
import { Button, Field } from "../components/ui";
import { useI18n } from "../i18n";
import { editable } from "../lib/serverSettings";

/** The label of a setting; a name this web does not know is shown as it is. */
export function useFieldLabel() {
  const { t } = useI18n();
  return (name: string) => {
    const known = SETTING_NAMES.find((n) => n === name);
    return known ? t(`serverSettings.fields.${known}`) : name;
  };
}

interface Props {
  field: SettingFieldView;
  value: string;
  onChange: (value: string) => void;
  removed: boolean;
  onRemove: (removed: boolean) => void;
  disabled: boolean;
  error?: string;
}

/**
 * One setting of an integration. A value set on the server is read-only and names its environment variable; a secret never
 * shows its value, only whether one is saved, and an empty password box keeps it; STARTTLS is a switch, the port a number.
 */
export function ServerSettingField({ field, value, onChange, removed, onRemove, disabled, error }: Props) {
  const { t } = useI18n();
  const id = useId();
  const labelOf = useFieldLabel();
  const name = labelOf(field.name);
  const label = field.required ? `${name} (${t("serverSettings.required")})` : name;

  if (!editable(field)) {
    const where = field.source === FieldSource.server ? t("serverSettings.setOnServer", { env: field.envName }) : field.source;
    // The server's value wins, but a value saved on the page under it would come back if the server's went: it can be removed.
    const hidden = field.source === FieldSource.server && field.savedHidden === true ? (
      <div>
        <span className="field-hint">{removed ? t("serverSettings.removing") : t("serverSettings.hiddenSaved")}</span>
        {removed ? (
          <Button variant="subtle" disabled={disabled} onClick={() => onRemove(false)}>{t("serverSettings.undoRemove")}</Button>
        ) : (
          <Button variant="danger" disabled={disabled} aria-label={t("serverSettings.hiddenRemoveNamed", { name })} onClick={() => onRemove(true)}>
            {t("serverSettings.hiddenRemove")}
          </Button>
        )}
      </div>
    ) : null;
    if (field.secret) {
      return (
        <div className="field">
          <span className="field-label">{label}</span>
          <span className="field-hint">{where}</span>
          {hidden}
        </div>
      );
    }
    return (
      <div className="stack">
        <Field label={label} value={field.value ?? ""} readOnly hint={where} error={error} />
        {hidden}
      </div>
    );
  }

  if (field.secret) {
    const saved = field.source === FieldSource.page;
    return (
      <div className="stack">
        <Field label={label} type="password" autoComplete="new-password" value={value} disabled={disabled || removed}
          hint={removed ? t("serverSettings.removing") : `${saved ? t("serverSettings.saved") : t("serverSettings.notSet")} · ${t("serverSettings.secretKeep")}`}
          error={error} onChange={(e) => onChange(e.target.value)} />
        {saved ? (
          <div>
            {removed ? (
              <Button variant="subtle" disabled={disabled} onClick={() => onRemove(false)}>{t("serverSettings.undoRemove")}</Button>
            ) : (
              <Button variant="danger" disabled={disabled} aria-label={t("serverSettings.removeNamed", { name })} onClick={() => onRemove(true)}>
                {t("serverSettings.remove")}
              </Button>
            )}
          </div>
        ) : null}
      </div>
    );
  }

  if (field.name === Setting.tls) {
    const savedOnPage = field.source === FieldSource.page;
    return (
      <div className="field">
        <label className="check" htmlFor={id}>
          <input id={id} type="checkbox" checked={value === SwitchValue.on} disabled={disabled || removed} aria-invalid={error ? true : undefined}
            onChange={(e) => onChange(e.target.checked ? SwitchValue.on : SwitchValue.off)} />
          <span>{label}</span>
        </label>
        {error ? <span className="field-error" role="alert">{error}</span> : null}
        {removed ? <span className="field-hint">{t("serverSettings.removing")}</span> : null}
        {savedOnPage ? (
          <div>
            {removed ? (
              <Button variant="subtle" disabled={disabled} onClick={() => onRemove(false)}>{t("serverSettings.undoRemove")}</Button>
            ) : (
              <Button variant="subtle" disabled={disabled} aria-label={t("serverSettings.resetNamed", { name })} onClick={() => onRemove(true)}>
                {t("serverSettings.resetDefault")}
              </Button>
            )}
          </div>
        ) : null}
      </div>
    );
  }

  const port = field.name === Setting.port;
  return (
    <Field label={label} type={port ? "number" : "text"} inputMode={port ? "numeric" : undefined} autoComplete="off"
      value={value} disabled={disabled} required={field.required} error={error} onChange={(e) => onChange(e.target.value)} />
  );
}
