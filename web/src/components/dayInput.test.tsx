import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { describe, expect, it } from "vitest";
import { ME, mockApi, renderAt } from "../test/helpers";
import { DayField } from "./DayInput";

function Host({ start, seen }: { start: string; seen: string[] }) {
  const [value, setValue] = useState(start);
  return <DayField label="Day" value={value} onChange={(v) => { seen.push(v); setValue(v); }} />;
}
const open = (start: string, seen: string[]) => {
  mockApi({ "GET /me": { body: ME } });
  renderAt("/x", [{ path: "/x", element: <Host start={start} seen={seen} /> }]);
};

describe("the day field", () => {
  it("shows a stored day as dd/mm/yyyy and hands up yyyy-mm-dd once a whole real day is typed", async () => {
    const seen: string[] = [];
    open("2026-10-05", seen);
    const box = await screen.findByLabelText("Day");
    expect(box).toHaveValue("05/10/2026");
    await userEvent.clear(box);
    await userEvent.type(box, "31/12/2027");
    expect(box).toHaveValue("31/12/2027");
    expect(seen.at(-1)).toBe("2027-12-31");
  });

  it("keeps a half-typed day as typed and hands it up for the form to refuse; an empty box is an empty day", async () => {
    const seen: string[] = [];
    open("", seen);
    const box = await screen.findByLabelText("Day");
    await userEvent.type(box, "13/13");
    expect(box).toHaveValue("13/13");
    expect(seen.at(-1)).toBe("13/13");
    await userEvent.clear(box);
    expect(seen.at(-1)).toBe("");
    expect(box).toHaveAttribute("placeholder", "dd/mm/yyyy");
  });
});
