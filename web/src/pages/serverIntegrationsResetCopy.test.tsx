import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { en } from "../i18n/en";
import { renderAt, type Call } from "../test/helpers";
import { github, jira, ORIGIN, serveServerSettings, serverView, smtp } from "../test/server-settings-fixtures";
import { ServerIntegrationsPage } from "./ServerIntegrationsPage";

const s = en.serverSettings;
const page = () => renderAt("/server/integrations", [{ path: "/server/integrations", element: <ServerIntegrationsPage /> }]);
const card = async (name: string) => within((await screen.findByRole("heading", { level: 2, name })).closest("section")!);
const saveMail = () => screen.getByRole("button", { name: s.saveNamed.replace("{name}", s.names.smtp) });
const puts = (calls: Call[]) => calls.filter((c) => c.method === "PUT");
const resetTls = s.resetNamed.replace("{name}", s.fields.tls);
/** E-mail whose STARTTLS comes from the given source. */
const mailWithTls = (source: string) => serverView({
  integrations: [smtp({ fields: smtp().fields.map((f) => (f.name === "tls" ? { ...f, source, value: "false" } : f)) }), github(), jira()],
});

describe("STARTTLS back to its default", () => {
  it("marks a STARTTLS saved on the page for removal and sends only an empty tls", async () => {
    const calls = serveServerSettings({ "GET /server/integrations": { body: mailWithTls("page") }, "PUT /server/integrations/smtp": { body: serverView() } });
    page();
    const mail = await card(s.names.smtp);
    expect(saveMail()).toBeDisabled();
    expect(mail.queryByText(s.removing)).not.toBeInTheDocument();
    await userEvent.click(mail.getByRole("button", { name: resetTls }));
    expect(mail.getByRole("checkbox", { name: s.fields.tls })).toBeDisabled();
    expect(mail.getByText(s.removing)).toBeInTheDocument();
    expect(mail.queryByRole("button", { name: resetTls })).not.toBeInTheDocument();
    expect(mail.getByRole("button", { name: s.undoRemove })).toBeEnabled();
    await userEvent.click(saveMail());
    await waitFor(() => expect(puts(calls)).toHaveLength(1));
    expect(puts(calls)[0]!.body).toEqual({ values: { tls: "" } });
  });

  it("takes the removal back with Keep it, leaving nothing to save", async () => {
    const calls = serveServerSettings({ "GET /server/integrations": { body: mailWithTls("page") } });
    page();
    const mail = await card(s.names.smtp);
    await userEvent.click(mail.getByRole("button", { name: resetTls }));
    expect(saveMail()).toBeEnabled();
    await userEvent.click(mail.getByRole("button", { name: s.undoRemove }));
    expect(mail.getByRole("checkbox", { name: s.fields.tls })).toBeEnabled();
    expect(mail.getByRole("checkbox", { name: s.fields.tls })).not.toBeChecked();
    expect(mail.queryByText(s.removing)).not.toBeInTheDocument();
    expect(mail.getByRole("button", { name: resetTls })).toBeInTheDocument();
    expect(saveMail()).toBeDisabled();
    expect(puts(calls)).toHaveLength(0);
  });

  it.each(["none", "server"])("offers no default button for a STARTTLS from %s", async (source) => {
    serveServerSettings({ "GET /server/integrations": { body: mailWithTls(source) } });
    page();
    const mail = await card(s.names.smtp);
    expect(mail.queryByRole("button", { name: resetTls })).not.toBeInTheDocument();
    expect(mail.queryByText(s.resetDefault)).not.toBeInTheDocument();
  });
});

describe("copying the callback address", () => {
  const address = `${ORIGIN}/api/auth/external/callback/github`;
  const copyButton = (c: Awaited<ReturnType<typeof card>>) => c.getByRole("button", { name: s.copyNamed.replace("{name}", s.names.github) });
  let original: PropertyDescriptor | undefined;
  const useClipboard = (clipboard: unknown) => Object.defineProperty(navigator, "clipboard", { value: clipboard, configurable: true });

  beforeEach(() => {
    original = Object.getOwnPropertyDescriptor(navigator, "clipboard");
  });
  afterEach(() => {
    if (original) Object.defineProperty(navigator, "clipboard", original);
    else Reflect.deleteProperty(navigator, "clipboard");
  });

  it("says Copied only once the browser has copied the address", async () => {
    let done: () => void = () => undefined;
    const writeText = vi.fn(() => new Promise<void>((resolve) => { done = resolve; }));
    useClipboard({ writeText });
    serveServerSettings();
    page();
    const gh = await card(s.names.github);
    await userEvent.click(copyButton(gh));
    expect(writeText).toHaveBeenCalledWith(address);
    expect(copyButton(gh).textContent).toBe(s.copy);
    done();
    await waitFor(() => expect(copyButton(gh).textContent).toBe(s.copied));
    expect(gh.queryByText(s.copyFailed)).not.toBeInTheDocument();
  });

  it("says the copy failed when the browser refuses it, and not Copied", async () => {
    const writeText = vi.fn().mockRejectedValue(new Error("denied"));
    useClipboard({ writeText });
    serveServerSettings();
    page();
    const gh = await card(s.names.github);
    await userEvent.click(copyButton(gh));
    expect(await gh.findByRole("alert")).toHaveTextContent(s.copyFailed);
    expect(writeText).toHaveBeenCalledWith(address);
    expect(copyButton(gh).textContent).toBe(s.copy);
  });

  it("says the copy failed when the browser has no clipboard", async () => {
    useClipboard(undefined);
    serveServerSettings();
    page();
    const gh = await card(s.names.github);
    await userEvent.click(copyButton(gh));
    expect(await gh.findByRole("alert")).toHaveTextContent(s.copyFailed);
    expect(copyButton(gh).textContent).toBe(s.copy);
  });
});
