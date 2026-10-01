"""App mode for the live board (docs/live-board.md §2e): the web app manifest and the
icons the browser needs to install the board as its own window. The icons are drawn here
(standard library only), so no binary file lives in the repository."""
from __future__ import annotations

import struct
import zlib
from functools import lru_cache

from board_config import (APP_BACKGROUND, APP_ICON_SIZES, APP_NAME, APP_SHORT_NAME,
                          APP_THEME_COLOR)

ICON_PATH = "/icon-{}.png"
SVG_ICON_PATH = "/icon.svg"
COLUMNS = 3            # the icon shows a three-column board
MARGIN, GAP = 0.2, 0.06  # as fractions of the icon's side
COLUMN_FILL = (255, 255, 255)


def manifest() -> dict:
    icons = [{"src": ICON_PATH.format(n), "sizes": f"{n}x{n}", "type": "image/png",
              "purpose": "any"} for n in APP_ICON_SIZES]
    icons.append({"src": SVG_ICON_PATH, "sizes": "any", "type": "image/svg+xml"})
    return {"name": APP_NAME, "short_name": APP_SHORT_NAME, "start_url": "/", "scope": "/",
            "display": "standalone", "background_color": APP_BACKGROUND,
            "theme_color": APP_THEME_COLOR, "icons": icons}


def _rgb(hex_color: str) -> tuple:
    return tuple(int(hex_color[i:i + 2], 16) for i in (1, 3, 5))


def _chunk(kind: bytes, data: bytes) -> bytes:
    return (struct.pack(">I", len(data)) + kind + data
            + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF))


@lru_cache(maxsize=2 * len(APP_ICON_SIZES))
def icon_png(size: int, alpha: bool = False) -> bytes:
    """A size x size PNG: the theme colour with three white board columns. RGB, or RGBA with
    alpha=True (the desktop window's icon, desktop/make_icon.py, must have an alpha channel)."""
    if size not in APP_ICON_SIZES:
        raise ValueError(f"no icon of size {size}")
    opaque = b"\xff" if alpha else b""
    bg, fg = bytes(_rgb(APP_THEME_COLOR)) + opaque, bytes(COLUMN_FILL) + opaque
    lo, hi = int(size * MARGIN), size - int(size * MARGIN)
    gap = int(size * GAP)
    col = (hi - lo - gap * (COLUMNS - 1)) // COLUMNS
    starts = [lo + i * (col + gap) for i in range(COLUMNS)]
    row_band = b"".join(fg if any(s <= x < s + col for s in starts) else bg for x in range(size))
    row_plain = bg * size
    raw = b"".join(b"\x00" + (row_band if lo <= y < hi else row_plain) for y in range(size))
    header = struct.pack(">IIBBBBB", size, size, 8, 6 if alpha else 2, 0, 0, 0)  # 8-bit RGB(A)
    return (b"\x89PNG\r\n\x1a\n" + _chunk(b"IHDR", header)
            + _chunk(b"IDAT", zlib.compress(raw, 9)) + _chunk(b"IEND", b""))


def icon_svg() -> bytes:
    """The same icon as SVG, for browsers that take a scalable one."""
    step = 100 * (1 - 2 * MARGIN - GAP * (COLUMNS - 1)) / COLUMNS
    left, top, height = 100 * MARGIN, 100 * MARGIN, 100 * (1 - 2 * MARGIN)
    cols = "".join(f'<rect x="{left + i * (step + 100 * GAP):.1f}" y="{top:.1f}" width="{step:.1f}" '
                   f'height="{height:.1f}" rx="3"/>' for i in range(COLUMNS))
    fill = "#%02x%02x%02x" % COLUMN_FILL
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">'
            f'<rect width="100" height="100" rx="18" fill="{APP_THEME_COLOR}"/>'
            f'<g fill="{fill}">{cols}</g></svg>').encode("utf-8")


def icon_size(path: str) -> int | None:
    """The size an /icon-<n>.png path asks for, if it is one we draw."""
    for n in APP_ICON_SIZES:
        if path == ICON_PATH.format(n):
            return n
    return None
