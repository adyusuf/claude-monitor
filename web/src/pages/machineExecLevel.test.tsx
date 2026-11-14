import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { ExecLevelView } from "../api/types";
import { en } from "../i18n/en";
import { approvalKeys, SIGNING } from "../lib/approvalKey";
import { execLevelPayload } from "../lib/execLevel";
import { mockApi, type Call } from "../test/helpers";
import { machine, renderPlain } from "../test/remote-fixtures";
import { MachineExecLevel } from "./MachineExecLevel";

const NOW = new Date("2026-11-11T12:00:00.500Z");
const DEADLINE = "2026-11-11T12:05:00.000Z"; // five minutes on, in whole seconds
const copy = en.machines.execLevel;

beforeEach(async () => {
  vi.useFakeTimers({ toFake: ["Date"] });
  vi.setSystemTime(NOW);
  await approvalKeys.forget();
});
afterEach(async () => {
  vi.useRealTimers();
  await approvalKeys.forget();
  vi.restoreAllMocks();
});

const view = (over: Partial<ExecLevelView> = {}): ExecLevelView => ({ agentId: "a1", execLevel: "argv", sequence: 3, report: null, reportAt: null, pending: null, ...over });
const puts = (calls: Call[]) => calls.filter((c) => c.method === "PUT" && c.path === "/agents/a1/exec-level");
const choose = async (label: string) => userEvent.selectOptions(await screen.findByLabelText(copy.label), label);
/** Presses the save button once it is enabled: the state read from the API has arrived and a change is chosen (and a code typed, when one is asked for). */
const save = async () => {
  const button = screen.getByRole("button", { name: copy.save });
  await waitFor(() => expect(button).toBeEnabled());
  await userEvent.click(button);
};
const bytes = (base64: string) => Uint8Array.from(atob(base64), (c) => c.charCodeAt(0));
const verifies = async (publicKey: string, signature: string, payload: string) =>
  crypto.subtle.verify({ name: "ECDSA", hash: "SHA-256" }, await crypto.subtle.importKey("spki", bytes(publicKey), SIGNING, true, ["verify"]), bytes(signature), new TextEncoder().encode(payload));

function open(state: ExecLevelView, over: Record<string, Parameters<typeof mockApi>[0][string]> = {}, owned = true) {
  const onChanged = vi.fn();
  const calls = mockApi({ "GET /agents/a1/exec-level": { body: state }, "PUT /agents/a1/exec-level": { body: state }, ...over });
  renderPlain(<MachineExecLevel agentId="a1" view={machine({ execLevel: state.execLevel })} owned={owned} onChanged={onChanged} />);
  return { calls, onChanged };
}

describe("who sees the controls", () => {
  it("shows a person who does not own the machine its level and a sentence that only the owner changes it, with no controls", async () => {
    open(view(), {}, false);

    expect(await screen.findByText(copy.ownerOnly)).toBeInTheDocument();
    expect(screen.getByText(copy.current.replace("{level}", copy.argv))).toBeInTheDocument();
    expect(screen.queryByLabelText(copy.label)).toBeNull();
    expect(screen.queryByRole("button", { name: copy.save })).toBeNull();
  });

  it("keeps the save button disabled until a level other than the current one is chosen", async () => {
    open(view());
    await choose(copy.argv);
    expect(screen.getByRole("button", { name: copy.save })).toBeDisabled();

    await choose(copy.off);

    await waitFor(() => expect(screen.getByRole("button", { name: copy.save })).toBeEnabled());
  });
});

describe("lowering the level", () => {
  it("sends the level, the next sequence and a five-minute deadline with no signature, then says it was sent and tells the page", async () => {
    const { calls, onChanged } = open(view({ execLevel: "argv", sequence: 3 }));
    await choose(copy.off);

    await save();

    expect(await screen.findByText(copy.saved)).toBeInTheDocument();
    expect(puts(calls)).toHaveLength(1);
    expect(puts(calls)[0]!.body).toEqual({ level: "off", sequence: 4, notAfter: DEADLINE });
    expect(onChanged).toHaveBeenCalledTimes(1);
  });

  it("never reads the approval key for a lowering, so a browser without one can still turn remote runs down", async () => {
    const sign = vi.spyOn(approvalKeys, "sign");
    const { calls } = open(view({ execLevel: "shell" }));
    await choose(copy.argv);

    await save();

    await screen.findByText(copy.saved);
    expect(sign).not.toHaveBeenCalled();
    expect(puts(calls)[0]!.body).not.toHaveProperty("signature");
  });
});

describe("raising the level", () => {
  it("signs the exact change with this browser's key and sends the key id and the signature with it", async () => {
    const key = await approvalKeys.generate("laptop");
    const { calls } = open(view({ execLevel: "off", sequence: 7 }));
    await choose(copy.shell);

    await save();

    await screen.findByText(copy.saved);
    const body = puts(calls)[0]!.body as { level: string; sequence: number; notAfter: string; keyId: string; signature: string };
    expect([body.level, body.sequence, body.notAfter, body.keyId]).toEqual(["shell", 8, DEADLINE, key.fingerprint]);
    expect(await verifies(key.publicKey, body.signature, execLevelPayload("a1", "shell", 8, DEADLINE))).toBe(true);
    expect(await verifies(key.publicKey, body.signature, execLevelPayload("a1", "argv", 8, DEADLINE))).toBe(false);
  });

  it("sends nothing and points to the account page when this browser holds no approval key", async () => {
    const { calls, onChanged } = open(view({ execLevel: "off" }));
    await choose(copy.argv);

    await save();

    expect(await screen.findByText(copy.noKey, { exact: false })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: copy.makeKey })).toHaveAttribute("href", "/account");
    expect(puts(calls)).toHaveLength(0);
    expect(onChanged).not.toHaveBeenCalled();
    expect(screen.queryByText(copy.saved)).toBeNull();
  });
});

describe("when the API refuses", () => {
  it("reloads the stored state after a 409 so the next save follows the new sequence, and keeps the chosen level", async () => {
    let reads = 0;
    let writes = 0;
    const { calls } = open(view({ sequence: 3 }), {
      "GET /agents/a1/exec-level": () => ({ body: view({ sequence: reads++ === 0 ? 3 : 9 }) }),
      "PUT /agents/a1/exec-level": () => (writes++ === 0 ? { status: 409, body: { title: "exec_level_changed" } } : { body: view({ sequence: 10 }) }),
    });
    await choose(copy.off);

    await save();

    expect(await screen.findByText(en.errors.exec_level_changed)).toBeInTheDocument();
    expect(screen.getByLabelText(copy.label)).toHaveValue("off");
    await save();
    await screen.findByText(copy.saved);
    expect(puts(calls).map((c) => (c.body as { sequence: number }).sequence)).toEqual([4, 10]);
  });

  it("asks for a two-step code when the API wants one, keeps the choice and sends the same change with the code", async () => {
    let writes = 0;
    const { calls } = open(view({ sequence: 3 }), {
      "PUT /agents/a1/exec-level": () => (writes++ === 0 ? { status: 403, body: { title: "reauth_required" } } : { body: view({ sequence: 4 }) }),
    });
    await choose(copy.off);
    await save();
    const field = await screen.findByLabelText(en.remote.codeLabel);
    expect(screen.getByRole("button", { name: copy.save })).toBeDisabled();
    expect(screen.getByLabelText(copy.label)).toHaveValue("off");

    await userEvent.type(field, "123456");
    await save();

    await screen.findByText(copy.saved);
    expect(puts(calls).map((c) => c.body)).toEqual([{ level: "off", sequence: 4, notAfter: DEADLINE }, { level: "off", sequence: 4, notAfter: DEADLINE, code: "123456" }]);
  });

  it("shows another refusal as its sentence and does not tell the page it changed", async () => {
    const { onChanged } = open(view(), { "PUT /agents/a1/exec-level": { status: 400, body: { title: "exec_level_invalid" } } });
    await choose(copy.off);

    await save();

    expect(await screen.findByText(en.errors.exec_level_invalid)).toBeInTheDocument();
    expect(onChanged).not.toHaveBeenCalled();
  });
});

describe("what the machine did with the latest change", () => {
  it.each([
    ["applied", copy.state.applied.replace("{level}", copy.argv)],
    ["signature_refused", copy.state.signature_refused],
    ["no_pinned_key", copy.state.no_pinned_key],
    ["expired", copy.state.expired],
    ["needs_admin", copy.state.needs_admin],
    ["invalid", copy.state.invalid],
    ["stale", copy.state.stale],
    ["something-new", copy.state.unknown],
  ])("says what the machine reported for %s", async (state, sentence) => {
    open(view({ sequence: 4, report: { sequence: 4, state } }));

    expect(await screen.findByText(sentence)).toBeInTheDocument();
    expect(screen.queryByText(copy.waiting.replace("{sequence}", "4"))).toBeNull();
  });

  it("shows the time of the report after the sentence", async () => {
    open(view({ sequence: 4, report: { sequence: 4, state: "applied" }, reportAt: new Date(2026, 10, 11, 9, 30).toISOString() }));

    expect(await screen.findByText(`${copy.state.applied.replace("{level}", copy.argv)} (11/11/2026 09:30)`)).toBeInTheDocument();
  });

  it.each([
    ["no report yet", null],
    ["a report of an older change", { sequence: 4, state: "applied" }],
  ])("says the change is waiting for the machine to read it when there is %s", async (_name, report) => {
    open(view({ sequence: 5, report }));

    expect(await screen.findByText(copy.waiting.replace("{sequence}", "5"))).toBeInTheDocument();
    expect(screen.queryByText(copy.state.applied.replace("{level}", copy.argv))).toBeNull();
  });

  it("shows neither a report nor a waiting sentence before any change was asked for", async () => {
    open(view({ sequence: 0, report: { sequence: 0, state: "stale" } }));
    await choose(copy.off);
    await waitFor(() => expect(screen.getByRole("button", { name: copy.save })).toBeEnabled()); // the state has been read

    expect(screen.queryByText(copy.state.stale)).toBeNull();
    expect(screen.queryByText(copy.waiting.replace("{sequence}", "0"))).toBeNull();
  });
});
