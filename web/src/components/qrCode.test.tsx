import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { QrCode } from "./QrCode";

const pathOf = (container: HTMLElement) => container.querySelector("path")!.getAttribute("d")!;

describe("the QR code", () => {
  it("is an image with the given label, drawn as a square of dark modules on a white card", () => {
    const { container } = render(<QrCode text="otpauth://totp/Test:a?secret=ABC&issuer=Test" label="scan me" />);
    const svg = screen.getByRole("img", { name: "scan me" });
    const [, , w, h] = svg.getAttribute("viewBox")!.split(" ").map(Number);
    expect(w).toBeGreaterThan(20); // at least a version 1 symbol (21 modules) plus its quiet zone
    expect(w).toBe(h);
    expect(container.querySelector("rect")!.getAttribute("fill")).toBe("#ffffff");
    expect(pathOf(container).startsWith("M")).toBe(true);
    expect(container.querySelector("path")!.getAttribute("fill")).toBe("#000000");
  });

  it("draws the same text the same way and different text differently", () => {
    const a = render(<QrCode text="otpauth://totp/x?secret=AAAA" label="a" />);
    const again = render(<QrCode text="otpauth://totp/x?secret=AAAA" label="a" />);
    const other = render(<QrCode text="otpauth://totp/x?secret=BBBB" label="a" />);
    expect(pathOf(again.container)).toBe(pathOf(a.container));
    expect(pathOf(other.container)).not.toBe(pathOf(a.container));
  });

  it("grows for longer text and puts the text nowhere in the page", () => {
    const short = render(<QrCode text="otpauth://totp/x?secret=A" label="s" />);
    const longText = `otpauth://totp/x?secret=${"A".repeat(200)}`;
    const long = render(<QrCode text={longText} label="l" />);
    const side = (c: HTMLElement) => Number(c.querySelector("svg")!.getAttribute("viewBox")!.split(" ")[2]);
    expect(side(long.container)).toBeGreaterThan(side(short.container));
    expect(long.container.innerHTML).not.toContain("AAAAAAAAAA");
  });
});
