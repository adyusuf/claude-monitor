import { screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { renderAt } from "../test/helpers";
import { machine } from "../test/remote-fixtures";
import { RoleBadge, roleCode } from "./MachineBadges";

/** A database server whose environment nobody set is still a strict target: never "not set", always red (qa finding of 16/10/2026). */
describe("a role with only a function", () => {
  it("shows the function behind 'not set' and is red for a database", async () => {
    renderAt("/x", [{ path: "/x", element: <RoleBadge view={machine({ effectiveFunction: "database" })} /> }]);
    const badge = await screen.findByText("not set/database");
    expect(badge).toHaveClass("status-failed");
  });

  it("shows a non-strict function as a plain badge, and nothing at all as muted text", async () => {
    renderAt("/x", [{ path: "/x", element: <><RoleBadge view={machine({ effectiveFunction: "web" })} /><RoleBadge view={machine()} /></> }]);
    expect(await screen.findByText("not set/web")).not.toHaveClass("status-failed");
    expect(screen.getByText("not set")).toHaveClass("muted");
  });

  it("codes every combination the server can send", () => {
    expect(roleCode(machine({ effectiveEnvironment: "test", effectiveFunction: "application" }), "not set")).toBe("test/application");
    expect(roleCode(machine({ effectiveEnvironment: "test" }), "not set")).toBe("test/not set");
    expect(roleCode(machine({ effectiveFunction: "database" }), "not set")).toBe("not set/database");
    expect(roleCode(machine(), "not set")).toBe("not set");
  });
});
