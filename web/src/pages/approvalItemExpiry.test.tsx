import { act, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { en } from "../i18n/en";
import { request } from "../test/approvals-fixtures";
import { renderPlain } from "../test/remote-fixtures";
import { ApprovalItem } from "./ApprovalItem";

// A request the agent no longer waits for cannot be answered: at its expiry Allow and Deny turn off and the card says the time is up, as the
// run card does, instead of offering a click that ends in a conflict.
const allow = () => screen.getByRole("button", { name: en.approvalRules.allow });
const deny = () => screen.getByRole("button", { name: en.approvalRules.deny });

afterEach(() => {
  vi.useRealTimers();
});

describe("an approval card at its expiry", () => {
  it("turns Allow and Deny off and says the time is up for a request that has expired", () => {
    renderPlain(<ApprovalItem ws="w1" row={request({ expiresAt: new Date(Date.now() - 1000).toISOString() })} onDone={vi.fn()} />);
    expect(allow()).toBeDisabled();
    expect(deny()).toBeDisabled();
    expect(screen.getByText(en.approvalRules.expired)).toBeInTheDocument();
  });

  it("keeps them on while the request waits and turns them off once its time passes on the open page", () => {
    vi.useFakeTimers({ toFake: ["setInterval", "clearInterval", "Date"] });
    renderPlain(<ApprovalItem ws="w1" row={request({ expiresAt: new Date(Date.now() + 1500).toISOString() })} onDone={vi.fn()} />);
    expect(allow()).toBeEnabled();
    expect(deny()).toBeEnabled();
    expect(screen.queryByText(en.approvalRules.expired)).toBeNull();
    act(() => {
      vi.advanceTimersByTime(2000);
    });
    expect(allow()).toBeDisabled();
    expect(deny()).toBeDisabled();
    expect(screen.getByText(en.approvalRules.expired)).toBeInTheDocument();
  });
});
