/** True when dotted version `version` is at least `min` ("0.5.0"); anything that is not numbers and dots counts as lower. */
export function versionAtLeast(version: string | null | undefined, min: string): boolean {
  const parse = (v: string) => (/^\d+(\.\d+)*$/.test(v) ? v.split(".").map(Number) : null);
  const a = version ? parse(version) : null;
  const b = parse(min);
  if (!a || !b) return false;
  for (let i = 0; i < Math.max(a.length, b.length); i++) {
    const x = a[i] ?? 0;
    const y = b[i] ?? 0;
    if (x !== y) return x > y;
  }
  return true;
}
