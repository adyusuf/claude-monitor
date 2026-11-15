import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router";
import { describe, expect, it, vi } from "vitest";
import { SessionProvider } from "../auth/session";
import { I18nProvider } from "../i18n";
import { en } from "../i18n/en";
import { ME, mockApi } from "../test/helpers";
import { pageReload } from "./ErrorBoundary";
import { Layout } from "./Layout";

function Broken(): never {
  throw new Error("render failed");
}

const frame = () =>
  render(
    <I18nProvider initial="en">
      <MemoryRouter initialEntries={["/w/w1/sessions"]}>
        <SessionProvider>
          <Routes>
            <Route element={<Layout />}>
              <Route path="/w/:ws/sessions" element={<Broken />} />
              <Route path="/w/:ws/projects" element={<div>projects page</div>} />
            </Route>
          </Routes>
        </SessionProvider>
      </MemoryRouter>
    </I18nProvider>,
  );

describe("a render error in a page", () => {
  it("shows the message and keeps the frame, logs the error, offers a reload, and clears when the person goes elsewhere", async () => {
    mockApi({ "GET /me": { body: ME } });
    const logged = vi.spyOn(console, "error").mockImplementation(() => undefined);
    const reload = vi.spyOn(pageReload, "reload").mockImplementation(() => undefined);
    frame();
    expect(await screen.findByText(en.pageError.title)).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /Projects/ })).toBeInTheDocument();
    expect(logged).toHaveBeenCalled();
    await userEvent.click(screen.getByRole("button", { name: en.pageError.reload }));
    expect(reload).toHaveBeenCalledTimes(1);
    await userEvent.click(screen.getByRole("link", { name: /Projects/ }));
    expect(await screen.findByText("projects page")).toBeInTheDocument();
    expect(screen.queryByText(en.pageError.title)).not.toBeInTheDocument();
  });
});
