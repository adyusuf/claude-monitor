import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { SettingsSection } from "./SettingsSection";

describe("SettingsSection", () => {
  it("names the group, explains it, and holds its controls", () => {
    render(<SettingsSection title="Capture" hint="What machines send."><button type="button">Save</button></SettingsSection>);
    expect(screen.getByRole("heading", { name: "Capture" })).toBeInTheDocument();
    expect(screen.getByText("What machines send.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Save" })).toBeInTheDocument();
  });

  it("leaves out the explanation when there is none", () => {
    const { container } = render(<SettingsSection title="Audit log"><p>Rows</p></SettingsSection>);
    expect(container.querySelector(".setting-intro p")).toBeNull();
    expect(screen.getByText("Rows")).toBeInTheDocument();
  });
});
