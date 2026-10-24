import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { en } from "../i18n/en";
import { renderAt, type Call } from "../test/helpers";
import { github, jira, serveServerSettings, serverView, smtp } from "../test/server-settings-fixtures";
import { ServerIntegrationsPage } from "./ServerIntegrationsPage";

const s = en.serverSettings;
const req = (label: string) => `${label} (${s.required})`;
const page = () => renderAt("/server/integrations", [{ path: "/server/integrations", element: <ServerIntegrationsPage /> }]);
const mailCard = async () => within((await screen.findByRole("heading", { level: 2, name: s.names.smtp })).closest("section")!);
const saveMail = () => screen.getByRole("button", { name: s.saveNamed.replace("{name}", s.names.smtp) });
const puts = (calls: Call[]) => calls.filter((c) => c.method === "PUT");

describe("saving an integration", () => {
  it("sends only the changed settings, leaves an empty secret out, and shows the view that comes back", async () => {
    const after = serverView({ integrations: [smtp({ status: "incomplete", fields: smtp().fields.map((f) => (f.name === "port" ? { ...f, value: "2525" } : f)) }), github(), jira()] });
    const calls = serveServerSettings({ "PUT /server/integrations/smtp": { body: after } });
    page();
    const mail = await mailCard();
    expect(saveMail()).toBeDisabled();
    await userEvent.clear(mail.getByLabelText(req(s.fields.port)));
    await userEvent.type(mail.getByLabelText(req(s.fields.port)), "2525");
    await userEvent.click(saveMail());
    expect(await mail.findByText(s.savedDone)).toBeInTheDocument();
    expect(puts(calls)).toHaveLength(1);
    expect(puts(calls)[0]!.body).toEqual({ values: { port: "2525" } });
    expect(mail.getByText(s.statusLabel.replace("{status}", s.status.incomplete))).toBeInTheDocument();
    expect(mail.getByLabelText(req(s.fields.port))).toHaveValue(2525);
    expect(saveMail()).toBeDisabled();
  });

  it("sends a typed secret and a switched STARTTLS", async () => {
    const calls = serveServerSettings({ "PUT /server/integrations/smtp": { body: serverView() } });
    page();
    const mail = await mailCard();
    await userEvent.type(mail.getByLabelText(req(s.fields.password)), "n3w-pass");
    await userEvent.click(mail.getByRole("checkbox", { name: s.fields.tls }));
    await userEvent.click(saveMail());
    await waitFor(() => expect(puts(calls)).toHaveLength(1));
    expect(puts(calls)[0]!.body).toEqual({ values: { password: "n3w-pass", tls: "false" } });
  });

  it("removes a saved secret by sending an empty value, and can take the removal back", async () => {
    const calls = serveServerSettings({ "PUT /server/integrations/smtp": { body: serverView() } });
    page();
    const mail = await mailCard();
    await userEvent.type(mail.getByLabelText(req(s.fields.password)), "typed-first");
    await userEvent.click(mail.getByRole("button", { name: s.removeNamed.replace("{name}", s.fields.password) }));
    expect(mail.getByText(s.removing)).toBeInTheDocument();
    expect(mail.getByLabelText(req(s.fields.password))).toBeDisabled();
    await userEvent.click(mail.getByRole("button", { name: s.undoRemove }));
    await userEvent.clear(mail.getByLabelText(req(s.fields.password)));
    expect(saveMail()).toBeDisabled();
    await userEvent.type(mail.getByLabelText(req(s.fields.password)), "typed-again");
    await userEvent.click(mail.getByRole("button", { name: s.removeNamed.replace("{name}", s.fields.password) }));
    await userEvent.click(saveMail());
    await waitFor(() => expect(puts(calls)).toHaveLength(1));
    expect(puts(calls)[0]!.body).toEqual({ values: { password: "" } });
  });

  it("sends an emptied setting as an empty value, which removes it", async () => {
    const calls = serveServerSettings({ "PUT /server/integrations/smtp": { body: serverView() } });
    page();
    await userEvent.clear((await mailCard()).getByLabelText(req(s.fields.user)));
    await userEvent.click(saveMail());
    await waitFor(() => expect(puts(calls)).toHaveLength(1));
    expect(puts(calls)[0]!.body).toEqual({ values: { user: "" } });
  });

  it("shows a refused value at its field, translated", async () => {
    serveServerSettings({ "PUT /server/integrations/smtp": { status: 400, body: { errors: { port: ["invalid_value"] } } } });
    page();
    const mail = await mailCard();
    await userEvent.type(mail.getByLabelText(req(s.fields.port)), "9");
    await userEvent.click(saveMail());
    expect(await mail.findByText(s.errors.invalid_value)).toBeInTheDocument();
    expect(mail.getByLabelText(req(s.fields.port))).toHaveAttribute("aria-invalid", "true");
    expect(mail.queryByText(en.errors.invalid_value)).not.toBeInTheDocument();
    expect(mail.queryByText(s.savedDone)).not.toBeInTheDocument();
  });

  it("shows a refusal the card has no field for at the top, with the app-wide text of its code", async () => {
    serveServerSettings({ "PUT /server/integrations/smtp": { status: 400, body: { errors: { values: ["set_on_server"] } } } });
    page();
    const mail = await mailCard();
    await userEvent.type(mail.getByLabelText(req(s.fields.user)), "x");
    await userEvent.click(saveMail());
    expect(await mail.findByText(en.errors.set_on_server)).toBeInTheDocument();
  });

  it("says someone else saved at the same moment on 409 settings_conflict", async () => {
    serveServerSettings({ "PUT /server/integrations/smtp": { status: 409, body: { title: "settings_conflict" } } });
    page();
    const mail = await mailCard();
    await userEvent.type(mail.getByLabelText(req(s.fields.user)), "x");
    await userEvent.click(saveMail());
    expect(await mail.findByRole("alert")).toHaveTextContent(en.errors.settings_conflict);
    expect(mail.queryByText(s.savedDone)).not.toBeInTheDocument();
  });

  it("asks for the two-step code on reauth_required and sends the same values again with it", async () => {
    const calls = serveServerSettings({
      "PUT /server/integrations/smtp": (call) => ((call.body as { code?: string }).code === "123456" ? { body: serverView() } : { status: 403, body: { title: "reauth_required" } }),
    });
    page();
    const mail = await mailCard();
    await userEvent.type(mail.getByLabelText(req(s.fields.user)), "2");
    await userEvent.click(saveMail());
    const code = await mail.findByLabelText(en.remote.codeLabel);
    expect(saveMail()).toBeDisabled();
    await userEvent.type(code, "123456");
    await userEvent.click(saveMail());
    expect(await mail.findByText(s.savedDone)).toBeInTheDocument();
    expect(puts(calls).map((c) => c.body)).toEqual([{ values: { user: "mailer2" } }, { values: { user: "mailer2" }, code: "123456" }]);
    expect(mail.queryByLabelText(en.remote.codeLabel)).not.toBeInTheDocument();
  });

  it("says a wrong code is wrong and keeps asking", async () => {
    serveServerSettings({ "PUT /server/integrations/smtp": { status: 403, body: { title: "reauth_required" } } });
    page();
    const mail = await mailCard();
    await userEvent.type(mail.getByLabelText(req(s.fields.user)), "2");
    await userEvent.click(saveMail());
    await userEvent.type(await mail.findByLabelText(en.remote.codeLabel), "000000");
    await userEvent.click(saveMail());
    expect(await mail.findByText(en.errors.invalid_code)).toBeInTheDocument();
    expect(mail.getByLabelText(en.remote.codeLabel)).toBeInTheDocument();
  });

  it("disables every input and save when the server cannot save, and says why", async () => {
    serveServerSettings({ "GET /server/integrations": { body: serverView({ canSave: false }) } });
    page();
    const mail = await mailCard();
    expect(screen.getByText(s.cannotSave)).toBeInTheDocument();
    expect(mail.getByLabelText(req(s.fields.port))).toBeDisabled();
    expect(mail.getByLabelText(req(s.fields.password))).toBeDisabled();
    expect(mail.getByRole("checkbox", { name: s.fields.tls })).toBeDisabled();
    expect(mail.getByRole("button", { name: s.removeNamed.replace("{name}", s.fields.password) })).toBeDisabled();
    expect(saveMail()).toBeDisabled();
  });
});

describe("the test mail", () => {
  it("is offered on the e-mail card only", async () => {
    serveServerSettings();
    page();
    await mailCard();
    expect(screen.getAllByRole("button", { name: s.testMail })).toHaveLength(1);
    const gh = within(screen.getByRole("heading", { level: 2, name: s.names.github }).closest("section")!);
    expect(gh.queryByRole("button", { name: s.testMail })).not.toBeInTheDocument();
  });

  it("says the mail went out on 204", async () => {
    const calls = serveServerSettings({ "POST /server/integrations/smtp/test": { status: 204 } });
    page();
    await userEvent.click((await mailCard()).getByRole("button", { name: s.testMail }));
    expect(await screen.findByText(s.testMailSent)).toBeInTheDocument();
    expect(calls.filter((c) => c.method === "POST").map((c) => c.path)).toEqual(["/server/integrations/smtp/test"]);
  });

  it.each([
    ["mail_failed", { smtp: ["mail_failed"] }, en.errors.mail_failed],
    ["no_email", { email: ["no_email"] }, en.errors.no_email],
  ])("says why it failed on %s", async (_, errors, text) => {
    serveServerSettings({ "POST /server/integrations/smtp/test": { status: 400, body: { errors } } });
    page();
    await userEvent.click((await mailCard()).getByRole("button", { name: s.testMail }));
    expect(await screen.findByText(text)).toBeInTheDocument();
    expect(screen.queryByText(s.testMailSent)).not.toBeInTheDocument();
  });
});

