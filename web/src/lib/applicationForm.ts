// The editable form of an application, and the checks the form runs before it asks the API (the API checks again, global #7).
import type {
  ApplicationInput, ApplicationKind, ApplicationView, EnvironmentName, TargetKind, VersioningScheme,
} from "../api/typesProjects";
import type { FieldErrors } from "./projectForm";
import { projectLimits as limit } from "./projectOptions";

export interface ApplicationDraft {
  name: string;
  description: string;
  kind: ApplicationKind;
  repoUrl: string;
  environments: { name: EnvironmentName; url: string }[];
  techStack: string[];
  versioningScheme: VersioningScheme;
  techOwnerUserId: string;
  nonFunctionalTargets: { kind: TargetKind; target: string }[];
}

export const emptyApplicationDraft = (): ApplicationDraft => ({
  name: "", description: "", kind: "web", repoUrl: "", environments: [], techStack: [], versioningScheme: "semver", techOwnerUserId: "",
  nonFunctionalTargets: [],
});

export const draftOfApplication = (a: ApplicationView): ApplicationDraft => ({
  name: a.name, description: a.description ?? "", kind: a.kind, repoUrl: a.repoUrl ?? "",
  environments: a.environments.flatMap((e) => e.name ? [{ name: e.name, url: e.url ?? "" }] : []),
  techStack: [...a.techStack], versioningScheme: a.versioningScheme, techOwnerUserId: a.techOwnerUserId ?? "",
  nonFunctionalTargets: a.nonFunctionalTargets.flatMap((n) => n.kind ? [{ kind: n.kind, target: n.target ?? "" }] : []),
});

const text = (value: string): string | null => value.trim() === "" ? null : value.trim();

export const inputOfApplication = (d: ApplicationDraft): ApplicationInput => ({
  name: d.name.trim(), description: text(d.description), kind: d.kind, repoUrl: text(d.repoUrl),
  environments: d.environments.map((e) => ({ name: e.name, url: text(e.url) })), techStack: d.techStack,
  versioningScheme: d.versioningScheme, techOwnerUserId: d.techOwnerUserId || null,
  nonFunctionalTargets: d.nonFunctionalTargets.map((n) => ({ kind: n.kind, target: n.target.trim() })),
});

const parse = (value: string): URL | null => {
  try {
    return new URL(value);
  } catch {
    return null;
  }
};

/** A repository link is https, ssh or git (or the scp form git@host:path) with no password; the API's rule, in short. */
const repoLinkOk = (value: string): boolean => {
  if (value.length > limit.urlMax) return false;
  if (/^[A-Za-z0-9._-]+@[A-Za-z0-9.-]+:[A-Za-z0-9._/~-]+$/.test(value)) return true;
  const url = parse(value);
  return url !== null && ["https:", "ssh:", "git:"].includes(url.protocol) && url.hostname !== "" && url.password === "";
};

/** An environment address is http or https with no user information. */
const webLinkOk = (value: string): boolean => {
  const url = parse(value);
  return value.length <= limit.urlMax && url !== null && ["https:", "http:"].includes(url.protocol) && url.hostname !== "" && url.username === "" && url.password === "";
};

export function checkApplication(d: ApplicationDraft): FieldErrors {
  const errors: FieldErrors = {};
  const input = inputOfApplication(d);
  if (input.name === "" || input.name.length > limit.nameMax) errors.name = "invalid_name";
  if ((input.description?.length ?? 0) > limit.purposeMax) errors.description = "too_long";
  if (input.repoUrl && !repoLinkOk(input.repoUrl)) errors.repoUrl = "invalid_url";
  if (input.environments.some((e) => e.url && !webLinkOk(e.url))) errors.environments = "invalid_url";
  if (input.techStack.length > limit.techStackMax) errors.techStack = "invalid_list";
  if (input.nonFunctionalTargets.length > limit.targetsMax || input.nonFunctionalTargets.some((n) => !n.target || n.target.length > limit.lineMax)) {
    errors.nonFunctionalTargets = "invalid_list";
  }

  return errors;
}
