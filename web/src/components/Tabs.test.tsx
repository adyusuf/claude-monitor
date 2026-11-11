import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { Tabs, type TabDef } from "./Tabs";

const three: TabDef[] = [
  { id: "one", label: "One", content: <p>first panel</p> },
  { id: "two", label: "Two", content: <p>second panel</p> },
  { id: "three", label: "Three", content: <p>third panel</p> },
];
const tab = (name: string) => screen.getByRole("tab", { name });
const show = (tabs: TabDef[] = three, initial?: string) => render(<Tabs label="Parts" tabs={tabs} initial={initial} />);

describe("Tabs by mouse", () => {
  it("shows the first tab's panel and marks only that tab as selected", () => {
    show();
    expect(tab("One")).toHaveAttribute("aria-selected", "true");
    expect(tab("Two")).toHaveAttribute("aria-selected", "false");
    expect(screen.getByRole("tabpanel", { name: "One" })).toHaveTextContent("first panel");
    expect(screen.queryByRole("tabpanel", { name: "Two" })).toBeNull(); // the other panels are hidden from the queries
  });

  it("puts only the selected tab in the tab order", () => {
    show();
    expect(tab("One")).toHaveAttribute("tabindex", "0");
    expect(tab("Two")).toHaveAttribute("tabindex", "-1");
  });

  it("switches panel on a click", async () => {
    show();
    await userEvent.click(tab("Three"));
    expect(screen.getByRole("tabpanel", { name: "Three" })).toHaveTextContent("third panel");
    expect(tab("One")).toHaveAttribute("aria-selected", "false");
  });

  it("starts on the tab named by initial", () => {
    show(three, "two");
    expect(tab("Two")).toHaveAttribute("aria-selected", "true");
  });

  it("keeps what was typed on a tab when the person goes to another tab and comes back", async () => {
    show([{ id: "one", label: "One", content: <input aria-label="Draft" /> }, { id: "two", label: "Two", content: <p>other</p> }]);
    await userEvent.type(screen.getByLabelText("Draft"), "half a sentence");
    await userEvent.click(tab("Two"));
    await userEvent.click(tab("One"));
    expect(screen.getByLabelText("Draft")).toHaveValue("half a sentence");
  });
});

describe("Tabs by keyboard", () => {
  it("moves to the next tab with ArrowRight, selecting it and giving it focus", async () => {
    show();
    tab("One").focus();
    await userEvent.keyboard("{ArrowRight}");
    expect(tab("Two")).toHaveAttribute("aria-selected", "true");
    expect(tab("Two")).toHaveFocus();
    expect(screen.getByRole("tabpanel", { name: "Two" })).toHaveTextContent("second panel");
  });

  it("goes round from the last tab to the first with ArrowRight", async () => {
    show();
    await userEvent.click(tab("Three"));
    await userEvent.keyboard("{ArrowRight}");
    expect(tab("One")).toHaveAttribute("aria-selected", "true");
    expect(tab("One")).toHaveFocus();
  });

  it("moves to the previous tab with ArrowLeft", async () => {
    show();
    await userEvent.click(tab("Two"));
    await userEvent.keyboard("{ArrowLeft}");
    expect(tab("One")).toHaveAttribute("aria-selected", "true");
    expect(tab("One")).toHaveFocus();
  });

  it("goes round from the first tab to the last with ArrowLeft", async () => {
    show();
    tab("One").focus();
    await userEvent.keyboard("{ArrowLeft}");
    expect(tab("Three")).toHaveAttribute("aria-selected", "true");
    expect(tab("Three")).toHaveFocus();
  });

  it("leaves the selection alone on a key that is not an arrow", async () => {
    show();
    tab("One").focus();
    await userEvent.keyboard("{ArrowDown}");
    expect(tab("One")).toHaveAttribute("aria-selected", "true");
    expect(tab("One")).toHaveFocus();
  });
});

describe("Tabs that do not apply", () => {
  const withHiddenFirst: TabDef[] = [{ ...three[0]!, hidden: true }, three[1]!, three[2]!];

  it("leaves a hidden tab out and starts on the first one that shows", () => {
    show(withHiddenFirst);
    expect(screen.getAllByRole("tab").map((t) => t.textContent)).toEqual(["Two", "Three"]);
    expect(tab("Two")).toHaveAttribute("aria-selected", "true");
    expect(screen.getByRole("tabpanel", { name: "Two" })).toBeInTheDocument();
  });

  it("falls back to the first tab that shows when initial names a hidden tab", () => {
    show(withHiddenFirst, "one");
    expect(tab("Two")).toHaveAttribute("aria-selected", "true");
  });

  it("goes round the tabs that show only, skipping the hidden one", async () => {
    show(withHiddenFirst);
    tab("Two").focus();
    await userEvent.keyboard("{ArrowLeft}");
    expect(tab("Three")).toHaveAttribute("aria-selected", "true");
    expect(tab("Three")).toHaveFocus();
  });

  it("shows nothing when every tab is hidden", () => {
    const { container } = show(three.map((t) => ({ ...t, hidden: true })));
    expect(container).toBeEmptyDOMElement();
  });
});
