import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { en } from "../i18n/en";
import { approvalKeys } from "../lib/approvalKey";
import { mockApi } from "../test/helpers";
import { renderPlain, run } from "../test/remote-fixtures";
import { ApprovalKeysCard } from "./ApprovalKeysCard";
import { RunApproval } from "./RunApproval";

beforeEach(async () => approvalKeys.forget());
afterEach(async () => {
  await approvalKeys.forget();
  vi.restoreAllMocks();
});

const listed = (fingerprint: string) => ({ id: "k1", name: "Mine", fingerprint, publicKey: "x", createdAt: new Date().toISOString(), retiredAt: null });

describe("the approval keys card when something fails", () => {
  it("shows the error and makes no registration when the browser cannot make a key", async () => {
    vi.spyOn(approvalKeys, "generate").mockRejectedValue(new Error("no webcrypto"));
    const calls = mockApi({ "GET /me/approval-keys": { body: [] }, "POST /me/approval-keys": { status: 201, body: {} } });
    renderPlain(<ApprovalKeysCard />);
    await userEvent.click(await screen.findByRole("button", { name: en.approvals.make }));
    expect(await screen.findByRole("alert")).toHaveTextContent("The server cannot be reached.");
    expect(calls.some((c) => c.method === "POST")).toBe(false);
  });

  it("shows the API's refusal when the registration fails for another reason than a missing code", async () => {
    mockApi({ "GET /me/approval-keys": { body: [] }, "POST /me/approval-keys": { status: 500, body: {} } });
    renderPlain(<ApprovalKeysCard />);
    await userEvent.click(await screen.findByRole("button", { name: en.approvals.make }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Something went wrong on the server.");
  });

  it("shows the error when the registered keys cannot be read", async () => {
    mockApi({ "GET /me/approval-keys": { status: 500, body: {} } });
    renderPlain(<ApprovalKeysCard />);
    expect(await screen.findByRole("alert")).toHaveTextContent("Something went wrong on the server.");
  });

  it("shows the error and keeps this browser's key when retiring it fails", async () => {
    const key = await approvalKeys.generate("Mine");
    mockApi({ "GET /me/approval-keys": { body: [listed(key.fingerprint)] }, "POST /me/approval-keys/k1/retire": { status: 500, body: {} } });
    renderPlain(<ApprovalKeysCard />);
    await userEvent.click(await screen.findByRole("button", { name: en.approvals.retire }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Something went wrong on the server.");
    expect((await approvalKeys.current())?.fingerprint).toBe(key.fingerprint);
    expect(screen.getByText(en.approvals.registered, { exact: false })).toBeInTheDocument();
  });
});

describe("allowing a run when this browser's key cannot sign", () => {
  it("still sends the approval, without a signature, to a machine that does not require one", async () => {
    vi.spyOn(approvalKeys, "sign").mockRejectedValue(new Error("key unreadable"));
    const calls = mockApi({ "POST /runs/r1/approve": { status: 204 } });
    const onChanged = vi.fn();
    renderPlain(<RunApproval run={run({ hash: "h".repeat(64) })} ws="w1" onChanged={onChanged} />);
    await userEvent.click(screen.getByRole("button", { name: en.remote.allow }));
    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1));
    const body = calls.find((c) => c.path === "/runs/r1/approve")!.body as Record<string, unknown>;
    expect(body).toEqual({ hash: "h".repeat(64) });
  });

  it("says a key is needed and sends nothing to a machine that requires a signature", async () => {
    vi.spyOn(approvalKeys, "sign").mockRejectedValue(new Error("key unreadable"));
    const calls = mockApi({ "POST /runs/r1/approve": { status: 204 } });
    renderPlain(<RunApproval run={run({ approvalsRequired: true })} ws="w1" onChanged={vi.fn()} />);
    await userEvent.click(screen.getByRole("button", { name: en.remote.allow }));
    expect(await screen.findByText(en.approvals.needKey)).toBeInTheDocument();
    expect(calls.some((c) => c.path === "/runs/r1/approve")).toBe(false);
  });
});
