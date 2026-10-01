#!/usr/bin/env python3
"""CLAUDE.md simplification gate — it catches rule loss.

Usage:  python3 scripts/md-rule-gate.py <old-file> <new-file>

⚠️ If the move was spread across several files, the "new" side is given CONCATENATED:
     cat CLAUDE.md docs/decision-log.md > /tmp/new.md
     python3 scripts/md-rule-gate.py <(git show origin/dev:CLAUDE.md) /tmp/new.md
⚠️ The canonical copy lives under ~/.claude/scripts/; every project takes a COPY
   and commits it. The three tools are twins — changing one means changing all.

The only acceptable form of simplification is MOVING content, not deleting it.
This script checks whether a RULE fell out of the active file. Moving history or
migration narrative into an archive is the expected behaviour and is reported,
but it does not break the gate; losing a rule does.

What counts as carrying a rule (the author's own markers):
  1. Never-do list items that start with ❌
  2. Lines carrying an obligation or a prohibition (PERMANENT / MANDATORY /
     FORBIDDEN, and plain modal wording such as never / only / must / no)
  3. Identifiers inside backticks (file path, class, endpoint, flag)

Matching is normalized (markdown decoration, whitespace and case are stripped),
because simplification may reformat a sentence — but the SUBSTANCE of the rule
and the identifiers inside it must survive verbatim.

Exit code: 0 = passed, 1 = a rule was lost (in which case DO NOT merge).
"""
import re
import sys
import unicodedata

# ⚠ This pattern was verified BY MUTATION and has been WIDENED once. Its first
# version looked only for PERMANENT/MANDATORY/FORBIDDEN, and a mutant that
# deleted a real rule written in plain prohibitive prose ("no direct push to
# `test`/`prod`") PASSED the gate. Most rules carry no uppercase label — they
# carry an obligation or prohibition MOOD. If a new rule mood turns up, add it
# here; never narrow the pattern.
RULE_MARKER = re.compile(
    r"PERMANENT|MANDATORY|FORBIDDEN|REQUIRED"
    r"|\bnever\b|\balways\b|\bonly\b|\bmust\b|\bcannot\b|\bcan not\b|\bmay not\b"
    r"|\bshall\b|\brequired\b|\bmandatory\b|\bforbidden\b|\bprohibited\b"
    r"|\bdo not\b|\bdon't\b|\bdoes not\b|\bis not\b|\bare not\b"
    r"|\bno\b|\bnot\b|\bnothing\b|\bevery\b|\beach\b",
    re.IGNORECASE,
)
# An identifier: a backticked fragment containing at least a dot/slash/parens or
# written in CamelCase — so that plain words like "see" are filtered out.
IDENTIFIER = re.compile(r"`([^`\n]{3,80})`")
MEANINGFUL_IDENTIFIER = re.compile(r"[./]|[a-z][A-Z]|^[A-Z][a-zA-Z]+[A-Z]|\(\)|^--|^-[A-Za-z]")


def normalize(text: str) -> str:
    text = unicodedata.normalize("NFKC", text)
    text = re.sub(r"[*_`>#\[\]()]", " ", text)     # markdown decoration
    text = re.sub(r"[^\w\s/.:-]", " ", text)       # emoji, punctuation
    text = re.sub(r"\s+", " ", text)
    return text.strip().lower()


def never_do_items(text: str):
    """Never-do list items marked with ❌."""
    out = []
    for line in text.splitlines():
        if "❌" in line:
            normalized = normalize(line.split("❌", 1)[1])
            if len(normalized) >= 12:
                out.append((line.strip(), normalized))
    return out


def rule_lines(text: str):
    """Lines that carry an obligation or a prohibition."""
    out = []
    for line in text.splitlines():
        if RULE_MARKER.search(line):
            normalized = normalize(line)
            if len(normalized) >= 12:
                out.append((line.strip(), normalized))
    return out


def identifiers(text: str):
    out = set()
    for match in IDENTIFIER.finditer(text):
        candidate = match.group(1).strip()
        if MEANINGFUL_IDENTIFIER.search(candidate):
            out.add(candidate)
    return out


NEGATION = re.compile(
    r"\binvalid\b|\bno longer\b|\bremoved\b|\brevoked\b|\bcancelled\b|\bcanceled\b"
    r"|\bsuperseded\b|\bdoes not apply\b|\brepealed\b|\bobsolete\b|\bfree to\b|\bwithdrawn\b",
    re.IGNORECASE,
)
MIN_RATIO = 0.6   # the new line must be at least this fraction of the old one's length


def matching_line(needle: str, new_lines):
    """Returns the NEW LINE carrying the substance of the rule (None if there is
    none). It does not look for the whole sentence (that may have been
    reformatted); it looks for meaningful word windows. Returning the matched
    line matters: length and negation are measured ON THAT LINE — they cannot be
    measured across the whole document."""
    words = needle.split()
    candidates = [needle]
    for window in (10, 7, 5, 4):
        if len(words) >= window:
            candidates.append(" ".join(words[:window]))
            middle = len(words) // 2
            candidates.append(" ".join(words[middle:middle + window]))
    # ⚠️ "Take the LONGEST matching line" was WRONG (caught in a regression): a
    # short line's window can also occur inside an unrelated LONG item, and the
    # word "removed" in that item's historical prose then produces a false
    # negation. The correct rule: an exact containment if there is one, otherwise
    # the line whose length is CLOSEST TO THE OLD ONE.
    exact = [x for x in new_lines if needle in x]
    if exact:
        return min(exact, key=lambda x: abs(len(x) - len(needle)))
    partial = [x for x in new_lines if any(c in x for c in candidates)]
    if not partial:
        return None
    return min(partial, key=lambda x: abs(len(x) - len(needle)))


def _triage_file():
    import pathlib
    here = pathlib.Path(__file__)
    for name in ("md-gate-triage.json",):
        candidate = here.with_name(name)
        if candidate.exists():
            return candidate
    return None


def rule_triage():
    """The "rule" map inside md-gate-triage.json: {the normalized FIRST 60
    CHARACTERS of the old line: reason}. ⚠️ An exemption is granted to a VALUE, not
    to a path, and it MUST carry a reason — an empty reason is not an exemption. A
    deliberate rewrite (an item proven wrong IN THE CODE) is recorded here;
    otherwise the gate breaks on it as "truncated", and it is right to."""
    import json
    path = _triage_file()
    if path is None:
        return {}
    data = json.loads(path.read_text(encoding="utf-8"))
    return {k: v for k, v in (data.get("rule") or {}).items()
            if isinstance(v, str) and v.strip()}


def is_lost(raw: str, normalized: str, new_lines, triage=None):
    """(reason | None). ⚠️ Two blind spots were found BY MUTATION and closed:
    (1) the rule text was still present but "NO LONGER VALID / FREE TO" had been
    appended — the old gate only checked for PRESENCE and passed. (2) the body of
    an item with no backticks was deleted and its first six words left behind —
    the window match succeeded and it passed. Both were verified with a control
    variable (a full deletion is still caught)."""
    key = normalized[:60]
    if triage and key in triage:
        return None   # a deliberate, justified change — listed separately in the report
    line = matching_line(normalized, new_lines)
    if line is None:
        return "LOST"
    if len(line) < MIN_RATIO * len(normalized):
        return "TRUNCATED (%d -> %d characters, the body is gone)" % (len(normalized), len(line))
    new_markers = set(m.group(0).lower() for m in NEGATION.finditer(line))
    old_markers = set(m.group(0).lower() for m in NEGATION.finditer(normalized))
    added = new_markers - old_markers
    if added:
        return "POSSIBLY INVERTED (new negation: %s)" % ", ".join(sorted(added))
    return None


def main() -> int:
    if len(sys.argv) != 3:
        print(__doc__)
        return 2
    old = open(sys.argv[1], encoding="utf-8").read()
    new = open(sys.argv[2], encoding="utf-8").read()
    # Normalized line by line: length and negation are measured PER LINE.
    new_lines = [normalize(l) for l in new.splitlines() if normalize(l)]

    lost_items, lost_rules = [], []
    triage = rule_triage()
    triaged = [(raw, triage[n[:60]])
               for raw, n in never_do_items(old) + rule_lines(old) if n[:60] in triage]

    for raw, normalized in never_do_items(old):
        reason = is_lost(raw, normalized, new_lines, triage)
        if reason:
            lost_items.append((raw, reason))

    for raw, normalized in rule_lines(old):
        reason = is_lost(raw, normalized, new_lines, triage)
        if reason:
            lost_rules.append((raw, reason))

    old_ids, new_ids = identifiers(old), identifiers(new)
    lost_ids = sorted(old_ids - new_ids)

    old_lines_n, new_lines_n = len(old.splitlines()), len(new.splitlines())
    old_kb, new_kb = len(old.encode()) / 1024, len(new.encode()) / 1024
    print(f"lines       : {old_lines_n} -> {new_lines_n}")
    print(f"size        : {old_kb:.0f} KB -> {new_kb:.0f} KB")
    print(f"❌ items    : {len(never_do_items(old))} -> {len(never_do_items(new))}")
    print(f"rule lines  : {len(rule_lines(old))} -> {len(rule_lines(new))}")
    print(f"identifiers : {len(old_ids)} -> {len(new_ids)}")

    failed = False
    if triaged:
        print(f"\n· Deliberate, justified changes ({len(set(r for r, _ in triaged))}) — in the triage file:")
        for raw, reason in dict(triaged).items():
            print(f"   {raw[:90]}\n      → {reason}")
    if lost_items:
        failed = True
        print(f"\n✗ LOST/BROKEN NEVER-DO ITEM ({len(lost_items)}):")
        for raw, reason in lost_items:
            print(f"   [{reason}] " + raw[:140])
    if lost_rules:
        failed = True
        print(f"\n✗ LOST/BROKEN RULE LINE ({len(lost_rules)}):")
        for raw, reason in lost_rules:
            print(f"   [{reason}] " + raw[:140])
    if lost_ids:
        # Losing an identifier ALSO BREAKS THE GATE. Some of it may be legitimate
        # (the table name of a finished migration, the old path of a file moved to
        # an archive) but that REQUIRES A DECISION — it never passes silently. An
        # exemption is granted to a VALUE, not to a path, and it is written into
        # the triage file with its reason. When the triage file is emptied the gate
        # must break again.
        triage_all = {}
        try:
            import json
            path = _triage_file()
            if path is not None:
                triage_all = json.loads(path.read_text(encoding="utf-8"))
        except Exception as error:   # if the triage file cannot be read, DO NOT loosen the gate
            print(f"\n✗ The triage file could not be read ({error}) — the gate counts as closed.")
            return 1

        per_file = triage_all.get(sys.argv[2], {}) or triage_all.get("*", {})
        justified = [x for x in lost_ids if x in per_file]
        unjustified = [x for x in lost_ids if x not in per_file]

        if justified:
            print(f"\n· Identifiers moved with a reason ({len(justified)}) — archived:")
            for x in justified:
                print(f"   `{x}` — {per_file[x]}")
        if unjustified:
            failed = True
            print(f"\n✗ IDENTIFIER DROPPED WITHOUT A REASON ({len(unjustified)}):")
            for x in unjustified:
                print("   `" + x + "`")
            print("   → Either it stays in the active file, or it goes into")
            print("     md-gate-triage.json with a reason why it is no longer needed there.")

    if failed:
        print("\nGATE BROKEN — a rule was lost; this simplification is not acceptable.")
        return 1
    print("\n✓ Gate passed: no never-do item and no rule line was dropped.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
