import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { describe, expect, it } from "vitest";
import { ME, mockApi, renderAt } from "../test/helpers";
import { TechStackEditor } from "./ApplicationRows";

function Host() {
  const [items, setItems] = useState<string[]>([]);
  return <TechStackEditor items={items} onChange={setItems} />;
}

describe("the technology stack editor", () => {
  it("treats names that differ only in case or accents as one: the API refuses the duplicate", async () => {
    mockApi({ "GET /me": { body: ME } });
    renderAt("/x", [{ path: "/x", element: <Host /> }]);
    const box = await screen.findByLabelText("Technology");
    for (const name of ["İstanbul", "istanbul", "ISTANBUL", "Café", "cafe", "React"]) {
      await userEvent.type(box, name);
      await userEvent.click(screen.getByRole("button", { name: "Add" }));
    }
    expect(screen.getAllByRole("listitem").map((li) => li.textContent)).toEqual(["İstanbul×", "Café×", "React×"]);
  });
});
