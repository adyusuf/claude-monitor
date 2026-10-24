// What a card of the server settings page sends (ADR-0017): only the fields that changed. A value set on the server itself is never
// sent (the API refuses it; only the saved value it hides can be removed), an empty secret means "keep", and removing a saved value sends an empty one.
import { FieldSource, type IntegrationSettingsView, type SettingFieldView } from "../api/typesServerSettings";

/** The values typed into a card, by field name. */
export type Draft = Record<string, string>;

/** The field can be typed into on the page: its value comes from the page or from nowhere yet. An unknown source is left alone. */
export function editable(field: SettingFieldView): boolean {
  switch (field.source) {
    case FieldSource.page:
    case FieldSource.none:
      return true;
    case FieldSource.server:
      return false;
    default:
      return false; // a source this web does not know: shown, never sent
  }
}

/** What the inputs start with: the value in force for a setting, and nothing for a secret (its value never reaches the page). */
export function initialDraft(integration: IntegrationSettingsView): Draft {
  const draft: Draft = {};
  for (const field of integration.fields) if (editable(field)) draft[field.name] = field.secret ? "" : (field.value ?? "");
  return draft;
}

/** The values to save: each changed setting, each typed secret, and "" for each saved value marked for removal. */
export function changedValues(integration: IntegrationSettingsView, draft: Draft, removed: ReadonlySet<string>): Record<string, string> {
  const values: Record<string, string> = {};
  for (const field of integration.fields) {
    // A value the server hides is not editable, but the saved one under it can be removed.
    if (field.source === FieldSource.server) {
      if (field.savedHidden === true && removed.has(field.name)) values[field.name] = "";
      continue;
    }
    if (!editable(field)) continue;
    const typed = draft[field.name] ?? "";
    if (removed.has(field.name)) values[field.name] = "";
    else if (field.secret) {
      if (typed !== "") values[field.name] = typed;
    } else if (typed !== (field.value ?? "")) {
      values[field.name] = typed;
    }
  }
  return values;
}
