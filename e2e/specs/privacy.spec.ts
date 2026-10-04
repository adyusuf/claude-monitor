import { createHmac } from "node:crypto";
import { expect, test } from "@playwright/test";
import { e2e } from "../config";
import { signUp } from "./helpers";

function fromBase32(text: string): Buffer {
  const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
  const bytes: number[] = [];
  let buffer = 0;
  let bits = 0;
  for (const c of text) {
    buffer = (buffer << 5) | alphabet.indexOf(c);
    bits += 5;
    if (bits >= 8) {
      bytes.push((buffer >> (bits - 8)) & 0xff);
      bits -= 8;
    }
  }
  return Buffer.from(bytes);
}

/** RFC 6238, as an authenticator app computes it; offset in 30-second steps. */
function totp(secret: Buffer, offset = 0): string {
  const step = Math.floor(Date.now() / 1000 / 30) + offset;
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(step));
  const hash = createHmac("sha1", secret).update(counter).digest();
  const at = hash[hash.length - 1]! & 0x0f;
  const value = ((hash[at]! & 0x7f) << 24) | (hash[at + 1]! << 16) | (hash[at + 2]! << 8) | hash[at + 3]!;
  return String(value % 1_000_000).padStart(6, "0");
}

test("the account page downloads the user's data and deletes the account", async ({ page }) => {
  const address = await signUp(page, "privacy");
  await page.goto("/account");
  const [download] = await Promise.all([page.waitForEvent("download"), page.getByRole("link", { name: "Download my data" }).click()]);
  expect(download.suggestedFilename()).toMatch(/^claude-monitor-export-\d{4}-\d{2}-\d{2}\.json$/);
  const exported = JSON.parse(await (await download.createReadStream()).toArray().then((c) => Buffer.concat(c).toString("utf8")));
  expect(exported.user.email).toBe(address);

  await page.getByLabel("Password").fill(e2e.password);
  await page.getByLabel("Type DELETE to confirm").fill("DELETE");
  await page.getByRole("button", { name: "Delete my account" }).click();
  await expect(page.getByText("Your account is deleted.")).toBeVisible();
  await page.goto("/login");
  await page.getByLabel("E-mail").fill(address);
  await page.getByLabel("Password").fill(e2e.password);
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page.getByText("The e-mail or the password is wrong.")).toBeVisible();
});

test("two-step sign-in is turned on with an authenticator code and then asked at sign-in", async ({ page }) => {
  const address = await signUp(page, "mfa");
  await page.goto("/account");
  await page.getByRole("button", { name: "Set up" }).click();
  const key = (await page.locator(".copy code").first().textContent())!.trim();
  const secret = fromBase32(key);
  await page.getByLabel(/Code from your authenticator app/).first().fill(totp(secret, -1));
  await page.getByRole("button", { name: "Turn on" }).click();
  await expect(page.getByText(/They are shown only now/)).toBeVisible();
  const recovery = (await page.locator(".recovery code").first().textContent())!.trim();

  await page.getByRole("button", { name: "Sign out" }).click();
  await expect(page.getByRole("heading", { name: "Sign in" })).toBeVisible();
  await page.getByLabel("E-mail").fill(address);
  await page.getByLabel("Password").fill(e2e.password);
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page.getByRole("heading", { name: "Two-step sign-in" })).toBeVisible();
  await page.getByLabel(/Code from your authenticator app/).fill(totp(secret));
  await page.getByRole("button", { name: "Verify" }).click();
  // Signed in again, back on the page the sign-out left (the account page).
  await expect(page.getByRole("heading", { name: "Account", exact: true })).toBeVisible();

  // A recovery code works once, in place of the app.
  await page.getByRole("button", { name: "Sign out" }).click();
  await expect(page.getByRole("heading", { name: "Sign in" })).toBeVisible();
  await page.getByLabel("E-mail").fill(address);
  await page.getByLabel("Password").fill(e2e.password);
  await page.getByRole("button", { name: "Sign in" }).click();
  await page.getByLabel(/Code from your authenticator app/).fill(recovery);
  await page.getByRole("button", { name: "Verify" }).click();
  await expect(page.getByRole("heading", { name: "Account", exact: true })).toBeVisible();
});
