import { render } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { BrandMark, Icon, type IconName } from "./icons";

const names: IconName[] = ["projects", "sessions", "machines", "members", "integrations", "settings", "server", "download", "lock", "check", "progress", "todo", "menu", "branch", "empty", "edit", "close", "runs"];

describe("icons", () => {
  it.each(names)("draws the %s icon as a hidden outline svg", (name) => {
    const { container } = render(<Icon name={name} />);
    const svg = container.querySelector("svg.icon");
    expect(svg).not.toBeNull();
    expect(svg?.getAttribute("aria-hidden")).toBe("true");
    expect(svg?.childElementCount).toBeGreaterThan(0);
  });

  it("draws the brand mark as three steps and no text", () => {
    const { container } = render(<BrandMark />);
    expect(container.querySelectorAll("rect")).toHaveLength(3);
    expect(container.textContent).toBe("");
  });
});
