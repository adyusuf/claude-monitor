#!/usr/bin/env python3
"""Splits a CLAUDE.md decision log into two layers — by MOVING, never by rewriting.

Usage:
  python3 scripts/md-split.py <claude.md> <section-heading> <detail-file> <relative-link>

⚠️ The canonical copy lives under ~/.claude/scripts/; every project takes a COPY
   and commits it. The three tools are twins — changing one means changing all.

Method:
  · The DETAIL file receives every item **verbatim and in full** (not a single
    line changes) — which makes losing a definition impossible after this step.
  · In the ACTIVE file the item's rule-carrying lines stay as they are; pure
    narrative lines that carry no rule marker drop out and a link to the detail
    takes their place.
  · No sentence is ever paraphrased. Paraphrase is the most common route to loss.

Measured (one project's backend/CLAUDE.md): 87% of the section was rule-carrying
lines and 12% pure narrative. So the SIZE gain of this operation is about 10% —
its real gain is that the detail has a home and the active file turns into a rule
index. Wanting more than that means sacrificing rules, and md-rule-gate.py
refuses it.
"""
import pathlib
import re
import sys

# This MUST be the same pattern as md-rule-gate.py: if we drop here what the gate
# protects, the gate breaks and is right to. If it is changed here for the sake of
# a single source, md-rule-gate.py is updated too.
RULE = re.compile(
    r"PERMANENT|MANDATORY|FORBIDDEN|REQUIRED"
    r"|\bnever\b|\balways\b|\bonly\b|\bmust\b|\bcannot\b|\bcan not\b|\bmay not\b"
    r"|\bshall\b|\brequired\b|\bmandatory\b|\bforbidden\b|\bprohibited\b"
    r"|\bdo not\b|\bdon't\b|\bdoes not\b|\bis not\b|\bare not\b"
    r"|\bno\b|\bnot\b|\bnothing\b|\bevery\b|\beach\b|❌|⚠",
    re.IGNORECASE,
)
# A line carrying a backticked identifier is preserved too: the gate refuses
# identifier loss, and rightly so — a sentence like `MemberTierInfo.HiddenTiers`
# carries no rule marker but IS the rule itself.
IDENTIFIER = re.compile(r"`[^`\n]*[./][^`\n]*`|`[a-z][A-Za-z0-9]*[A-Z][^`\n]*`")
BOLD = re.compile(r"\*\*(.+?)\*\*", re.DOTALL)


def slug(text: str) -> str:
    folded = {"ı": "i", "İ": "i", "ş": "s", "Ş": "s", "ğ": "g", "Ğ": "g",
              "ü": "u", "Ü": "u", "ö": "o", "Ö": "o", "ç": "c", "Ç": "c"}
    text = "".join(folded.get(char, char) for char in text)
    text = re.sub(r"[^a-zA-Z0-9\s-]", "", text).strip().lower()
    return re.sub(r"\s+", "-", text)[:60].strip("-") or "decision"


def title_of(item: str) -> str:
    match = BOLD.search(item)
    if not match:
        return item.lstrip("- ").split("\n")[0][:110]
    return " ".join(match.group(1).split()).rstrip(":").strip()


def items_of(body: str):
    out, current = [], None
    for line in body.split("\n"):
        if re.match(r"^- ", line):
            if current:
                out.append("\n".join(current).rstrip())
            current = [line]
        elif current is not None:
            current.append(line)
    if current:
        out.append("\n".join(current).rstrip())
    return [item for item in out if item.strip()]


def is_kept(line: str) -> bool:
    """Should this line STAY in the active file?"""
    if not line.strip():
        return False
    return bool(RULE.search(line) or IDENTIFIER.search(line))


def main() -> int:
    if len(sys.argv) != 5:
        print(__doc__)
        return 2
    source, heading, detail_path, relative = sys.argv[1:5]
    path = pathlib.Path(source)
    raw = path.read_text(encoding="utf-8")
    lines = raw.split("\n")

    start = end = None
    for index, line in enumerate(lines):
        if line.strip() == heading.strip():
            start = index
        elif start is not None and line.startswith("## "):
            end = index
            break
    if start is None:
        print(f"ERROR: '{heading}' not found.")
        return 1
    end = end if end is not None else len(lines)

    item_list = items_of("\n".join(lines[start + 1:end]))
    if not item_list:
        print("ERROR: no items found.")
        return 1

    used, detail_parts, new_lines = set(), [], [lines[start], ""]
    new_lines += [
        "> Every line below is **a binding rule**. The rationale for each item, how",
        f"> it was discovered and its history live **verbatim** in",
        f"> [{pathlib.Path(detail_path).name}]({relative}) — that file is not loaded",
        "> automatically; read the relevant item there before touching a decision.",
        "> Never loosen a rule without knowing WHY it exists.",
        "",
    ]

    dropped = 0
    for item in item_list:
        title = title_of(item)
        anchor = slug(title)
        base, counter = anchor, 2
        while anchor in used:
            anchor, counter = f"{base}-{counter}", counter + 1
        used.add(anchor)

        # VERBATIM and COMPLETE into the detail file
        detail_parts.append(f"### {title}\n\n{item}\n")

        # Only rule-carrying lines stay in the active file
        kept = [line for line in item.split("\n") if is_kept(line)]
        dropped += len(item.split("\n")) - len(kept)
        if not kept:
            kept = [item.split("\n")[0]]
        new_lines += kept
        new_lines.append(f"  · [rationale & history]({relative}#{anchor})")

    new_lines.append("")

    detail = pathlib.Path(detail_path)
    detail.parent.mkdir(parents=True, exist_ok=True)
    detail.write_text(
        "# Permanent structural decisions — full text\n\n"
        f"> The decision log of `{path.name}`. **It is not loaded automatically.**\n"
        "> The active file keeps each decision's rule lines; the rationale, the\n"
        "> story of the trap, the test lock and the history live here — moved\n"
        "> **verbatim** from the active file, with no paraphrase.\n\n" + "\n".join(detail_parts),
        encoding="utf-8")

    path.write_text("\n".join(lines[:start] + new_lines + lines[end:]), encoding="utf-8")

    print(f"items           : {len(item_list)}")
    print(f"lines dropped   : {dropped} (pure narrative — all of it is in the detail file)")
    print(f"detail file     : {detail} ({len(detail.read_text(encoding='utf-8'))/1024:.0f} KB)")
    print(f"active file     : {len(raw)/1024:.0f} KB -> {len(path.read_text(encoding='utf-8'))/1024:.0f} KB")
    return 0


if __name__ == "__main__":
    sys.exit(main())
