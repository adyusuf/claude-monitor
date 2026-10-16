import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { approvalKeys, payloadFor, SIGNING } from "../lib/approvalKey";
import { en } from "../i18n/en";
import { mockApi } from "../test/helpers";
import { renderPlain, run } from "../test/remote-fixtures";
import { ApprovalKeysCard, grouped } from "./ApprovalKeysCard";
import { RunApproval } from "./RunApproval";

const bytes = (base64: string) => Uint8Array.from(atob(base64), (c) => c.charCodeAt(0));

beforeEach(async () => approvalKeys.forget());
afterEach(async () => {
  await approvalKeys.forget();
  vi.restoreAllMocks();
});

describe("the approval keys card", () => {
  it("groups a fingerprint in fours the way a person compares it", () => {
    expect(grouped("abcdef0123456789")).toBe("abcd ef01 2345 6789");
    expect(grouped("")).toBe("");
  });

  it("shows how to make a key when there is none and registers the public half of a new one with the name given", async () => {
    const calls = mockApi({
      "GET /me/approval-keys": { body: [] },
      "POST /me/approval-keys": { body: { id: "k1", name: "Work laptop", fingerprint: "f".repeat(64), publicKey: "x", createdAt: new Date().toISOString(), retiredAt: null } },
    });
    renderPlain(<ApprovalKeysCard />);
    await userEvent.type(await screen.findByLabelText(en.approvals.name), "Work laptop");
    await userEvent.click(screen.getByRole("button", { name: en.approvals.make }));

    await waitFor(() => expect(calls.some((c) => c.method === "POST" && c.path === "/me/approval-keys")).toBe(true));
    const body = calls.find((c) => c.method === "POST")!.body as { name: string; publicKey: string };
    expect(body.name).toBe("Work laptop");
    expect(bytes(body.publicKey)).toHaveLength(91); // a P-256 SubjectPublicKeyInfo
    expect(JSON.stringify(body)).not.toContain("privateKey"); // the private half never goes anywhere
    const key = await approvalKeys.current();
    expect(key?.privateKey.extractable).toBe(false);
  });

  it("shows the command an admin types on the machine, with the key and its name", async () => {
    mockApi({ "GET /me/approval-keys": { body: [] }, "POST /me/approval-keys": { body: {} } });
    const key = await approvalKeys.generate('My "own" laptop');
    renderPlain(<ApprovalKeysCard />);
    expect(await screen.findByText(`cm-agent approvers add ${key.publicKey} --name "My own laptop"`)).toBeInTheDocument();
    expect(screen.getByText(en.approvals.notRegistered, { exact: false })).toBeInTheDocument();
  });

  it("lists the registered keys, marks this browser's as registered and retires one", async () => {
    const key = await approvalKeys.generate("This one");
    const calls = mockApi({
      "GET /me/approval-keys": { body: [{ id: "k1", name: "This one", fingerprint: key.fingerprint, publicKey: key.publicKey, createdAt: new Date().toISOString(), retiredAt: null },
        { id: "k2", name: "Old phone", fingerprint: "e".repeat(64), publicKey: "y", createdAt: new Date().toISOString(), retiredAt: null }] },
      "POST /me/approval-keys/k2/retire": { status: 204 },
    });
    renderPlain(<ApprovalKeysCard />);
    expect(await screen.findByText(en.approvals.registered, { exact: false })).toBeInTheDocument();
    const list = screen.getByRole("list", { name: en.approvals.registeredKeys });
    expect(list).toHaveTextContent("Old phone");
    await userEvent.click(screen.getAllByRole("button", { name: en.approvals.retire })[1]!);
    await waitFor(() => expect(calls.some((c) => c.path === "/me/approval-keys/k2/retire")).toBe(true));
    expect(await approvalKeys.current()).not.toBeNull(); // another key was retired, not this browser's
  });

  it("forgets the key of this browser when that is the one retired", async () => {
    const key = await approvalKeys.generate("Mine");
    mockApi({
      "GET /me/approval-keys": { body: [{ id: "k1", name: "Mine", fingerprint: key.fingerprint, publicKey: key.publicKey, createdAt: new Date().toISOString(), retiredAt: null }] },
      "POST /me/approval-keys/k1/retire": { status: 204 },
    });
    renderPlain(<ApprovalKeysCard />);
    await userEvent.click(await screen.findByRole("button", { name: en.approvals.retire }));
    await waitFor(async () => expect(await approvalKeys.current()).toBeNull());
  });

  it("asks for a code when the sign-in is old and sends it with the key", async () => {
    const calls = mockApi({
      "GET /me/approval-keys": { body: [] },
      "POST /me/approval-keys": (call) => ((call.body as { code?: string }).code ? { body: { id: "k1" } } : { status: 403, body: { title: "reauth_required" } }),
    });
    renderPlain(<ApprovalKeysCard />);
    await userEvent.click(await screen.findByRole("button", { name: en.approvals.make }));
    await userEvent.type(await screen.findByLabelText(en.remote.codeLabel), "123456");
    await userEvent.click(screen.getByRole("button", { name: en.approvals.register }));
    await waitFor(() => expect(calls.filter((c) => c.method === "POST").some((c) => (c.body as { code?: string }).code === "123456")).toBe(true));
  });
});

describe("allowing a run signs it with this browser's key", () => {
  const verify = async (publicKey: string, signature: string, payload: string) =>
    crypto.subtle.verify({ name: "ECDSA", hash: "SHA-256" }, await crypto.subtle.importKey("spki", bytes(publicKey), SIGNING, true, ["verify"]), bytes(signature), new TextEncoder().encode(payload));

  it("sends the key's id and a signature over this exact run, its target, its hash and its expiry", async () => {
    const key = await approvalKeys.generate("k");
    const calls = mockApi({ "POST /runs/r1/approve": { status: 204 } });
    const card = run({ hash: "h".repeat(64) });
    renderPlain(<RunApproval run={card} ws="w1" onChanged={vi.fn()} />);
    await userEvent.click(screen.getByRole("button", { name: en.remote.allow }));
    await waitFor(() => expect(calls.some((c) => c.path === "/runs/r1/approve")).toBe(true));
    const body = calls.find((c) => c.path === "/runs/r1/approve")!.body as { hash: string; keyId: string; signature: string };
    expect(body.keyId).toBe(key.fingerprint);
    expect(await verify(key.publicKey, body.signature, payloadFor("run", "r1", "a1", "h".repeat(64), card.expiresAt))).toBe(true);
    expect(await verify(key.publicKey, body.signature, payloadFor("run", "r1", "a1", "g".repeat(64), card.expiresAt))).toBe(false);
  });

  it("sends no signature when this browser has no key, for a machine that does not require one", async () => {
    const calls = mockApi({ "POST /runs/r1/approve": { status: 204 } });
    renderPlain(<RunApproval run={run()} ws="w1" onChanged={vi.fn()} />);
    await userEvent.click(screen.getByRole("button", { name: en.remote.allow }));
    await waitFor(() => expect(calls.some((c) => c.path === "/runs/r1/approve")).toBe(true));
    const body = calls.find((c) => c.path === "/runs/r1/approve")!.body as Record<string, unknown>;
    expect(body.signature).toBeUndefined();
    expect(body.keyId).toBeUndefined();
  });

  it("says a key is needed and sends nothing when the machine requires signatures and this browser has none", async () => {
    const calls = mockApi({ "POST /runs/r1/approve": { status: 204 } });
    renderPlain(<RunApproval run={run({ approvalsRequired: true })} ws="w1" onChanged={vi.fn()} />);
    expect(screen.getByText(en.approvals.runNeedsKey)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: en.remote.allow }));
    expect(await screen.findByText(en.approvals.needKey)).toBeInTheDocument();
    expect(calls.some((c) => c.path === "/runs/r1/approve")).toBe(false);
  });

  it("shows the notice only for a machine that requires signatures", () => {
    renderPlain(<RunApproval run={run({ approvalsRequired: false })} ws="w1" onChanged={vi.fn()} />);
    expect(screen.queryByText(en.approvals.runNeedsKey)).toBeNull();
  });
});
