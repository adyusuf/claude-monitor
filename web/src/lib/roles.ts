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

/** The environments a machine's admin set: the set an API sends, else the single value as a set of one (an older API). */
export const environmentsOf = (m: MachineView): MachineEnvironment[] =>
  m.roleEnvironments && m.roleEnvironments.length > 0 ? normalize(m.roleEnvironments, ENVIRONMENT_ORDER) : m.roleEnvironment ? [m.roleEnvironment] : [];

/** The functions a machine's admin set, the same way. */
export const functionsOf = (m: MachineView): MachineFunction[] =>
  m.roleFunctions && m.roleFunctions.length > 0 ? normalize(m.roleFunctions, FUNCTION_ORDER) : m.roleFunction ? [m.roleFunction] : [];
