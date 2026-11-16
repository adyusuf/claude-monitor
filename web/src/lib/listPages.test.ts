import { describe, expect, it, vi } from "vitest";
import type { CursorPage } from "../api/types";
import { appendNew, nextByCursor } from "./listPages";

type Row = { id: string };
const rows = (...ids: string[]): Row[] => ids.map((id) => ({ id }));
const key = (r: Row) => r.id;
const page = (ids: string[], nextCursor: string | null): CursorPage<Row> => ({ items: rows(...ids), nextCursor });

describe("appendNew", () => {
  it("adds the rows of the later page after those shown, each once", () => {
    expect(appendNew(rows("a", "b"), rows("b", "c"), key).map(key)).toEqual(["a", "b", "c"]);
  });

  it("gives the rows shown back when the page brings nothing new", () => {
    expect(appendNew(rows("a"), rows("a"), key).map(key)).toEqual(["a"]);
  });
});

describe("nextByCursor", () => {
  it("reads from the start past the rows shown and keeps the cursor it reached", async () => {
    const read = vi.fn(async (cursor: string | null) => (cursor === null ? page(["a", "b"], "k1") : page(["c"], "k2")));
    const next = await nextByCursor(read, { truncated: true, cursor: null }, rows("a", "b"), key);
    expect(next.rows.map(key)).toEqual(["a", "b", "c"]);
    expect(next.state).toEqual({ truncated: true, cursor: "k2" });
    expect(read.mock.calls.map((c) => c[0])).toEqual([null, "k1"]);
  });

  it("reads one page from a kept cursor and says nothing is left on the last page", async () => {
    const read = vi.fn(async () => page(["d"], null));
    const next = await nextByCursor(read, { truncated: true, cursor: "k2" }, rows("a"), key);
    expect(next.rows.map(key)).toEqual(["a", "d"]);
    expect(next.state).toEqual({ truncated: false, cursor: null });
    expect(read).toHaveBeenCalledTimes(1);
  });

  it("stops on a cursor that does not move on instead of reading the same page forever", async () => {
    const read = vi.fn(async () => page(["a"], "k1"));
    const next = await nextByCursor(read, { truncated: true, cursor: "k1" }, rows("a"), key);
    expect(next.rows.map(key)).toEqual(["a"]);
    expect(next.state.truncated).toBe(false);
    expect(read).toHaveBeenCalledTimes(1);
  });

  it("lets a failed read throw to the page, which shows its error", async () => {
    const read = vi.fn(async () => Promise.reject(new Error("server_error")));
    await expect(nextByCursor(read, { truncated: true, cursor: null }, rows("a"), key)).rejects.toThrow("server_error");
  });
});
