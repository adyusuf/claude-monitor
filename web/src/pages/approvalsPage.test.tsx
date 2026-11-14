import { act, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { ApprovalRequestRow, ApprovalRuleRow } from "../api/types";
import { en } from "../i18n/en";
import { type Call, FakeEventSource, ME, mockApi, renderAt } from "../test/helpers";
import { request, rule } from "../test/approvals-fixtures";
import { ApprovalsPage } from "./ApprovalsPage";
import { count } from "./machinesFixture";

const route = [{ path: "/w/:ws/approvals", element: <ApprovalsPage /> }];

beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
afterEach(() => vi.unstubAllGlobals());

interface Data { waiting: ApprovalRequestRow[]; rules: ApprovalRuleRow[]; recent: ApprovalRequestRow[] }
const empty: Data = { waiting: [], rules: [], recent: [] };
const serve = (data: () => Data, extra: Parameters<typeof mockApi>[0] = {}) => mockApi({
  "GET /me": { body: ME },
  "GET /workspaces/w1/permission-requests": (c: Call) => ({ body: c.path.includes("status=answered") ? data().recent : data().waiting }),
  "GET /workspaces/w1/approval-rules": () => ({ body: data().rules }),
  ...extra,
});
const card = (title: string) => screen.getByRole("heading", { name: title }).closest(".card") as HTMLElement;
const reads = (calls: Call[]) => count(calls, "/workspaces/w1/approval-rules");

describe("the approvals page", () => {
  it("says nothing waits, there is no standing approval and nothing was answered lately", async () => {
    serve(() => empty);
    renderAt("/w/w1/approvals", route);
    expect(await screen.findByText(en.approvalRules.noneWaiting)).toBeInTheDocument();
    expect(screen.getByText(en.approvalRules.noRules)).toBeInTheDocument();
    expect(screen.getByText(en.approvalRules.noRecent)).toBeInTheDocument();
  });

  it("lists each waiting request as a card with its command, and draws attention to the card", async () => {
    serve(() => ({ ...empty, waiting: [request({ id: "q1" }), request({ id: "q2", toolInput: { command: "make all" }, sessionTitle: "Other" })] }));
    renderAt("/w/w1/approvals", route);
    await screen.findByText("npm test");
    const waiting = card(en.approvalRules.waiting);
    expect(within(waiting).getByText("make all")).toBeInTheDocument();
    expect(within(waiting).queryByText(en.approvalRules.noneWaiting)).toBeNull();
    expect(waiting).toHaveClass("card-attention");
  });

  it("does not draw attention to the waiting card when nothing waits", async () => {
    serve(() => empty);
    renderAt("/w/w1/approvals", route);
    await screen.findByText(en.approvalRules.noneWaiting);
    expect(card(en.approvalRules.waiting)).not.toHaveClass("card-attention");
  });

  it("shows the error text when the lists cannot be read", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/permission-requests": { status: 500, body: {} }, "GET /workspaces/w1/approval-rules": { body: [] } });
    renderAt("/w/w1/approvals", route);
    expect(await screen.findByRole("alert")).toHaveTextContent("Something went wrong on the server.");
  });

  it("reads the lists again when a card was answered, so the request leaves the waiting list and appears among the answered", async () => {
    let data: Data = { ...empty, waiting: [request()] };
    const answered = request({ status: "answered", decision: "allow", answeredByName: "Örnek Kişi", answeredAt: new Date(2026, 9, 5, 12, 5, 0).toISOString() });
    serve(() => data, { "POST /permission-requests/q1/answer": () => { data = { ...empty, recent: [answered] }; return { status: 204 }; } });
    renderAt("/w/w1/approvals", route);
    await userEvent.click(await screen.findByRole("button", { name: en.approvalRules.allow }));
    expect(await screen.findByText(en.approvalRules.noneWaiting)).toBeInTheDocument();
    expect(within(card(en.approvalRules.recent)).getByText("Örnek Kişi")).toBeInTheDocument();
  });
});

describe("the standing approvals table", () => {
  const rowOf = (text: string) => screen.getByText(text).closest("tr") as HTMLElement;
  const cellTexts = (row: HTMLElement) => within(row).getAllByRole("cell").slice(0, 6).map((c) => c.textContent);

  it("shows the machine and project, tool, command, duration, end and how often it answered", async () => {
    serve(() => ({ ...empty, rules: [rule()] }));
    renderAt("/w/w1/approvals", route);
    await screen.findByText("npm test");
    expect(cellTexts(rowOf("npm test"))).toEqual(["laptop-1 · widgets", "Bash", "npm test", "1 hour", "06/10/2026 09:30", "3"]);
  });

  it("says 'any input' for a rule with no command and 'until revoked' for one that never ends", async () => {
    serve(() => ({ ...empty, rules: [rule({ command: null, expiresAt: null, duration: "always", toolName: "Write", projectName: null })] }));
    renderAt("/w/w1/approvals", route);
    const row = (await screen.findByText(en.approvalRules.anyInput)).closest("tr") as HTMLElement;
    expect(cellTexts(row)).toEqual(["laptop-1", "Write", en.approvalRules.anyInput, "Always", en.approvalRules.never, "3"]);
  });

  it("asks before revoking and revokes nothing when the person declines", async () => {
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(false);
    const calls = serve(() => ({ ...empty, rules: [rule()] }));
    renderAt("/w/w1/approvals", route);
    await userEvent.click(await screen.findByRole("button", { name: en.approvalRules.revoke }));
    expect(confirm).toHaveBeenCalledWith(en.approvalRules.revokeConfirm);
    expect(calls.filter((c) => c.method === "DELETE")).toEqual([]);
    expect(screen.getByText("npm test")).toBeInTheDocument();
  });

  it("deletes that rule when confirmed and reads the lists again, so the row is gone", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    let data: Data = { ...empty, rules: [rule()] };
    const calls = serve(() => data, { "DELETE /approval-rules/ru1": () => { data = empty; return { status: 204 }; } });
    renderAt("/w/w1/approvals", route);
    await userEvent.click(await screen.findByRole("button", { name: en.approvalRules.revoke }));
    expect(await screen.findByText(en.approvalRules.noRules)).toBeInTheDocument();
    expect(calls.filter((c) => c.method === "DELETE").map((c) => c.path)).toEqual(["/approval-rules/ru1"]);
  });

  it("shows the API's error in the row and keeps the rule when the revoke fails", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    serve(() => ({ ...empty, rules: [rule()] }), { "DELETE /approval-rules/ru1": { status: 404, body: {} } });
    renderAt("/w/w1/approvals", route);
    await userEvent.click(await screen.findByRole("button", { name: en.approvalRules.revoke }));
    expect(await within(rowOf("npm test")).findByRole("alert")).toHaveTextContent("Not found.");
    expect(screen.getByRole("button", { name: en.approvalRules.revoke })).toBeEnabled();
  });
});

describe("the recently answered table", () => {
  it("shows the tool with its decision, the session, the machine, who answered and when", async () => {
    serve(() => ({ ...empty, recent: [
      request({ id: "a", status: "answered", decision: "allow", answeredByName: "Örnek Kişi", answeredAt: new Date(2026, 9, 5, 12, 5, 0).toISOString() }),
      request({ id: "b", status: "answered", decision: "deny", sessionTitle: null, hostname: "box-2", answeredByName: null, answeredByRule: "ru1", answeredAt: new Date(2026, 9, 5, 12, 6, 0).toISOString() }),
    ] }));
    renderAt("/w/w1/approvals", route);
    await screen.findByText("Örnek Kişi");
    const rows = within(card(en.approvalRules.recent)).getAllByRole("row").slice(1);
    const texts = rows.map((r) => within(r).getAllByRole("cell").map((c) => c.textContent));
    expect(texts).toEqual([
      ["Bash Allowed", "Fix the login", "laptop-1", "Örnek Kişi", "05/10/2026 12:05"],
      ["Bash Denied", en.approvalRules.untitled, "box-2", en.approvalRules.byRule, "05/10/2026 12:06"],
    ]);
  });

  it("marks an allow as good and a denial as plain", async () => {
    serve(() => ({ ...empty, recent: [request({ id: "a", status: "answered", decision: "allow" }), request({ id: "b", status: "answered", decision: "deny" })] }));
    renderAt("/w/w1/approvals", route);
    expect(await screen.findByText(en.approvalRules.decision.allow)).toHaveClass("ok");
    expect(screen.getByText(en.approvalRules.decision.deny)).not.toHaveClass("ok");
  });
});

describe("the approvals page follows the live stream", () => {
  it("reads the lists again on a permission event and shows what is new", async () => {
    let data: Data = empty;
    const calls = serve(() => data);
    renderAt("/w/w1/approvals", route);
    await screen.findByText(en.approvalRules.noneWaiting);
    const before = reads(calls);
    data = { ...empty, waiting: [request({ toolInput: { command: "make all" } })] };
    act(() => FakeEventSource.last!.emit("permission", {}));
    expect(await screen.findByText("make all")).toBeInTheDocument();
    expect(reads(calls)).toBe(before + 1);
  });

  it("reads again when the stream comes back, because events may have been missed", async () => {
    let data: Data = empty;
    serve(() => data);
    renderAt("/w/w1/approvals", route);
    await screen.findByText(en.approvalRules.noneWaiting);
    act(() => FakeEventSource.last!.emit("ready", {}));
    data = { ...empty, rules: [rule()] };
    act(() => FakeEventSource.last!.emit("ready", {}));
    expect(await screen.findByText("npm test")).toBeInTheDocument();
  });

  it("does not read again for an event about something else", async () => {
    const calls = serve(() => empty);
    renderAt("/w/w1/approvals", route);
    await screen.findByText(en.approvalRules.noneWaiting);
    const before = reads(calls);
    act(() => FakeEventSource.last!.emit("run", {}));
    act(() => FakeEventSource.last!.emit("session", {}));
    expect(reads(calls)).toBe(before);
  });
});
