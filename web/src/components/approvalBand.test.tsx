import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import type { ApprovalRequestRow } from "../api/types";
import { en } from "../i18n/en";
import { previewOf } from "../pages/ApprovalItem";
import { request } from "../test/approvals-fixtures";
import { mockApi } from "../test/helpers";
import { renderPlain } from "../test/remote-fixtures";
import { ApprovalBand, firstLine } from "./ApprovalBand";

// The band on every workspace page (ADR-0019 §11): how many requests wait, the oldest in one line, the runs waiting, and Details with the cards.
const band = (rows: ApprovalRequestRow[], runs = 0, onAnswered = vi.fn()) => {
  const view = renderPlain(<ApprovalBand ws="w1" rows={rows} runs={runs} onAnswered={onAnswered} />);
  return { ...view, onAnswered };
};
const details = () => screen.queryByRole("button", { name: en.approvalRules.details });

describe("the approval band", () => {
  it("is not there when nothing waits", () => {
    const { container } = band([], 0);
    expect(container).toBeEmptyDOMElement();
  });

  it("says one approval is waiting and shows the tool, the command, the session and the machine in one line", () => {
    band([request({ toolInput: { command: "npm test" } })]);
    const status = screen.getByRole("status");
    expect(within(status).getByText(en.approvalRules.bandOne)).toBeInTheDocument();
    expect(within(status).getByText("npm test").tagName).toBe("CODE");
    expect(status.textContent).toContain("Bash npm test · Fix the login · laptop-1");
    expect(screen.queryByRole("link", { name: /runs wait/ })).toBeNull();
  });

  it("counts many and shows only the oldest, the first line of its command and how many lines follow", () => {
    band([request({ id: "q1", toolInput: { command: "cd app\n\n  \nnpm ci\r\nnpm test" } }), request({ id: "q2", sessionTitle: "Other", toolInput: { command: "ls" } })]);
    const status = screen.getByRole("status");
    expect(within(status).getByText(en.approvalRules.bandMany.replace("{n}", "2"))).toBeInTheDocument();
    expect(within(status).getByText("cd app").tagName).toBe("CODE");
    expect(status.textContent).toContain(en.approvalRules.moreLines.replace("{n}", "2"));
    expect(status.textContent).not.toContain("Other");
  });

  it("says '+1 more line' for a command of two lines and 'Untitled session' for a session with no title", () => {
    band([request({ sessionTitle: null, toolInput: { command: "make\nmake install" } })]);
    const status = screen.getByRole("status");
    expect(status.textContent).toContain(`make ${en.approvalRules.moreLine}`);
    expect(status.textContent).toContain(en.approvalRules.untitled);
  });

  it("with only runs waiting, counts them, links to the runs page and offers no Details", () => {
    band([], 3);
    expect(screen.getByRole("link", { name: en.approvalRules.bandRuns.replace("{n}", "3") })).toHaveAttribute("href", "/w/w1/runs");
    expect(details()).toBeNull();
    expect(screen.queryByText(en.approvalRules.bandOne)).toBeNull();
  });

  it("opens every waiting request in Details and tells the frame which one was answered there", async () => {
    mockApi({ "POST /permission-requests/q2/answer": { status: 204 } });
    const { onAnswered } = band([request({ id: "q1" }), request({ id: "q2", toolInput: { command: "make lint" } })], 1);
    await userEvent.click(details()!);
    const dialog = screen.getByRole("dialog", { name: en.approvalRules.dialog });
    expect(within(dialog).getAllByRole("button", { name: en.approvalRules.allow })).toHaveLength(2);
    expect(within(dialog).getByRole("link", { name: en.approvalRules.title })).toHaveAttribute("href", "/w/w1/approvals");
    await userEvent.click(within(dialog).getAllByRole("button", { name: en.approvalRules.deny })[1]!);
    await waitFor(() => expect(onAnswered).toHaveBeenCalledWith("q2"));
  });

  it("closes Details when the link to the approvals page is followed", async () => {
    band([request()]);
    await userEvent.click(details()!);
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("link", { name: en.approvalRules.title }));
    expect(screen.queryByRole("dialog")).toBeNull();
  });
});

describe("the band's one-line preview", () => {
  it("is the first line with text and the number of such lines after it", () => {
    expect(firstLine("")).toEqual(["", 0]);
    expect(firstLine("\n  \nonly")).toEqual(["only", 0]);
    expect(firstLine("a\r\nb\rc\n\nd")).toEqual(["a", 3]);
  });

  it("is a file path, a URL or a pattern for other tools, and nothing when there is none", () => {
    expect(previewOf({ toolName: "Edit", toolInput: { file_path: "/srv/a.ts" } })).toBe("/srv/a.ts");
    expect(previewOf({ toolName: "WebFetch", toolInput: { url: "https://docs.local/x" } })).toBe("https://docs.local/x");
    expect(previewOf({ toolName: "Grep", toolInput: { pattern: "TODO", path: "" } })).toBe("TODO");
    expect(previewOf({ toolName: "Task", toolInput: { description: 7 } })).toBe("");
    expect(previewOf({ toolName: "Bash", toolInput: { command: 7 } })).toBe("");
  });
});
