#!/usr/bin/env python3
"""Pack the release package where `zip` is not installed (Git Bash on the Windows server has none).

    python3 scripts/zip_package.py <stage folder> <out.zip> <member> [<member> ...]

Each member (a folder or a file in the stage) goes in with its path relative to the stage, folders (also empty ones)
included, like `cd <stage> && zip -r -X <out.zip> <member> ...`. A missing member stops the run and leaves no package.
"""
import sys
import zipfile
from pathlib import Path


def entries(stage: Path, member: str):
    root = stage / member
    if not root.exists():
        raise FileNotFoundError(f"{member} is not in the stage {stage}")
    yield root
    if root.is_dir():
        yield from sorted(root.rglob("*"))


def main(argv: list[str]) -> int:
    if len(argv) < 4:
        print("usage: zip_package.py <stage> <out.zip> <member> [<member> ...]", file=sys.stderr)
        return 2
    stage, out, members = Path(argv[1]), Path(argv[2]), argv[3:]
    partial = out.with_name(out.name + ".partial")
    try:
        with zipfile.ZipFile(partial, "w", zipfile.ZIP_DEFLATED) as z:
            for member in members:
                for path in entries(stage, member):
                    z.write(path, path.relative_to(stage).as_posix())
    except FileNotFoundError as e:
        partial.unlink(missing_ok=True)
        print(str(e), file=sys.stderr)
        return 1
    partial.replace(out)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
