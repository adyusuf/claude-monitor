import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { dictionaries, I18nProvider } from "../i18n";
import { ExternalBadge } from "./ExternalBadge";

const show = (externalKey: string | null, externalUrl: string | null) => render(<I18nProvider initial="en"><ExternalBadge externalKey={externalKey} externalUrl={externalUrl} /></I18nProvider>);

describe("the key of a mirrored issue", () => {
  it("links an https address that opens in a new tab without giving the page away", () => {
    show("SHOP-12", "https://acme.atlassian.net/browse/SHOP-12");
    const link = screen.getByRole("link", { name: "SHOP-12 ↗" });
    expect(link).toHaveAttribute("href", "https://acme.atlassian.net/browse/SHOP-12");
    expect(link).toHaveAttribute("target", "_blank");
    expect(link).toHaveAttribute("rel", "noopener noreferrer");
    expect(link).toHaveAttribute("title", "Open in Jira");
    expect(link).toHaveClass("badge");
  });

  it.each([
    ["a javascript: address", "javascript:alert(1)"],
    ["a plain http address", "http://acme.atlassian.net/browse/SHOP-12"],
    ["a data: address", "data:text/html,<script>1</script>"],
    ["an upper-case scheme the browser would still run", "JAVASCRIPT:alert(1)"],
    ["a relative address", "/browse/SHOP-12"],
    ["an address that only mentions https", "x-https://acme.atlassian.net"],
    ["an empty address", ""],
    ["no address", null],
  ])("shows the key as plain text, never as a link, for %s", (_name, url) => {
    const { container } = show("SHOP-12", url);
    expect(screen.queryByRole("link")).not.toBeInTheDocument();
    expect(container.querySelector("a")).toBeNull();
    const text = screen.getByText("SHOP-12");
    expect(text.tagName).toBe("SPAN");
    expect(text).toHaveClass("badge");
    expect(text).toHaveAttribute("title", "From Jira");
    expect(text).not.toHaveTextContent("↗");
  });

  it("shows nothing without a key, even when there is an address", () => {
    const { container } = show(null, "https://acme.atlassian.net/browse/SHOP-12");
    expect(container).toBeEmptyDOMElement();
  });

  it("shows nothing for an empty key", () => {
    const { container } = show("", "https://acme.atlassian.net/browse/SHOP-12");
    expect(container).toBeEmptyDOMElement();
  });

  it.each(["tr", "de"] as const)("words the titles in %s when the page is in that language", (language) => {
    const dict = dictionaries[language].integrations;
    const { unmount } = render(<I18nProvider initial={language}><ExternalBadge externalKey="SHOP-1" externalUrl="https://x.org/a" /></I18nProvider>);
    expect(screen.getByRole("link")).toHaveAttribute("title", dict.openInJira);
    unmount();
    render(<I18nProvider initial={language}><ExternalBadge externalKey="SHOP-1" externalUrl="http://x.org/a" /></I18nProvider>);
    expect(screen.getByText("SHOP-1")).toHaveAttribute("title", dict.fromJira);
    expect(dict.openInJira).not.toBe(dictionaries.en.integrations.openInJira);
  });
});
