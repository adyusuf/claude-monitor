// The path half of the access policy rules, mirroring AccessPolicyRules.Paths.cs and the parts of GrantTemplateRules.cs it uses
// (ADR-0020 §4). Pure and lexical: the OS is a parameter. The API judges again; this only tells the person early.
import type { OsKind } from "../api/typesAccess";

export const MAX_PATH_LENGTH = 1024;
const MIN_ROOT_DEPTH = 2;
const WINDOWS_REFUSED = "*?\"<>|";
const META_CHARS = "\"'`$%~*?[]^!&|<>;{}";
const WINDOWS_EXE = ".exe";
const WINDOWS_DEVICES = new Set(["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$"]);
const FORBIDDEN_UNIX_TOP = ["etc", "root", "proc", "sys", "dev"];

// The programs a grant can never name (GrantTemplateRules.NeverGrantable): shells and interpreters run anything they are given.
const INTERPRETERS = new Set([
  "sh", "bash", "zsh", "dash", "ksh", "fish", "csh", "tcsh", "ash", "busybox", "toybox",
  "env", "sudo", "su", "doas", "pkexec", "runuser", "setsid", "nohup", "xargs", "nice", "timeout", "ssh",
  "perl", "ruby", "node", "nodejs", "deno", "bun", "php", "lua", "tclsh", "awk", "gawk", "mawk", "nawk", "sed",
  "vi", "vim", "ed", "emacs", "less", "more", "man",
  "pwsh", "cmd", "wsl", "wscript", "cscript", "mshta", "rundll32", "regsvr32", "msiexec", "forfiles", "schtasks",
  "sc", "certutil", "bitsadmin", "wmic", "conhost", "explorer", "runas",
  "osascript", "open", "launchctl",
  "dotnet", "java", "docker", "kubectl", "git",
]);
const INTERPRETER_PREFIXES = ["python", "pypy", "powershell"];

export const separatorOf = (os: OsKind): string => (os === "windows" ? "\\" : "/");

const isAsciiLetter = (c: string | undefined) => c !== undefined && /^[A-Za-z]$/.test(c);
const isAsciiDigit = (c: string | undefined) => c !== undefined && c >= "0" && c <= "9";

/** char.IsControl: the C0 and C1 control characters. */
export const isControl = (code: number) => code <= 0x1f || (code >= 0x7f && code <= 0x9f);

/** TextSafety.IsHidden: a control other than a newline or a tab, a bidirectional override or a zero-width character. */
export const isHidden = (code: number) =>
  (isControl(code) && code !== 0x0a && code !== 0x09) || (code >= 0x202a && code <= 0x202e) || (code >= 0x2066 && code <= 0x2069)
  || code === 0x200b || code === 0x200c || code === 0x200d || code === 0x2060 || code === 0xfeff;

/** No control, bidirectional or zero-width character (counted in UTF-16 units, as .NET does). */
export const hasNoHidden = (text: string) => {
  for (let i = 0; i < text.length; i++) {
    const code = text.charCodeAt(i);
    if (isControl(code) || isHidden(code)) return false;
  }
  return true;
};

/** Every surrogate is paired: a text .NET can normalise. */
export function isWellFormed(text: string): boolean {
  for (let i = 0; i < text.length; i++) {
    const code = text.charCodeAt(i);
    const next = text.charCodeAt(i + 1);
    if (code >= 0xd800 && code <= 0xdbff && next >= 0xdc00 && next <= 0xdfff) i++;
    else if (code >= 0xd800 && code <= 0xdfff) return false;
  }
  return true;
}

/** NFC, except for a text with an unpaired surrogate, which is left as it is (and refused by the validation). */
export const nfc = (text: string) => (isWellFormed(text) ? text.normalize("NFC") : text);

/** One path in canonical form: NFC; on Windows "/" becomes "\" and the drive letter upper case; no trailing separator except a root. */
export function canonicalPath(path: string, os: OsKind): string {
  let text = nfc(path);
  const separator = separatorOf(os);
  let root = text.startsWith("/") ? 1 : 0;
  if (os === "windows") {
    text = text.replaceAll("/", "\\");
    const first = text[0];
    if (text.length >= 2 && text[1] === ":" && first !== undefined && first >= "a" && first <= "z") text = first.toUpperCase() + text.slice(1);
    root = text.length >= 3 && text[1] === ":" && text[2] === "\\" ? 3 : 0;
  }
  while (text.length > Math.max(root, 1) && text.endsWith(separator)) text = text.slice(0, -1);
  return text;
}

function isValidSegment(segment: string, windows: boolean): boolean {
  if (segment.length === 0 || segment === "." || segment === "..") return false;
  if (!windows) return true;
  if (segment.endsWith(".") || segment.endsWith(" ")) return false;
  for (let i = 0; i + 1 < segment.length; i++) if (segment[i] === "~" && isAsciiDigit(segment[i + 1])) return false;
  return true;
}

/** An absolute folder or file path for the OS (AccessPolicyRules.IsValidPath). */
export function isValidPath(path: string, os: OsKind): boolean {
  if (path.length < 1 || path.length > MAX_PATH_LENGTH || !isWellFormed(path) || !hasNoHidden(path)) return false;
  const windows = os === "windows";
  let rest: string;
  if (windows) {
    if (path.length < 3 || !isAsciiLetter(path[0]) || path[1] !== ":" || path[2] !== "\\") return false;
    rest = path.slice(3);
    if ([...rest].some((c) => c === ":" || c === "/" || WINDOWS_REFUSED.includes(c))) return false;
  } else {
    if (path[0] !== "/") return false;
    rest = path.slice(1);
  }
  return rest.length === 0 || rest.split(separatorOf(os)).every((segment) => isValidSegment(segment, windows));
}

function isDeviceName(segment: string): boolean {
  const dot = segment.indexOf(".");
  const stem = (dot >= 0 ? segment.slice(0, dot) : segment).replace(/ +$/, "").toUpperCase();
  return WINDOWS_DEVICES.has(stem) || (stem.length === 4 && (stem.startsWith("COM") || stem.startsWith("LPT")) && isAsciiDigit(stem[3]));
}

function isCleanSegment(segment: string, windows: boolean): boolean {
  if (segment.length === 0 || segment === "." || segment === ".." || segment.startsWith(" ") || segment.endsWith(" ")) return false;
  for (const c of segment) {
    if (c < " " || c > "~" || META_CHARS.includes(c) || c === "\\") return false;
    if (windows && (c === ":" || c === "/")) return false;
  }
  return !windows || (!segment.endsWith(".") && !isDeviceName(segment));
}

/** An absolute executable path a grant could name (GrantMatcher.IsAbsoluteExecutable): plain ASCII, no metacharacters; on Windows an .exe. */
export function isAbsoluteExecutable(path: string, os: OsKind): boolean {
  if (path.length === 0 || path.length > MAX_PATH_LENGTH) return false;
  const windows = os === "windows";
  let rest: string;
  if (windows) {
    if (path.length < 3 || !isAsciiLetter(path[0]) || path[1] !== ":" || path[2] !== "\\") return false;
    rest = path.slice(3);
  } else {
    if (path[0] !== "/") return false;
    rest = path.slice(1);
  }
  if (rest.length === 0) return false;
  const segments = rest.split(separatorOf(os));
  if (!segments.every((segment) => isCleanSegment(segment, windows))) return false;
  const last = segments[segments.length - 1] ?? "";
  return !windows || (last.length > WINDOWS_EXE.length && last.toLowerCase().endsWith(WINDOWS_EXE));
}

// Only ASCII letters are folded: .NET and the browser fold some other letters differently, and the API decides anyway.
const sameName = (a: string, b: string, os: OsKind) =>
  os === "linux" ? a === b : a.length === b.length && [...a].every((c, i) => c === b[i] || (isAsciiLetter(c) && c.toLowerCase() === b[i]?.toLowerCase()));

/** A folder too close to the root, or a system folder, to be an allowed folder (GrantTemplateRules.IsForbiddenRoot). */
export function isForbiddenRoot(root: string, os: OsKind): boolean {
  if (root.length === 0) return true;
  const windows = os === "windows";
  const parts = root.split(separatorOf(os)).filter((p) => p.length > 0);
  const segments = windows && parts.length > 0 && parts[0]!.endsWith(":") ? parts.slice(1) : parts;
  if (segments.length < MIN_ROOT_DEPTH) return true;
  const top = segments[0]!;
  if (windows) return sameName(top, "Windows", os);
  if (sameName(top, "private", os) && sameName(segments[1]!, "etc", os)) return true;
  return FORBIDDEN_UNIX_TOP.some((name) => sameName(top, name, os));
}

function programName(path: string, os: OsKind): string {
  let name = path.slice(Math.max(path.lastIndexOf("/"), path.lastIndexOf("\\")) + 1).toLowerCase();
  if (os === "windows") {
    name = name.replace(/[. ]+$/, "");
    const dot = name.lastIndexOf(".");
    if (dot > 0) name = name.slice(0, dot);
  }
  return name;
}

function stripVersion(name: string): string {
  let end = name.length;
  while (end > 0 && (isAsciiDigit(name[end - 1]) || (name[end - 1] === "." && end > 1 && isAsciiDigit(name[end - 2])))) end--;
  if (end > 0 && end < name.length && (name[end - 1] === "-" || name[end - 1] === "_")) end--;
  return end === 0 ? name : name.slice(0, end);
}

/** A shell or an interpreter (GrantMatcher.IsInterpreter): allowing it lets a run start anything, so an application list with it limits little. */
export function isInterpreter(path: string, os: OsKind): boolean {
  if (!path) return false;
  const name = programName(path, os);
  const key = stripVersion(name);
  return INTERPRETERS.has(name) || INTERPRETERS.has(key) || INTERPRETER_PREFIXES.some((p) => name.startsWith(p) || key.startsWith(p));
}
