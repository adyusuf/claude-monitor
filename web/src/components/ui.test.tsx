import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { I18nProvider } from "../i18n";
import { en } from "../i18n/en";
import { CopyText } from "./ui";

describe("CopyText says Copied only after the copy", () => {
  const c = en.common;
  let original: PropertyDescriptor | undefined;
  const useClipboard = (clipboard: unknown) => Object.defineProperty(navigator, "clipboard", { value: clipboard, configurable: true });
  const show = () => render(<I18nProvider initial="en"><CopyText text="SECRET-123" /></I18nProvider>);
  const button = () => screen.getByRole("button");

  beforeEach(() => {
    original = Object.getOwnPropertyDescriptor(navigator, "clipboard");
  });
  afterEach(() => {
    if (original) Object.defineProperty(navigator, "clipboard", original);
    else Reflect.deleteProperty(navigator, "clipboard");
  });

  it("says Copied once the browser has copied the text", async () => {
    let done: () => void = () => undefined;
    const writeText = vi.fn(() => new Promise<void>((resolve) => { done = resolve; }));
    useClipboard({ writeText });
    show();
    await userEvent.click(button());
    expect(writeText).toHaveBeenCalledWith("SECRET-123");
    expect(button().textContent).toBe(c.copy);
    done();
    await waitFor(() => expect(button().textContent).toBe(c.copied));
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("says the copy failed when the browser refuses it, and not Copied", async () => {
    const writeText = vi.fn().mockRejectedValue(new Error("denied"));
    useClipboard({ writeText });
    show();
    await userEvent.click(button());
    expect(await screen.findByRole("alert")).toHaveTextContent(c.copyFailed);
    expect(writeText).toHaveBeenCalledWith("SECRET-123");
    expect(button().textContent).toBe(c.copy);
  });

  it("says the copy failed when the browser has no clipboard", async () => {
    useClipboard(undefined);
    show();
    await userEvent.click(button());
    expect(await screen.findByRole("alert")).toHaveTextContent(c.copyFailed);
    expect(button().textContent).toBe(c.copy);
  });
});
