import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { describe, expect, it, vi } from "vitest";
import { I18nProvider } from "../i18n";
import { Drawer } from "./Drawer";
import { Modal } from "./Modal";

const inEnglish = (node: React.ReactNode) => <I18nProvider initial="en">{node}</I18nProvider>;
const dialog = () => screen.getByRole("dialog");

describe("Modal closing", () => {
  it("closes on Escape", async () => {
    const onClose = vi.fn();
    render(inEnglish(<Modal title="Sure?" onClose={onClose}><button type="button">Yes</button></Modal>));
    await userEvent.keyboard("{Escape}");
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it("closes on the close button", async () => {
    const onClose = vi.fn();
    render(inEnglish(<Modal title="Sure?" onClose={onClose}><p>text</p></Modal>));
    await userEvent.click(screen.getByRole("button", { name: "Close" }));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it("closes when the backdrop is pressed", async () => {
    const onClose = vi.fn();
    render(inEnglish(<Modal title="Sure?" onClose={onClose}><p>text</p></Modal>));
    await userEvent.pointer({ keys: "[MouseLeft>]", target: dialog().parentElement! });
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it("stays open when the press is inside the dialog", async () => {
    const onClose = vi.fn();
    render(inEnglish(<Modal title="Sure?" onClose={onClose}><p>text</p></Modal>));
    await userEvent.pointer({ keys: "[MouseLeft>]", target: screen.getByText("text") });
    expect(onClose).not.toHaveBeenCalled();
  });

  it("calls the latest onClose, not the one it opened with", async () => {
    const first = vi.fn();
    const second = vi.fn();
    const { rerender } = render(inEnglish(<Modal title="Sure?" onClose={first}><p>text</p></Modal>));
    rerender(inEnglish(<Modal title="Sure?" onClose={second}><p>text</p></Modal>));
    await userEvent.keyboard("{Escape}");
    expect(second).toHaveBeenCalledTimes(1);
    expect(first).not.toHaveBeenCalled();
  });

  it("stops answering the keyboard once it is gone", async () => {
    const onClose = vi.fn();
    const { unmount } = render(inEnglish(<Modal title="Sure?" onClose={onClose}><p>text</p></Modal>));
    unmount();
    await userEvent.keyboard("{Escape}");
    expect(onClose).not.toHaveBeenCalled();
  });

  it("is named by its title for assistive technology", () => {
    render(inEnglish(<Drawer title="Invite" onClose={() => undefined}><p>text</p></Drawer>));
    expect(screen.getByRole("dialog", { name: "Invite" })).toBeInTheDocument();
  });
});

describe("Modal nested in Modal", () => {
  function Stack({ onOuter, onInner }: { onOuter: () => void; onInner: () => void }) {
    const [inner, setInner] = useState(false);
    return (
      <Modal title="Outer" onClose={onOuter}>
        <button type="button" onClick={() => setInner(true)}>Open inner</button>
        {inner ? <Modal title="Inner" onClose={() => { onInner(); setInner(false); }}><p>inner text</p></Modal> : null}
      </Modal>
    );
  }

  it("answers Escape in the inner dialog only, and then in the outer one", async () => {
    const onOuter = vi.fn();
    const onInner = vi.fn();
    render(inEnglish(<Stack onOuter={onOuter} onInner={onInner} />));
    await userEvent.click(screen.getByRole("button", { name: "Open inner" }));
    await userEvent.keyboard("{Escape}");
    expect(onInner).toHaveBeenCalledTimes(1);
    expect(onOuter).not.toHaveBeenCalled();
    expect(screen.queryByText("inner text")).toBeNull();
    await userEvent.keyboard("{Escape}");
    expect(onOuter).toHaveBeenCalledTimes(1);
  });

  it("keeps the page locked while the outer dialog is open after the inner one closes", async () => {
    render(inEnglish(<Stack onOuter={() => undefined} onInner={() => undefined} />));
    await userEvent.click(screen.getByRole("button", { name: "Open inner" }));
    await userEvent.keyboard("{Escape}");
    expect(document.body.style.overflow).toBe("hidden");
  });
});

describe("Modal focus", () => {
  it("moves focus into the dialog when it opens", () => {
    render(inEnglish(<Modal title="Sure?" onClose={() => undefined}><button type="button">Yes</button></Modal>));
    expect(dialog()).toHaveFocus();
  });

  it("gives focus back to what had it before, when it closes", async () => {
    function Page() {
      const [open, setOpen] = useState(false);
      return (
        <>
          <button type="button" onClick={() => setOpen(true)}>Open</button>
          {open ? <Modal title="Sure?" onClose={() => setOpen(false)}><button type="button">Yes</button></Modal> : null}
        </>
      );
    }
    render(inEnglish(<Page />));
    const opener = screen.getByRole("button", { name: "Open" });
    await userEvent.click(opener);
    expect(dialog()).toHaveFocus();
    await userEvent.keyboard("{Escape}");
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(opener).toHaveFocus();
  });

  it("leaves focus on a field that took it itself (autoFocus), and gives it back to the opener when it closes", async () => {
    function Page() {
      const [open, setOpen] = useState(false);
      return (
        <>
          <button type="button" onClick={() => setOpen(true)}>Open</button>
          {open ? <Modal title="Code" onClose={() => setOpen(false)}><input aria-label="Code" autoFocus /></Modal> : null}
        </>
      );
    }
    render(inEnglish(<Page />));
    const opener = screen.getByRole("button", { name: "Open" });
    await userEvent.click(opener);
    expect(screen.getByRole("textbox", { name: "Code" })).toHaveFocus();
    await userEvent.keyboard("{Escape}");
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(opener).toHaveFocus();
  });

  it("takes Tab from the last control back to the first", async () => {
    render(inEnglish(<Modal title="Sure?" onClose={() => undefined}><button type="button">Yes</button><button type="button">No</button></Modal>));
    const close = screen.getByRole("button", { name: "Close" });
    screen.getByRole("button", { name: "No" }).focus();
    await userEvent.tab();
    expect(close).toHaveFocus();
  });

  it("takes Shift+Tab from the first control to the last", async () => {
    render(inEnglish(<Modal title="Sure?" onClose={() => undefined}><button type="button">Yes</button><button type="button">No</button></Modal>));
    screen.getByRole("button", { name: "Close" }).focus();
    await userEvent.tab({ shift: true });
    expect(screen.getByRole("button", { name: "No" })).toHaveFocus();
  });

  it("takes Shift+Tab from the dialog itself to the last control", async () => {
    render(inEnglish(<Modal title="Sure?" onClose={() => undefined}><button type="button">Yes</button></Modal>));
    expect(dialog()).toHaveFocus();
    await userEvent.tab({ shift: true });
    expect(screen.getByRole("button", { name: "Yes" })).toHaveFocus();
  });

  it("brings focus back inside when it was outside the dialog", async () => {
    render(inEnglish(<><button type="button">Behind</button><Modal title="Sure?" onClose={() => undefined}><button type="button">Yes</button></Modal></>));
    screen.getByRole("button", { name: "Behind" }).focus();
    await userEvent.tab();
    expect(screen.getByRole("button", { name: "Close" })).toHaveFocus();
  });

  it("skips a disabled control when going round", async () => {
    render(inEnglish(<Modal title="Sure?" onClose={() => undefined}><button type="button">Yes</button><button type="button" disabled>Never</button></Modal>));
    screen.getByRole("button", { name: "Yes" }).focus();
    await userEvent.tab();
    expect(screen.getByRole("button", { name: "Close" })).toHaveFocus();
  });
});

describe("Modal scroll lock", () => {
  it("locks the page's scroll while open and gives back what it was", () => {
    document.body.style.overflow = "auto";
    const { unmount } = render(inEnglish(<Modal title="Sure?" onClose={() => undefined}><p>text</p></Modal>));
    expect(document.body.style.overflow).toBe("hidden");
    unmount();
    expect(document.body.style.overflow).toBe("auto");
    document.body.style.overflow = "";
  });
});
