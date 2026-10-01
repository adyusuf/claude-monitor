"""Tokens and dollar cost read from Claude Code transcripts (docs/live-board.md §2c).

A transcript is JSONL; every `assistant` entry carries message.usage and message.model.
Streaming writes one message several times, so entries are deduplicated by message.id
(the last write wins). Files are read incrementally: each call parses only the bytes
added since the previous call. Prices live in board_config.py only; a model that is not
priced there makes the cost "cannot be measured" (None) — never a guess.
"""
from __future__ import annotations

import json
import threading
from datetime import datetime
from pathlib import Path

from board_config import (CACHE_WRITE_1H_X, CACHE_WRITE_5M_X, CONTEXT_WINDOW, MODEL_SUFFIX_CHARS,
                          PRICING_USD_PER_MTOK, TITLE_ENTRIES, TITLE_MAX, TOKENS_PER_MTOK)

TOKEN_KEYS = ("input", "output", "cache_read", "cache_write_5m", "cache_write_1h")


def model_key(model: str | None, table: dict) -> str | None:
    """The table key a transcript's model id prices as: an exact match, or the key followed
    by a suffix such as a date ("claude-haiku-4-5-20251001") or "[1m]"."""
    if not model:
        return None
    for key in table:
        if model == key or (model.startswith(key) and model[len(key)] in MODEL_SUFFIX_CHARS):
            return key
    return None


def tokens_of(usage: dict) -> tuple:
    """(input, output, cache read, cache write 5m, cache write 1h) from one message.usage."""
    cc = usage.get("cache_creation") or {}
    w5, w1 = cc.get("ephemeral_5m_input_tokens"), cc.get("ephemeral_1h_input_tokens")
    if w5 is None and w1 is None:  # older transcripts: one undivided figure, priced as 5m
        w5, w1 = usage.get("cache_creation_input_tokens"), 0
    return (usage.get("input_tokens") or 0, usage.get("output_tokens") or 0,
            usage.get("cache_read_input_tokens") or 0, w5 or 0, w1 or 0)


def cost_of(model: str | None, tok: tuple) -> float | None:
    key = model_key(model, PRICING_USD_PER_MTOK)
    if key is None:
        return None
    p_in, p_out, p_read = PRICING_USD_PER_MTOK[key]
    i, o, r, w5, w1 = tok
    return (i * p_in + o * p_out + r * p_read
            + w5 * p_in * CACHE_WRITE_5M_X + w1 * p_in * CACHE_WRITE_1H_X) / TOKENS_PER_MTOK


def epoch(stamp: str | None) -> float | None:
    if not stamp:
        return None
    try:
        return datetime.fromisoformat(stamp.replace("Z", "+00:00")).timestamp()
    except ValueError:
        return None


class Transcript:
    """What one transcript file has cost so far, kept up to date incrementally."""

    def __init__(self, path: Path):
        self.path = path
        self.offset = 0
        self.messages: dict = {}  # message id -> (epoch | None, model, tokens)
        self.context = None       # (model, tokens) of the last main-thread call
        self.titles: dict = {}    # title entry type -> its last value (see title)

    def update(self) -> "Transcript":
        try:
            size = self.path.stat().st_size
        except OSError:
            return self
        if size < self.offset:  # rewritten or truncated: start again
            self.__init__(self.path)
        if size == self.offset:
            return self
        with self.path.open("rb") as f:
            f.seek(self.offset)
            chunk = f.read(size - self.offset)
        end = chunk.rfind(b"\n") + 1  # a half-written last line waits for the next call
        for line in chunk[:end].splitlines():
            self._take(line)
        self.offset += end
        return self

    @property
    def title(self) -> str | None:
        return next((self.titles[t] for t in TITLE_ENTRIES if t in self.titles), None)

    def _take(self, line: bytes) -> None:
        try:
            entry = json.loads(line)
        except ValueError:
            return
        if not isinstance(entry, dict):
            return
        field = TITLE_ENTRIES.get(entry.get("type"))
        if field:  # the last one written wins: a session can be renamed
            value = entry.get(field)
            if isinstance(value, str) and value.strip():
                self.titles[entry["type"]] = value.strip()[:TITLE_MAX]
            return
        if entry.get("type") != "assistant":
            return
        msg = entry.get("message") or {}
        usage = msg.get("usage")
        if not isinstance(usage, dict):
            return
        tok = tokens_of(usage)
        model = msg.get("model")
        self.messages[msg.get("id") or entry.get("uuid")] = (epoch(entry.get("timestamp")), model, tok)
        if not entry.get("isSidechain"):
            self.context = (model, tok[0] + tok[2] + tok[3] + tok[4])


def summarize(transcripts: list, since: float | None = None, start: float | None = None,
              end: float | None = None) -> dict:
    """Totals over several transcripts. cost is None when any non-empty message used a model
    that has no price; `window_cost` counts only messages at or after `since` (epoch);
    `first` is the earliest message (epoch). With `start`/`end` only the messages inside that
    time window are counted at all (a message without a timestamp is outside any window)."""
    tokens = dict.fromkeys(TOKEN_KEYS, 0)
    cost, window, unpriced, count, first = 0.0, 0.0, set(), 0, None
    for tr in transcripts:
        for at, model, tok in tr.messages.values():
            if (start is not None or end is not None) and (
                    at is None or (start is not None and at < start) or (end is not None and at > end)):
                continue
            count += 1
            if at is not None and (first is None or at < first):
                first = at
            for k, n in zip(TOKEN_KEYS, tok):
                tokens[k] += n
            c = cost_of(model, tok)
            if c is None:
                if any(tok):  # an empty entry (e.g. model "<synthetic>") costs nothing
                    unpriced.add(model or "?")
                continue
            cost += c
            if since is not None and at is not None and at >= since:
                window += c
    measured = not unpriced
    return {"tokens": tokens, "messages": count, "unpriced": sorted(unpriced), "first": first,
            "cost": round(cost, 4) if measured else None,
            "window_cost": round(window, 4) if measured else None}


def context_use(tr: Transcript | None) -> dict | None:
    """The last main-thread call's prompt size against the model's window; None if unknown."""
    if tr is None or tr.context is None:
        return None
    model, used = tr.context
    key = model_key(model, CONTEXT_WINDOW)
    window = CONTEXT_WINDOW[key] if key else None
    return {"model": model, "tokens": used, "window": window,
            "ratio": used / window if window else None}  # unrounded: the 80% check reads it


class Cache:
    """One Transcript per path, shared by the server's request threads."""

    def __init__(self):
        self._items: dict = {}
        self._lock = threading.Lock()

    def get(self, path) -> Transcript | None:
        if not path:
            return None
        p = Path(path)
        with self._lock:
            tr = self._items.get(str(p))
            if tr is None:
                if not p.is_file():
                    return None
                tr = self._items[str(p)] = Transcript(p)
            return tr.update()
