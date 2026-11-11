import { screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { en } from "../i18n/en";
import { ME, mockApi, renderAt } from "../test/helpers";
import { DownloadPage } from "./AccountPages";

const show = () => renderAt("/download", [{ path: "/download", element: <DownloadPage /> }]);

describe("the download page's version information", () => {
  it("lists every earlier version with links to its own files", async () => {
    mockApi({
      "GET /me": { body: ME },
      "GET /version": { body: { commit: "abc", downloadsUrl: null, agentVersion: "0.5.0", agentVersions: [{ version: "0.4.0", url: "https://old.example.net/0.4.0" }, { version: "0.3.0", url: "https://old.example.net/0.3.0" }] } },
    });
    show();
    expect(await screen.findByRole("heading", { level: 2, name: en.download.olderTitle })).toBeInTheDocument();
    expect(screen.getByText("version 0.4.0")).toBeInTheDocument();
    expect(screen.getByText("version 0.3.0")).toBeInTheDocument();
    const older = screen.getAllByRole("link", { name: "macOS · Apple silicon" }).map((a) => a.getAttribute("href"));
    expect(older).toEqual(["/downloads/aipod-macos-arm64.zip", "https://old.example.net/0.4.0/aipod-macos-arm64.zip", "https://old.example.net/0.3.0/aipod-macos-arm64.zip"]);
  });

  it("names the current version beside the download step", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /version": { body: { commit: "abc", downloadsUrl: null, agentVersion: "0.5.0" } } });
    show();
    expect(await screen.findByText("version 0.5.0")).toBeInTheDocument();
    expect(screen.queryByRole("heading", { level: 2, name: en.download.olderTitle })).toBeNull();
  });

  it("shows no earlier-versions section when the server lists none", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /version": { body: { commit: "abc", downloadsUrl: null, agentVersions: [] } } });
    show();
    await screen.findByRole("link", { name: "macOS · Apple silicon" });
    expect(screen.queryByRole("heading", { level: 2, name: en.download.olderTitle })).toBeNull();
  });

  it("still shows the whole page from this site's own downloads when the version call fails", async () => {
    const calls = mockApi({ "GET /me": { body: ME }, "GET /version": { status: 500 } });
    show();
    expect(await screen.findByRole("link", { name: "macOS · Apple silicon" })).toHaveAttribute("href", "/downloads/aipod-macos-arm64.zip");
    expect(calls.some((c) => c.path === "/version")).toBe(true);
    expect(screen.getByText(`aipod login --server ${window.location.origin}`)).toBeInTheDocument();
    expect(screen.queryByText(/^version /)).toBeNull();
    expect(screen.queryByRole("heading", { level: 2, name: en.download.olderTitle })).toBeNull();
  });
});
