import type { MachineEnvironment, MachineFunction, MachineView } from "../api/types";

/** The environments from the loosest to the strictest, as the API orders them (MachineRoles.EnvironmentOrder). */
export const ENVIRONMENT_ORDER: readonly MachineEnvironment[] = ["dev", "test", "other", "stage", "preprod", "production"];
/** The functions from the loosest to the strictest (MachineRoles.FunctionOrder). */
export const FUNCTION_ORDER: readonly MachineFunction[] = ["development", "ci", "web", "application", "storage", "other", "database"];

/** The set without repeats, loosest first; values outside `order` are dropped. */
export function normalize<T extends string>(values: readonly T[], order: readonly T[]): T[] {
  return order.filter((v) => values.includes(v));
}

/** The strictest member of a set, or null for an empty one: what a reader of a single role sees. */
export function strictest<T extends string>(values: readonly T[], order: readonly T[]): T | null {
  const sorted = normalize(values, order);
  return sorted.length === 0 ? null : sorted[sorted.length - 1]!;
}

/**
 * The set when its strictest member is the single value, else the single value as a set of one: an older API sends no set, and a set that
 * disagrees with the single value is stale (the single value is what the rules enforce), so it is never shown or saved back.
 */
function setOf<T extends string>(values: readonly T[] | null | undefined, one: T | null | undefined, order: readonly T[]): T[] {
  if (!one) return [];
  return values && values.length > 0 && strictest(values, order) === one ? normalize(values, order) : [one];
}

/** The environments a machine's admin set. */
export const environmentsOf = (m: MachineView): MachineEnvironment[] => setOf(m.roleEnvironments, m.roleEnvironment, ENVIRONMENT_ORDER);

/** The functions a machine's admin set. */
export const functionsOf = (m: MachineView): MachineFunction[] => setOf(m.roleFunctions, m.roleFunction, FUNCTION_ORDER);
