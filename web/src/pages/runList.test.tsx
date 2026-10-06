import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import type { RunStatus, WebRunView } from "../api/types";
import { config } from "../config";
import { en } from "../i18n/en";
import { mockApi } from "../test/helpers";
import { renderPlain, run } from "../test/remote-fixtures";
import { RunList, RunStatusChip } from "./RunList";
import { RunOutput } from "./RunOutput";

function list(runs: WebRunView[], over: { hasMore?: boolean; onMore?: () => void; onChanged?: () => void; version?: number } = {}) {
  const onChanged = over.onChanged ?? vi.fn();
  const onMore = over.onMore ?? vi.fn();
  renderPlain(<RunList runs={runs} ws="w1" version={over.version ?? 0} hasMore={over.hasMore ?? false} onMore={onMore} onChanged={onChanged} />);
  return { onChanged, onMore };
}

describe("run status chips", () => {
  const cases: [RunStatus | string, string, string][] = [
    ["pending_approval", en.remote.status.pending_approval, "open"], ["approved", en.remote.status.approved, "open"],
    ["delivered", en.remote.status.delivered, "open"], ["running", en.remote.status.running, "open"],
    ["succeeded", en.remote.status.succeeded, "ok"], ["failed", en.remote.status.failed, "bad"],
    ["timed_out", en.remote.status.timed_out, "bad"], ["denied", en.remote.status.denied, "bad"],
    ["expired", en.remote.status.expired, "quiet"], ["cancelled", en.remote.status.cancelled, "quiet"],
    ["something_new", en.remote.status.unknown, "quiet"],
  ];
  it.each(cases)("shows %s as %s in the %s tone", (status, label, tone) => {
    renderPlain(<RunStatusChip status={status} />);
    const chip = screen.getByText(label);
    expect(chip).toHaveClass(`run-status-${tone}`);
  });
});

describe("the run list", () => {
  it("says nothing has run yet for an empty history", () => {
    list([]);
    expect(screen.getByText(en.remote.noRuns)).toBeInTheDocument();
  });

  it("shows one row per run with its chip, one-line command, requester and a joined argv preview", () => {
    list([run({ id: "r1", status: "succeeded", argv: ["/bin/echo", "a", "b"], canDecide: false }), run({ id: "r2", status: "failed", mode: "shell", argv: null, shellCommand: "make all" })]);
    const rows = screen.getAllByRole("listitem");
    expect(rows).toHaveLength(2);
    expect(within(rows[0]!).getByText(en.remote.status.succeeded)).toBeInTheDocument();
    expect(within(rows[0]!).getByText("/bin/echo a b")).toBeInTheDocument();
    expect(within(rows[1]!).getByText("make all")).toBeInTheDocument();
    expect(within(rows[0]!).getByText(/Diğer Kişi · laptop-9/)).toBeInTheDocument();
  });

  it("hides the command of a run the user may not see, and offers neither cancel nor output", async () => {
    mockApi({});
    list([run({ visible: false, argv: null, status: "running", outputBytes: 500 })]);
    expect(screen.getByText(en.remote.commandHidden)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Cancel" })).toBeNull();
    await userEvent.click(screen.getByRole("button", { expanded: false }));
    expect(screen.queryByLabelText(en.remote.output)).toBeNull();
    expect(screen.queryByText(en.remote.noOutput)).toBeNull();
  });

  it("offers Cancel for approved, delivered and running runs only", () => {
    const statuses: RunStatus[] = ["pending_approval", "approved", "delivered", "running", "succeeded", "failed", "timed_out", "denied", "expired", "cancelled"];
    list(statuses.map((status, i) => run({ id: `r${i}`, status, canDecide: false })));
    const withCancel = screen.getAllByRole("listitem").filter((li) => within(li).queryByRole("button", { name: "Cancel" }));
    expect(withCancel.map((li) => within(li).getByText(/^(Waiting|Approved|Sent to the machine|Running|Succeeded|Failed|Timed out|Denied|Expired|Cancelled)$/).textContent))
      .toEqual([en.remote.status.approved, en.remote.status.delivered, en.remote.status.running]);
  });

  it("cancels a run through the API and reports the change", async () => {
    const calls = mockApi({ "POST /runs/r1/cancel": { status: 204 } });
    const { onChanged } = list([run({ status: "running" })]);
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1));
    expect(calls.map((c) => `${c.method} ${c.path}`)).toEqual(["POST /runs/r1/cancel"]);
  });

  it("shows a failed cancel and does not report a change", async () => {
    mockApi({ "POST /runs/r1/cancel": { status: 409, body: { title: "not_pending" } } });
    const { onChanged } = list([run({ status: "approved" })]);
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(await screen.findByText(en.errors.not_pending)).toBeInTheDocument();
    expect(onChanged).not.toHaveBeenCalled();
  });

  it("expands to the full facts, exit code and error, collapses again", async () => {
    list([run({ status: "failed", argv: ["/bin/false"], exitCode: 3, error: "boom", canDecide: false })]);
    const toggle = screen.getByRole("button", { expanded: false });
    expect(screen.queryByText(en.remote.cwd)).toBeNull();
    await userEvent.click(toggle);
    expect(toggle).toHaveAttribute("aria-expanded", "true");
    expect(screen.getByText(en.remote.cwd)).toBeInTheDocument();
    expect(screen.getByText("Exit code: 3")).toBeInTheDocument();
    expect(screen.getByText(/Message: boom/)).toBeInTheDocument();
    await userEvent.click(toggle);
    expect(screen.queryByText(en.remote.cwd)).toBeNull();
  });

  it("shows exit code 0 (zero is a real value) and nothing when there is none", async () => {
    list([run({ id: "r1", status: "succeeded", exitCode: 0, canDecide: false }), run({ id: "r2", status: "running", exitCode: null, canDecide: false })]);
    for (const b of screen.getAllByRole("button", { expanded: false })) await userEvent.click(b);
    expect(screen.getAllByText(/^Exit code:/)).toHaveLength(1);
    expect(screen.getByText("Exit code: 0")).toBeInTheDocument();
  });

  it("loads no output for a run that produced none", async () => {
    const calls = mockApi({});
    list([run({ outputBytes: 0, status: "succeeded" })]);
    await userEvent.click(screen.getByRole("button", { expanded: false }));
    expect(calls).toHaveLength(0);
  });

  it("offers More only when there are older runs", async () => {
    const { onMore } = list([run()], { hasMore: true });
    await userEvent.click(screen.getByRole("button", { name: en.sessions.more }));
    expect(onMore).toHaveBeenCalledTimes(1);
  });

  it("offers no More for a complete history", () => {
    list([run()], { hasMore: false });
    expect(screen.queryByRole("button", { name: en.sessions.more })).toBeNull();
  });
});

describe("run output", () => {
  const page = (chunks: { seq: number; stream?: string; body: string; gapBefore?: boolean }[], nextSeq: number, done: boolean) => ({
    chunks: chunks.map((c) => ({ stream: "stdout", gapBefore: false, ...c })), nextSeq, done,
  });

  it("loads the first page from the start, shows stderr tinted and stdout plain", async () => {
    const calls = mockApi({
      "GET /runs/r1/output": { body: page([{ seq: 0, body: "hello\n" }, { seq: 1, stream: "stderr", body: "warn\n" }], 2, true) },
    });
    renderPlain(<RunOutput runId="r1" truncated={false} version={0} />);
    const out = await screen.findByLabelText(en.remote.output);
    await waitFor(() => expect(out).toHaveTextContent("hello"));
    expect(calls[0]!.path).toBe(`/runs/r1/output?after=-1&limit=${config.outputPageSize}`);
    expect(screen.getByText("warn", { exact: false })).toHaveClass("out-err");
    expect(screen.getByText("hello", { exact: false })).not.toHaveClass("out-err");
    expect(screen.queryByRole("button", { name: en.remote.outputMore })).toBeNull();
  });

  it("asks for the next page after nextSeq and appends it without duplicating a chunk", async () => {
    const calls = mockApi({
      "GET /runs/r1/output": (c) => c.path.includes("after=-1")
        ? { body: page([{ seq: 0, body: "one|" }, { seq: 1, body: "two|" }], 2, false) }
        : { body: page([{ seq: 1, body: "two|" }, { seq: 2, body: "three|" }], 3, true) },
    });
    renderPlain(<RunOutput runId="r1" truncated={false} version={0} />);
    await userEvent.click(await screen.findByRole("button", { name: en.remote.outputMore }));
    await waitFor(() => expect(screen.getByLabelText(en.remote.output)).toHaveTextContent("one|two|three|"));
    expect(calls.map((c) => c.path)).toEqual([`/runs/r1/output?after=-1&limit=${config.outputPageSize}`, `/runs/r1/output?after=2&limit=${config.outputPageSize}`]);
    expect(screen.queryByRole("button", { name: en.remote.outputMore })).toBeNull();
  });

  it("marks a gap before a chunk", async () => {
    mockApi({ "GET /runs/r1/output": { body: page([{ seq: 0, body: "a" }, { seq: 5, body: "b", gapBefore: true }], 6, true) } });
    renderPlain(<RunOutput runId="r1" truncated={false} version={0} />);
    expect(await screen.findByText(new RegExp(en.remote.outputGap))).toHaveClass("out-gap");
    expect(screen.getAllByText(new RegExp(en.remote.outputGap))).toHaveLength(1);
  });

  it("makes control and direction characters in output visible", async () => {
    mockApi({ "GET /runs/r1/output": { body: page([{ seq: 0, body: "ok\u001b[2J‮" }], 1, true) } });
    renderPlain(<RunOutput runId="r1" truncated={false} version={0} />);
    await waitFor(() => expect(screen.getByLabelText(en.remote.output)).toHaveTextContent("ok\\u001B[2J\\u202E"));
  });

  it("notes truncated output, and only then", async () => {
    mockApi({ "GET /runs/r1/output": { body: page([{ seq: 0, body: "x" }], 1, true) } });
    const { unmount } = renderPlain(<RunOutput runId="r1" truncated={true} version={0} />);
    expect(await screen.findByText(en.remote.outputTruncated)).toBeInTheDocument();
    unmount();
    renderPlain(<RunOutput runId="r1" truncated={false} version={0} />);
    await screen.findByLabelText(en.remote.output);
    expect(screen.queryByText(en.remote.outputTruncated)).toBeNull();
  });

  it("says there is no output for an empty first page", async () => {
    mockApi({ "GET /runs/r1/output": { body: page([], 0, true) } });
    renderPlain(<RunOutput runId="r1" truncated={false} version={0} />);
    expect(await screen.findByText(en.remote.noOutput)).toBeInTheDocument();
    expect(screen.queryByLabelText(en.remote.output)).toBeNull();
  });

  it("shows a failed read, and reads again from the same place when the version changes", async () => {
    let fail = true;
    const calls = mockApi({
      "GET /runs/r1/output": () => (fail ? { status: 500 } : { body: page([{ seq: 0, body: "late" }], 1, true) }),
    });
    const { rerender } = renderPlain(<RunOutput runId="r1" truncated={false} version={0} />);
    expect(await screen.findByRole("alert")).toBeInTheDocument();
    expect(screen.queryByText(en.remote.noOutput)).toBeNull();
    fail = false;
    rerender(<RunOutput runId="r1" truncated={false} version={1} />);
    await waitFor(() => expect(screen.getByLabelText(en.remote.output)).toHaveTextContent("late"));
    expect(screen.queryByRole("alert")).toBeNull();
    expect(calls).toHaveLength(2);
  });

  it("is loaded by expanding a visible run that has output", async () => {
    const calls = mockApi({ "GET /runs/r1/output": { body: page([{ seq: 0, body: "from the run" }], 1, true) } });
    list([run({ status: "succeeded", outputBytes: 12, outputTruncated: true, canDecide: false })]);
    await userEvent.click(screen.getByRole("button", { expanded: false }));
    await waitFor(() => expect(screen.getByLabelText(en.remote.output)).toHaveTextContent("from the run"));
    expect(screen.getByText(en.remote.outputTruncated)).toBeInTheDocument();
    expect(calls).toHaveLength(1);
  });
});
