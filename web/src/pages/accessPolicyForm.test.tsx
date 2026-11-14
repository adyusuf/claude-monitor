import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { AccessPolicyView } from "../api/typesAccess";
import { en } from "../i18n/en";
import { approvalKeys } from "../lib/approvalKey";
import { defaultView, policyDocument, policyView, REPO } from "../test/access-fixtures";
import { mockApi } from "../test/helpers";
import { renderPlain } from "../test/remote-fixtures";
import { AccessPolicyForm } from "./AccessPolicyForm";

beforeEach(async () => approvalKeys.forget());
afterEach(async () => approvalKeys.forget());

const OTHER = "git.example.org/api";
const reauth = { status: 403, body: { title: "reauth_required" } };
const saved = (c: { method: string }) => c.method === "PUT";

function open(view: AccessPolicyView = policyView()) {
  const onSaved = vi.fn();
  const onConflict = vi.fn();
  const onCancel = vi.fn();
  renderPlain(<AccessPolicyForm view={view} ws="w1" onSaved={onSaved} onConflict={onConflict} onCancel={onCancel} />);
  return { onSaved, onConflict, onCancel };
}
const save = () => userEvent.click(screen.getByRole("button", { name: en.access.save }));
const key = (name: string) => screen.getByRole("checkbox", { name });

describe("the access policy form: repositories", () => {
  it("lists the candidates and the keys already in the policy, sorted, with the policy's own ticked", () => {
    mockApi({});
    open(policyView({ candidateKeys: [REPO, OTHER], document: policyDocument({ projectKeys: [REPO] }) }));
    expect(screen.getAllByRole("checkbox").slice(0, 2).map((c) => c.closest("label")?.textContent)).toEqual([OTHER, REPO]);
    expect(key(OTHER)).not.toBeChecked();
    expect(key(REPO)).toBeChecked();
  });

  it("says so, and cannot save, when the project has no repository to cover", () => {
    mockApi({});
    open(defaultView({ candidateKeys: [], document: policyDocument({ projectKeys: [] }) }));
    expect(screen.getByText(en.access.noCandidates)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: en.access.save })).toBeDisabled();
  });

  it("cannot save with no repository ticked, and can once one is", async () => {
    mockApi({});
    open(defaultView({ candidateKeys: [REPO], document: policyDocument({ projectKeys: [] }) }));
    expect(screen.getByRole("button", { name: en.access.save })).toBeDisabled();
    await userEvent.click(key(REPO));
    expect(screen.getByRole("button", { name: en.access.save })).toBeEnabled();
    await userEvent.click(key(REPO));
    expect(screen.getByRole("button", { name: en.access.save })).toBeDisabled();
  });

  it("sends both repositories, sorted, once the second is ticked", async () => {
    const calls = mockApi({ "PUT /projects/p1/access-policy": { body: policyView({ version: 4 }) } });
    open(policyView({ candidateKeys: [REPO, OTHER] }));
    await userEvent.click(key(OTHER));
    await save();
    await waitFor(() => expect(calls.filter(saved)).toHaveLength(1));
    expect((calls.find(saved)!.body as { document: { projectKeys: string[] } }).document.projectKeys).toEqual([OTHER, REPO]);
  });
});

describe("the access policy form: saving", () => {
  it("sends the expected version and the document unsigned when this browser holds no key", async () => {
    const calls = mockApi({ "PUT /projects/p1/access-policy": { body: policyView({ version: 4 }) } });
    const { onSaved } = open();
    await save();
    await waitFor(() => expect(onSaved).toHaveBeenCalledWith(false));
    expect(calls.find(saved)?.body).toEqual({ expectedVersion: 3, document: policyDocument() });
    expect(screen.queryByText(en.access.willSign)).toBeNull();
  });

  it("signs with this browser's key: the key's id and a signature go with it, and the form says it signed", async () => {
    const stored = await approvalKeys.generate("Mine");
    const calls = mockApi({ "PUT /projects/p1/access-policy": { body: policyView({ version: 4 }) } });
    const { onSaved } = open();
    expect(await screen.findByText(en.access.willSign)).toBeInTheDocument();
    await save();
    await waitFor(() => expect(onSaved).toHaveBeenCalledWith(true));
    const body = calls.find(saved)!.body as { keyId: string; signature: string };
    expect(body.keyId).toBe(stored.fingerprint);
    expect(body.signature).toMatch(/^[A-Za-z0-9+/]{86}==$/); // a 64-byte signature in base64
  });

  it("sends nothing and says so when the browser's key cannot sign", async () => {
    await approvalKeys.generate("Mine");
    vi.spyOn(approvalKeys, "sign").mockRejectedValue(new Error("key unusable"));
    const calls = mockApi({ "PUT /projects/p1/access-policy": { body: policyView({ version: 4 }) } });
    const { onSaved } = open();
    await screen.findByText(en.access.willSign);
    await save();
    expect(await screen.findByText(en.access.signFailed)).toBeInTheDocument();
    expect(calls.some(saved)).toBe(false);
    expect(onSaved).not.toHaveBeenCalled();
  });

  it("says the key could not be read when the browser's store fails", async () => {
    vi.spyOn(approvalKeys, "current").mockRejectedValue(new Error("store unavailable"));
    mockApi({});
    open();
    expect(await screen.findByText(en.access.keyUnreadable)).toBeInTheDocument();
  });

  it("warns that machines which require signatures will ignore an unsigned version", async () => {
    mockApi({});
    open(policyView({ machinesRequiringSignature: 2 }));
    expect(await screen.findByText("This browser has no approval key: 2 machine(s) that require signatures will ignore this version and keep their last signed policy. Make a key on your account page first.")).toBeInTheDocument();
  });

  it("gives no such warning when this browser holds a key", async () => {
    await approvalKeys.generate("Mine");
    mockApi({});
    open(policyView({ machinesRequiringSignature: 2 }));
    await screen.findByText(en.access.willSign);
    expect(screen.queryByText(/will ignore this version/)).toBeNull();
  });

  it("asks for the authenticator code when the sign-in is not recent, then sends it with the same document", async () => {
    const calls = mockApi({
      "PUT /projects/p1/access-policy": (c) => ((c.body as { code?: string }).code === "123456" ? { body: policyView({ version: 4 }) } : reauth),
    });
    const { onSaved } = open();
    await save();
    await userEvent.type(await screen.findByLabelText(en.remote.codeLabel), "123456");
    expect(onSaved).not.toHaveBeenCalled();
    await save();
    await waitFor(() => expect(onSaved).toHaveBeenCalledWith(false));
    expect(calls.filter(saved).map((c) => (c.body as { code?: string }).code)).toEqual([undefined, "123456"]);
  });

  it("tells the card to reload, and not that it saved, when another save came first", async () => {
    mockApi({ "PUT /projects/p1/access-policy": { status: 409, body: { title: "access_policy_changed" } } });
    const { onSaved, onConflict } = open();
    await save();
    await waitFor(() => expect(onConflict).toHaveBeenCalledTimes(1));
    expect(onSaved).not.toHaveBeenCalled();
  });

  it("shows any other refusal as it is and reports nothing saved", async () => {
    mockApi({ "PUT /projects/p1/access-policy": { status: 409, body: { title: "access_key_taken" } } });
    const { onSaved, onConflict } = open();
    await save();
    expect(await screen.findByText(en.errors.access_key_taken)).toBeInTheDocument();
    expect(onSaved).not.toHaveBeenCalled();
    expect(onConflict).not.toHaveBeenCalled();
  });

  it("cancels without sending anything", async () => {
    const calls = mockApi({});
    const { onCancel } = open();
    await userEvent.click(screen.getByRole("button", { name: en.common.cancel }));
    expect(onCancel).toHaveBeenCalledTimes(1);
    expect(calls.some(saved)).toBe(false);
  });
});
