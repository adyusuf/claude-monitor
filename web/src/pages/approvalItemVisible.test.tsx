import { screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { ApprovalRequestRow } from "../api/types";
import { request } from "../test/approvals-fixtures";
import { renderPlain } from "../test/remote-fixtures";
import { ApprovalItem } from "./ApprovalItem";

// What the approver reads must be what runs (ADR-0005): a right-to-left override, a zero-width character or a carriage return in the input is
// shown as its escape and highlighted, as the run card does, never drawn as it is.
const show = (row: ApprovalRequestRow) => renderPlain(<ApprovalItem ws="w1" row={row} onDone={vi.fn()} />);
const marks = (container: HTMLElement) => [...container.querySelectorAll("mark.vis-flag")].map((m) => m.textContent);

describe("hidden characters on an approval card", () => {
  it("shows a right-to-left override and a carriage return in a Bash command as escapes", () => {
    const { container } = show(request({ toolInput: { command: "echo safe‮;hs.lim\rrm -rf ~" } }));
    const command = container.querySelector("pre.approval-command")!;
    expect(command.textContent).toBe("echo safe\\u202E;hs.lim\\rrm -rf ~");
    expect(command.textContent).not.toContain("‮");
    expect(marks(container)).toEqual(["\\u202E", "\\r"]);
  });

  it("shows a zero-width character in a file path of another tool as its escape", () => {
    const { container } = show(request({ toolName: "Write", toolInput: { file_path: "/srv/app/a​.ts" } }));
    expect(screen.getByText(/file_path/).textContent).toBe('{"file_path":"/srv/app/a\\u200B.ts"}');
    expect(marks(container)).toEqual(["\\u200B"]);
  });

  it("shows a hidden character in the other keys of a Bash call as its escape", () => {
    const { container } = show(request({ toolInput: { command: "npm test", run_in_background: "yes⁦" } }));
    expect(marks(container)).toEqual(["\\u2066"]);
  });
});
