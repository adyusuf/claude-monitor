"""Writes icons/icon.png from the board's own drawn icon, so no binary file is committed.
Run it before a build:  python3 desktop/make_icon.py   (docs/live-board.md §2g)."""
from __future__ import annotations

import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "scripts" / "board"))

from board_app import icon_png  # noqa: E402

ICON_SIZE = 512
TARGET = HERE / "icons" / "icon.png"


def main() -> int:
    TARGET.parent.mkdir(exist_ok=True)
    TARGET.write_bytes(icon_png(ICON_SIZE, alpha=True))
    print(TARGET)
    return 0


if __name__ == "__main__":
    sys.exit(main())
